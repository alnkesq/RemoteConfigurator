using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
namespace RemoteConfigurator;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            await MainCore(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
    private static async Task MainCore(string[] args)
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
            var exportTmuxPath = GetArgByName("--export-tmux");
            var exportDockerfilePath = GetArgByName("--export-dockerfile");
            var buildContainer = HasArg("--build-container");

            if (buildContainer && exportDockerfilePath == null)
            {
                var tempdir = Path.Combine(Path.GetTempPath(), "remoteconfigurator-container-temp-" + Guid.NewGuid());
                Directory.CreateDirectory(tempdir);
                exportDockerfilePath = tempdir;
            }
            if (exportDockerfilePath != null)
            {
                if (Directory.Exists(exportDockerfilePath))
                {
                    exportDockerfilePath = Path.Combine(exportDockerfilePath, "Dockerfile");
                }
            }

            ctx.ExportShBuilder = exportShPath != null ? new("#!/usr/bin/env bash\n# Generated via RemoteConfigurator\nset -e\n") : null;
            ctx.ExportTmuxBuilder = exportTmuxPath != null ? new("#!/usr/bin/env bash\n# Generated via RemoteConfigurator\n") : null;
            ctx.ExportDockerfileBuilder = exportDockerfilePath != null ? new() : null;
            ctx.ExportDockerfilePath = exportDockerfilePath;
            ctx.MainScriptPath = path;

            if (ctx.ExportShBuilder != null)
            {
                ctx.SetVariable("IS_SH_EXPORT", "1");
            }
            if (ctx.ExportDockerfileBuilder != null)
            {
                ctx.SetVariable("IS_DOCKER_EXPORT", "1");
            }

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
            ctx.SkipTmux = HasArg("--skip-tmux") || exportTmuxPath != null || ctx.PrintTmuxSession != null || ctx.KillTmuxSessions.Any() || ctx.InlineTmux != null || ctx.KillProcess != null || ctx.ListTmuxSessions;

            await ctx.RunAsync(path);
            if (exportShPath != null)
            {
                System.IO.File.WriteAllText(exportShPath, ctx.ExportShBuilder!.ToString());
            }
            if (exportTmuxPath != null)
            {
                System.IO.File.WriteAllText(exportTmuxPath, ctx.ExportTmuxBuilder!.ToString());
            }
            if (exportDockerfilePath != null)
            {
                var baseImage = ctx.GetVariable("DOCKER_BASE_IMAGE") ?? "debian:stable";
                System.IO.File.WriteAllText(exportDockerfilePath, "# Generated via RemoteConfigurator\nFROM " + baseImage + "\n\n" + ctx.ExportDockerfileBuilder!.ToString());
            }
            await ctx.MaybeRunCustomTmuxActionsAsync();

            if (buildContainer)
            {
                var imageName = Path.GetFileNameWithoutExtension(path);
                var psi = new ProcessStartInfo("podman", ["build", "-t", imageName, "-f", "./" + Path.GetFileName(exportDockerfilePath)!]);
                psi.WorkingDirectory = Path.GetDirectoryName(exportDockerfilePath);
                psi.UseShellExecute = false;
                using var proc = Process.Start(psi)!;
                await proc.WaitForExitAsync();
                if (proc.ExitCode != 0)
                    throw new Exception("podman build returned exit code " + proc.ExitCode);
                Console.WriteLine("Created image: " + imageName);
                Console.WriteLine("Use");
                Console.WriteLine("    podman run --rm -it " + imageName + " bash");
            }

        }
        else throw new Exception("Missing argument.");

    }
}