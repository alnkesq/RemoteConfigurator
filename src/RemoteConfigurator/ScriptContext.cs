using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteConfigurator;

public class ScriptContext
{
    private Dictionary<string, string> variables = new(StringComparer.OrdinalIgnoreCase);
    private Machine? machine;
    private HashSet<string>? alreadyExecutedCommands;
    private StreamWriter? alreadyExecutedCommandsFile;
    public string[] ForceRestartTmuxSessions = [];
    public string[] KillTmuxSessions = [];
    public string[] ForceKillTmuxSessions = [];
    public string? PrintTmuxSession;
    public string? KillProcess;
    public bool KillProcessSigKill;
    public bool SkipTmux;
    public string? InlineTmux;
    private List<Dictionary<string, string>> scopes = new();
    public bool ListTmuxSessions;

    public string GetVariableMandatory(string name) => GetVariableNormalized(name) is { } s ? s : throw new Exception("Missing variable " + name);
    public string? GetVariable(string name)
    {
        for (int i = scopes.Count - 1; i >= 0; i--)
        {
            if (scopes[i].TryGetValue(name, out var vv))
                return vv;
        }
        return variables.TryGetValue(name, out var v) ? v : null;
    }

    public string? GetVariableNormalized(string name) => GetVariable(name) is { } s && !string.IsNullOrEmpty(s) ? s : null;

    private const string VariableIp = "IP";
    private const string VariableUserName = "USER";
    private const string VariableVersion = "VERSION";

    private const string VariableUserNameOrRoot = "USER_OR_ROOT";
    private const string VariablePwd = "PWD";
    private const string VariableHome = "HOME";
    private const string VariableRootHome = "ROOT_HOME";
    private const string VariableLocalPath = "LOCAL_PATH";
    private const string VariableWindows = "WINDOWS";
    public Machine GetMachine()
    {
        if (machine != null) return machine;

        var ip = GetVariableMandatory(VariableIp);

        if (alreadyExecutedCommands == null && ExportShBuilder == null)
        {
            var path = Path.Combine(Environment.GetEnvironmentVariable("ALNKESQ_REMOTE_CONFIGURATOR_ALREADY_EXECUTED_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alnkesq", "RemoteConfigurator", "AlreadyExecuted"), (IsLocal() ? Environment.GetEnvironmentVariable(IsWindows() ? "ComputerName" : "HOSTNAME") : ip) + ".txt");
            if (File.Exists(path))
                alreadyExecutedCommands = File.ReadAllLines(path).Where(x => !string.IsNullOrEmpty(x)).ToHashSet();
            else
                alreadyExecutedCommands = new();

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            alreadyExecutedCommandsFile = new StreamWriter(path, Encoding.UTF8, new FileStreamOptions { Share = FileShare.Read | FileShare.Delete, Access = FileAccess.Write, Mode = FileMode.Append })
            {
                AutoFlush = true
            };
        }
        machine = new Machine(ip, GetVariableNormalized(VariableUserName));
        return machine;
    }

    public async Task RunAsync(string path)
    {
        path = Path.GetFullPath(path);
        UpdateAutomaticVariables();
        var s = ParseScript(File.ReadAllLines(path));

        scopes.Add(new(StringComparer.OrdinalIgnoreCase));
        ScriptLocationStack.Add(path);
        try
        {
            foreach (var line in s)
            {
                await ExecuteAsync(path, line);
            }
        }
        finally
        {
            ScriptLocationStack.RemoveAt(ScriptLocationStack.Count - 1);
            scopes.RemoveAt(scopes.Count - 1);
        }



    }
    public List<string> ScriptLocationStack = new();
    internal StringBuilder? ExportShBuilder;
    private string? ExportShCurrentDirectory;

    private async Task ExecuteAsync(string currentScriptPath, ScriptLine line, CancellationToken ct = default)
    {
        var rootArgs = line.Arguments;

        if (TryConsume(rootArgs, "IfDef", out var inner))
        {
            var val = GetVariableNormalized(inner[0]);
            if (val == null) return;
            rootArgs = inner.Skip(1).ToArray();
        }
        if (TryConsume(rootArgs, "IfNotDef", out var inner2))
        {
            var val = GetVariableNormalized(inner2[0]);
            if (val != null) return;
            rootArgs = inner2.Skip(1).ToArray();
        }
        rootArgs = rootArgs.Select(x => ExpandVariables(x)).ToArray();
        //Console.Error.WriteLine("Executing: " + string.Join(" ", rootArgs));


        if (TryConsume(rootArgs, "Abort", out _))
        {
            Console.Error.WriteLine("Encountered ABORT command.");
            Environment.Exit(1);
        }
        if (TryConsume(rootArgs, "SetVersion", out var ver))
        {
            SetVariable(VariableVersion, ver[0], local: true);
            return;
        }
        if (TryConsume(rootArgs, "Set", out var setArgs) || TryConsume(rootArgs, "SetLocal", out setArgs))
        {
            var isLocal = rootArgs[0].Equals("SetLocal", StringComparison.OrdinalIgnoreCase);
            var argname = setArgs[0];
            if (argname is VariableIp or VariableUserName)
                this.machine = null;
            if (argname is VariableUserName or VariableWindows)
            {
                SetVariable(VariablePwd, null, local: isLocal);
            }
            var val = setArgs[1];
            if (argname is VariableIp && GetVariableNormalized(VariableIp) is { } oldIp && oldIp != null && oldIp != val) throw new ArgumentException("Variable IP can be set only once.");

            SetVariable(argname, val, local: isLocal);
            UpdateAutomaticVariables();
            return;
        }
        if (TryConsume(rootArgs, "cd", out var cdpath))
        {
            var cd = cdpath.ElementAtOrDefault(0);
            if (string.IsNullOrEmpty(cd)) SetVariable(VariablePwd, GetVariableMandatory(VariableHome));
            else
            {
                cd = GetFullRemotePath(cd);
                SetVariable(VariablePwd, cd);
            }
            return;
        }

        if (rootArgs[0].EndsWith(".sshrecipe", StringComparison.Ordinal))
        {
            await RunAsync(Path.Combine(Path.GetDirectoryName(currentScriptPath)!, rootArgs[0]));
            return;
        }

        var machine = GetMachine();
        var scriptVersion = GetVariableNormalized(VariableVersion);
        var bump = line.Bump;
        if (scriptVersion != null)
            bump += "+" + scriptVersion;
        string?[] commandKey = [machine.UserName, GetVariableMandatory(VariablePwd), bump, line.Key, .. (line.Key == null ? rootArgs : [])];


        var sudo = false;
        if (TryConsume(rootArgs, "sudo", out var sudoed))
        {
            sudo = true;
            rootArgs = sudoed;
        }

        if (rootArgs[0].Equals("Upload", StringComparison.OrdinalIgnoreCase) || rootArgs[0].Equals("UploadAs", StringComparison.OrdinalIgnoreCase))
        {
            var scriptPath = Path.Combine(Path.GetDirectoryName(currentScriptPath)!, rootArgs[1]);
            commandKey = [.. commandKey, File.GetLastWriteTimeUtc(scriptPath).ToString("o")];
        }

        var commandKeyStr = string.Join("\t", commandKey).Replace("\r", null).Replace("\n", " ");
        if (rootArgs[0].Equals("LaunchTmux", StringComparison.OrdinalIgnoreCase)) commandKeyStr = null;
        if (commandKeyStr != null && alreadyExecutedCommands != null && alreadyExecutedCommands!.Contains(commandKeyStr)) return;



        if (TryConsume(rootArgs, "Download", out var dloadargs))
        {
            rootArgs = ["curl", "-fsSL", dloadargs[0], "-o", dloadargs[1]]; //, "--http1.1"]; // http1.1 otherwise doesn't work on raspberrypi
        }
        if (TryConsume(rootArgs, "CreateSymLink", out var symlinkargs))
        {
            sudo = true;
            rootArgs = ["ln", "-f", "-s", GetFullRemotePath(symlinkargs[0]), GetFullRemotePath(symlinkargs[1])];
        }
        if (TryConsume(rootArgs, "WriteFile", out var writeFileArgs))
        {
            rootArgs = ["sh", "-c", @"  echo ""$2"" > ""$1"" ", "_", writeFileArgs[0], writeFileArgs[1]];
        }
        if (TryConsume(rootArgs, "AppendFile", out var appendFileArgs))
        {
            rootArgs = ["sh", "-c", @"  echo ""$2"" >> ""$1"" ", "_", appendFileArgs[0], appendFileArgs[1]];
        }
        if (TryConsume(rootArgs, "ReplaceInPlace", out var replaceInPlaceArgs))
        {
            rootArgs = ["sed", "-i", "s/" + Regex.Escape(replaceInPlaceArgs[1]) + "/" + replaceInPlaceArgs[2].Replace("\\", "\\\\") + "/g", replaceInPlaceArgs[0]];
        }
        if (TryConsume(rootArgs, "Sleep", out var sleepAmount))
        {
            await Task.Delay(int.Parse(sleepAmount[0]) * 1000, ct);
        }
        else if (TryConsume(rootArgs, "AwaitReconnect", out _))
        {
            while (true)
            {
                Console.Error.WriteLine("Awaiting reconnection...");
                try
                {
                    var r = await GetMachine().TryRunAsync(null, ["echo", "1"], sudo: false, ct);
                    if (r.ExitCode != 0) throw new Exception(r.ToString());
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(30000, ct);
                }
            }
        }
        else
        {
            int[] allowExitCodes = [];
            if (TryConsume(rootArgs, "AllowExitCode", out var allow))
            {
                allowExitCodes = allow[0].Split(",").Select(x => int.Parse(x)).ToArray();
                rootArgs = allow.Skip(1).ToArray();

            }


            if (TryConsume(rootArgs, "LaunchTmux", out var tmuxargs))
            {

                var name = tmuxargs[0];
                var forceRestart = ForceRestartTmuxSessions.Contains(name) || (InlineTmux == name);
                if (forceRestart)
                {
                    await KillTmuxSession(machine, name, ct);
                }
                if (SkipTmux && !forceRestart) return;
                if (KillTmuxSessions.Contains(name)) return;

                var workdir = GetFullRemotePath(tmuxargs[1]);

                string[] commandAndArgs;


                if (tmuxargs[2].EndsWith(".ps1", StringComparison.Ordinal))
                    commandAndArgs = ["pwsh",
                    //"-NoExit", "-Interactive", 
                    "-EncodedCommand", EncodePowershellCommand("cd \"" + workdir + "\"; " + string.Join(" ", tmuxargs.Skip(2).Select(x => MaybeQuoteForPowershell(x)).ToArray()))];
                else
                    commandAndArgs = ["env", "-C", workdir, .. (tmuxargs.Skip(2))];

                if (InlineTmux == name)
                {
                    rootArgs = commandAndArgs;
                }
                else
                {
                    if (GetVariableNormalized("TMUX_LOG_TAIL") == "1")
                    {
                        commandAndArgs = [GetVariableMandatory("HOME") + "/log-tail.sh", name, .. commandAndArgs];
                    }
                    rootArgs = ["tmux", "new-session", "-d", "-s", name, .. commandAndArgs];
                    allowExitCodes = [1];
                }


                if (InlineTmux == null && !Debugger.IsAttached)
                    ct = CancellationTokenSource.CreateLinkedTokenSource(ct, new CancellationTokenSource(5000).Token).Token;
            }


            async Task UploadCoreAsync(string sourcePath, string dest, bool copyAs, string[] extraArgs)
            {
                if (ExportShBuilder != null)
                {
                    ExportShBuilder.AppendLineUnix("# Omitted upload: " + sourcePath + " to " + dest);
                    return;
                }
                if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath is "/" or "\\") throw new ArgumentException();
                sourcePath = Path.Combine(Path.GetDirectoryName(currentScriptPath)!, sourcePath);
                var destPath = GetFullRemotePath(dest);
                Console.Error.WriteLine("Uploading " + sourcePath + " to " + destPath);
                await machine.UploadAsync(sourcePath, destPath, extraArgs, sudo ? "root" : GetVariableMandatory(VariableUserNameOrRoot), copyAs: copyAs, ct: ct);
                if (sourcePath.EndsWith(".sh", StringComparison.Ordinal))
                {
                    if (!copyAs) destPath = destPath + "/" + Path.GetFileName(sourcePath);
                    var r = await machine.TryRunAsync(null, ["chmod", "+x", destPath], sudo, ct);
                    if (r.ExitCode != 0)
                        throw new Exception("chmod: " + r);
                }
            }

            if (TryConsume(rootArgs, "Upload", out var uploadArgs))
            {
                await UploadCoreAsync(uploadArgs[0], uploadArgs[1], copyAs: false, uploadArgs.Skip(2).ToArray());
            }
            else if (TryConsume(rootArgs, "UploadAs", out var uploadAsArgs))
            {
                await UploadCoreAsync(uploadAsArgs[0], uploadAsArgs[1], copyAs: true, uploadAsArgs.Skip(2).ToArray());
            }
            else
            {
                var path = rootArgs[0];

                if (path.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
                {
                    rootArgs = ["reg", "import", .. rootArgs];
                }
                else if (path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    rootArgs = ["pwsh", "-ExecutionPolicy", "Unrestricted", "-NoProfile", path.AsSpan().ContainsAny('/', '\\') ? path : "./" + path, .. rootArgs.Skip(1)];
                }
                else if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    rootArgs = ["cmd", "/c", .. rootArgs];
                }

                // TODO: winget install --accept-package-agreements --accept-source-agreements?
                // TODO: Refresh environment after installing winget

                var pwd = GetVariable(VariablePwd);


                if (ExportShBuilder != null)
                {
                    if (pwd != null && ExportShCurrentDirectory != pwd)
                    {
                        ExportShBuilder.Append("cd ");
                        ExportShBuilder.AppendBashEscaped(pwd!);
                        ExportShBuilder.Append('\n');
                        ExportShCurrentDirectory = pwd;
                    }
                    if (allowExitCodes.Length != 0) ExportShBuilder.AppendLineUnix("set +e");
                    for (int i = 0; i < rootArgs.Length; i++)
                    {
                        if (i != 0) ExportShBuilder.Append(' ');
                        var arg = rootArgs[i];
                        ExportShBuilder.AppendBashEscaped(arg);
                    }
                    ExportShBuilder.Append('\n');
                    if (allowExitCodes.Length != 0) ExportShBuilder.AppendLineUnix("set -e");
                    // TODO: check if exit code is one of the allowed ones instead of allowing everything

                }
                else
                {
                    if (!sudo && !IsLocal() && pwd == GetVariableNormalized(VariableHome)) pwd = null;

                    var r = await machine.TryRunAsync(pwd, rootArgs, sudo: sudo, ct);
                    if (r.ExitCode != 0)
                    {
                        if (path == "winget" && r.ExitCode == -1978335189)
                        {
                            // ok, Already installed and no updates available
                        }
                        else if (!allowExitCodes.Contains(r.ExitCode))
                        {
                            throw new Exception(r.ToString());
                        }
                    }
                }
            }
        }



        if (commandKeyStr != null && alreadyExecutedCommands != null)
        {
            alreadyExecutedCommands!.Add(commandKeyStr);
            alreadyExecutedCommandsFile!.WriteLine(commandKeyStr);
        }
    }


    private async Task KillTmuxSession(Machine machine, string name, CancellationToken ct)
    {
        if (ForceKillTmuxSessions.Contains(name))
        {
            var c = await machine.TryRunAsync(null, ["tmux", "kill-session", "-t", name], false, ct);
            if (c.ExitCode != 0 && (c.ExitCode != 1 && !c.StdErr.StartsWith("can't find session", StringComparison.Ordinal)))
                throw new Exception("tmux kill-session: " + c.ToString());
            return;
        }
        else
        {
            var c = await machine.TryRunAsync(null, ["tmux", "send-keys", "-t", name, "C-c"], false, ct);


            if (c.ExitCode == 1 && c.StdErr.StartsWith("can't find pane", StringComparison.Ordinal)) return;
            if (c.ExitCode != 0) throw new Exception("tmux kill-session: " + c.ToString());

            while (true)
            {
                var z = await machine.TryRunAsync(null, ["tmux", "has-session", "-t", name], false, ct);
                if (z.ExitCode == 1) return;
                if (z.ExitCode != 0) throw new Exception("tmux has-session: " + z.ToString());
                Console.Error.WriteLine("  Waiting for session " + name + "to exit...");
                await Task.Delay(500, ct);
            }
        }
    }

    private static string MaybeQuoteForPowershell(string x)
    {
        if (x.Contains('"') || x.Contains(' ')) return $@"""{x.Replace("\"", "\"\"")}""";
        return x;
    }

    private static string EncodePowershellCommand(string script)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(script);
        return Convert.ToBase64String(bytes);
    }
    private string GetFullRemotePath(string cd)
    {
        var oldPwd = GetVariableMandatory(VariablePwd);
        if (!IsWindows() && !cd.StartsWith('/')) cd = Path.Combine(oldPwd, cd).Replace('\\', '/');
        return NormalizePathTraversal(cd);
    }

    private string NormalizePathTraversal(string path)
    {
        if (IsWindows())
        {
            if (path.Length < 3) throw new ArgumentException();
            if (path[1] != ':' || path[2] != '\\') throw new ArgumentException();
        }
        else
        {
            if (!path.StartsWith('/')) throw new ArgumentException();
        }
        var p = new List<string>();

        foreach (var part in path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (p.Count == 0) throw new ArgumentException("Too many ..s in " + path);
                p.RemoveAt(p.Count - 1);
                continue;
            }
            p.Add(part);
        }

        if (IsWindows())
        {
            if (p.Count == 0) throw new ArgumentException("Cannot navigate above drive root: " + path);
            if (p.Count == 1) return p[0] + "\\";
            return string.Join("\\", p);
        }

        var pp = "/" + string.Join("/", p);
        if (Path.EndsInDirectorySeparator(path) && !Path.EndsInDirectorySeparator(pp))
            pp += "/";
        return pp;
    }

    private void UpdateAutomaticVariables()
    {
        if (IsWindows())
        {
            var username = GetVariableNormalized(VariableUserName);
            string? home = null;
            if (username == null && IsLocal())
            {
                username = Environment.UserName;
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (username != null)
            {
                home ??= Path.Combine("C:\\Users", username);
                SetVariable(VariableHome, home);
                SetVariable(VariableRootHome, home);
                SetVariable(VariableUserNameOrRoot, username);
            }
        }
        else
        {
            SetVariable(VariableHome, GetVariableNormalized(VariableUserName) is { } s ? "/home/" + s : "/root");
            SetVariable(VariableRootHome, "/root");
            SetVariable(VariableUserNameOrRoot, GetVariableNormalized(VariableUserName) ?? "root");
        }
        if (GetVariableNormalized(VariablePwd) == null)
            SetVariable(VariablePwd, GetVariable(VariableHome)!);

        SetVariable(VariableLocalPath, IsLocal() && this.ScriptLocationStack.LastOrDefault() is { } scriptPath ? Path.GetDirectoryName(scriptPath) : null);

    }

    public bool IsWindows() => GetVariableNormalized(VariableWindows) == "1";
    public bool IsLocal() => GetVariableNormalized(VariableIp) == ".";

    private void SetVariable(string name, string? value, bool local = false)
    {
        var v = local ? scopes.Last() : variables;

        if (value == null) v.Remove(name);
        else v[name] = value;
    }

    private string ExpandVariables(string s)
    {
        return Regex.Replace(s, @"%(\w+)%", x =>
        {
            var name = x.Groups[1].Value;
            var v = GetVariableNormalized(name);
            if (v == null) throw new ArgumentException("Variable is null or empty: " + name);
            return v;
        });
    }

    private static bool TryConsume(IReadOnlyList<string> inargs, string command, [NotNullWhen(true)] out string[]? subArgs)
    {
        if (inargs[0].Equals(command, StringComparison.OrdinalIgnoreCase))
        {
            subArgs = inargs.Skip(1).ToArray();
            return true;
        }
        subArgs = null;
        return false;
    }

    private static IReadOnlyList<ScriptLine> ParseScript(string[] lines)
    {
        var list = new List<ScriptLine>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            string? key = null;
            string? bump = null;
            var meta = Regex.Match(line, @"^(.*)\s+\[([^\[\]]*)\]$");
            if (meta.Success)
            {
                line = meta.Groups[1].Value.Trim();
                var metaArr = meta.Groups[2].Value.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();

                key = metaArr.FirstOrDefault(x => !int.TryParse(x, out _));
                bump = metaArr.FirstOrDefault(x => int.TryParse(x, out _));
            }

            var setvar = Regex.Match(line, @"^(\w+)=(.*)$");
            if (setvar.Success)
            {
                list.Add(new ScriptLine { Arguments = ["Set", setvar.Groups[1].Value, setvar.Groups[2].Value] });
            }
            else
            {
                var r = new List<string>();
                ParseArgumentsIntoList(line, r);
                list.Add(new ScriptLine { Arguments = r.ToArray(), Key = key, Bump = bump });
            }
        }
        return list;
    }




    // https://github.com/dotnet/runtime/blob/f944a7771ddf9bf194992c7c6b4f8927992807e5/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Unix.cs


    /// <summary>Parses a command-line argument string into a list of arguments.</summary>
    /// <param name="arguments">The argument string.</param>
    /// <param name="results">The list into which the component arguments should be stored.</param>
    /// <remarks>
    /// This follows the rules outlined in "Parsing C++ Command-Line Arguments" at
    /// https://msdn.microsoft.com/en-us/library/17w5ykft.aspx.
    /// </remarks>
    private static void ParseArgumentsIntoList(string arguments, List<string> results)
    {
        // Iterate through all of the characters in the argument string.
        for (int i = 0; i < arguments.Length; i++)
        {
            while (i < arguments.Length && (arguments[i] == ' ' || arguments[i] == '\t'))
                i++;

            if (i == arguments.Length)
                break;

            results.Add(GetNextArgument(arguments, ref i));
        }
    }


    private static string GetNextArgument(string arguments, ref int i)
    {
        var currentArgument = new StringBuilder();
        bool inQuotes = false;

        while (i < arguments.Length)
        {
            // From the current position, iterate through contiguous backslashes.
            int backslashCount = 0;
            while (i < arguments.Length && arguments[i] == '\\')
            {
                i++;
                backslashCount++;
            }

            if (backslashCount > 0)
            {
                if (i >= arguments.Length || arguments[i] != '"')
                {
                    // Backslashes not followed by a double quote:
                    // they should all be treated as literal backslashes.
                    currentArgument.Append('\\', backslashCount);
                }
                else
                {
                    // Backslashes followed by a double quote:
                    // - Output a literal slash for each complete pair of slashes
                    // - If one remains, use it to make the subsequent quote a literal.
                    currentArgument.Append('\\', backslashCount / 2);
                    if (backslashCount % 2 != 0)
                    {
                        currentArgument.Append('"');
                        i++;
                    }
                }

                continue;
            }

            char c = arguments[i];

            // If this is a double quote, track whether we're inside of quotes or not.
            // Anything within quotes will be treated as a single argument, even if
            // it contains spaces.
            if (c == '"')
            {
                if (inQuotes && i < arguments.Length - 1 && arguments[i + 1] == '"')
                {
                    // Two consecutive double quotes inside an inQuotes region should result in a literal double quote
                    // (the parser is left in the inQuotes region).
                    // This behavior is not part of the spec of code:ParseArgumentsIntoList, but is compatible with CRT
                    // and .NET Framework.
                    currentArgument.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                i++;
                continue;
            }

            // If this is a space/tab and we're not in quotes, we're done with the current
            // argument, it should be added to the results and then reset for the next one.
            if ((c == ' ' || c == '\t') && !inQuotes)
            {
                break;
            }

            // Nothing special; add the character to the current argument.
            currentArgument.Append(c);
            i++;
        }

        return currentArgument.ToString();
    }

    internal static async Task RunNewAsync(string path)
    {
        var ctx = new ScriptContext();
        await ctx.RunAsync(path);
    }

    internal async Task MaybeRunCustomTmuxActionsAsync()
    {
        if (KillProcess != null)
        {
            var r = await machine!.TryRunAsync(null, ["pkill", KillProcessSigKill ? "-SIGKILL" : "-SIGINT", KillProcess], true, CancellationToken.None);
            if (r.ExitCode == 1)
            {
                Console.Error.WriteLine("No process was found matching the provided name.");
            }
            else if (r.ExitCode == 0)
            {
                Console.Error.WriteLine("Process " + KillProcess + " killed.");
            }
            else throw new Exception("pkill: " + r.ToString());
        }
        foreach (var name in KillTmuxSessions)
        {
            await KillTmuxSession(machine!, name, CancellationToken.None);
        }
        if (ListTmuxSessions)
        {
            var r = await machine!.TryRunAsync(null, ["tmux", "ls"], false, CancellationToken.None);
            if (r.ExitCode != 0) throw new Exception("tmux ls: " + r.ToString());
        }
        if (PrintTmuxSession != null)
        {
            var r = await machine!.TryRunAsync(null, ["tmux", "capture-pane", "-p", "-S", "-100", "-t", PrintTmuxSession], false, CancellationToken.None);
            if (r.ExitCode != 0) throw new Exception("tmux capture-pane: " + r.ToString());
        }
    }
}

public record ScriptLine
{
    //public string? SetVariableName;
    //public string? SetVariableValue;
    public string[] Arguments;
    public string? Key;
    public string? Bump;
}
