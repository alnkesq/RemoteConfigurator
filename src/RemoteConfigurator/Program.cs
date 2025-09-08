using System;
using System.Linq;
using System.Threading.Tasks;
namespace RemoteConfigurator;

internal class Program
{
    private static async Task Main(string[] args)
    {
        var path = args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal));


        string? GetArgByName(string name)
        {
            if (!name.StartsWith("--", StringComparison.Ordinal)) throw new Exception();
            var index = Array.IndexOf(args, name);
            if (index == -1) return null;

            if (index == args.Length - 1) throw new ArgumentException("Missing value for " + name);
            var r = args[index + 1];
            if (r.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Missing value for " + name);
            return r;
        }
        bool HasArg(string name)
        {
            if (!name.StartsWith("--", StringComparison.Ordinal)) throw new Exception();
            var index = Array.IndexOf(args, name);
            return index != -1;
        }
        if (path != null)
        {
            var ctx = new ScriptContext();

            ctx.KillTmuxSessions = GetArgByName("--kill-tmux")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
            ctx.ForceKillTmuxSessions = GetArgByName("--force-kill-tmux")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
            ctx.KillTmuxSessions = ctx.KillTmuxSessions.Concat(ctx.ForceKillTmuxSessions).Distinct().ToArray();
            ctx.ForceRestartTmuxSessions = GetArgByName("--restart-tmux")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
            ctx.InlineTmux = GetArgByName("--inline-tmux");
            ctx.PrintTmuxSession = GetArgByName("--print-tmux");
            var exportShPath = GetArgByName("--export-sh");
            ctx.ExportShBuilder = exportShPath != null ? new("#!/usr/bin/env bash\n# Generated via RemoteConfigurator\nset -e\n") : null;

            if (GetArgByName("--quit-process") is { } quitProcess)
            {
                ctx.KillProcess = quitProcess;
            }
            else if (GetArgByName("--kill-process") is { } killProcess)
            {
                ctx.KillProcess = killProcess;
                ctx.KillProcessSigKill = true;
            }

            ctx.ListTmuxSessions = HasArg("--list-tmux");
            ctx.SkipTmux = HasArg("--skip-tmux") || (ctx.PrintTmuxSession != null || ctx.KillTmuxSessions.Any() || ctx.InlineTmux != null || ctx.KillProcess != null || ctx.ListTmuxSessions);

            await ctx.RunAsync(path);
            if (exportShPath != null)
            {
                System.IO.File.WriteAllText(exportShPath, ctx.ExportShBuilder!.ToString());
            }
            await ctx.MaybeRunCustomTmuxActionsAsync();

        }
        else throw new Exception("Missing argument.");

    }
}