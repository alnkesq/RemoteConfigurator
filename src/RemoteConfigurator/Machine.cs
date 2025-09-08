using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteConfigurator;

public class Machine
{
    public readonly string Ip;
    public readonly string? UserName;
    public Machine(string ip, string? username)
    {
        ArgumentException.ThrowIfNullOrEmpty(ip);
        this.Ip = ip;
        this.UserName = username;
    }


    public async Task<CommandResult> TryRunAsync(string? cd, string[] args, bool sudo, CancellationToken ct)
    {
        Console.Error.WriteLine("Running: " + string.Join(", ", args));
        if (IsLocal())
        {
            var psi = new ProcessStartInfo(args[0], args.Skip(1));
            if (cd != null)
                psi.WorkingDirectory = cd;
            return await TryRunCoreAsync(psi, ct);
        }
        else
        {
            var psi = new ProcessStartInfo("ssh");
            var sshUserName = sudo || UserName == null ? "root" : UserName;
            psi.ArgumentList.Add(sshUserName + "@" + Ip);
            var shellArgs = new List<string>();
            if (cd != null)
            {
                shellArgs.Add("env");
                shellArgs.Add("-C");
                shellArgs.Add(cd);
            }
            if (args != null)
            {
                foreach (var arg in args)
                {
                    shellArgs.Add(arg);
                }
            }
            var sb = new StringBuilder();
            foreach (var item in shellArgs)
            {
                sb.AppendBashEscaped(item);
                sb.Append(' ');
            }
            var shellArg = sb.ToString();
            // ssh passes everything as a single argument to the shell. We create that single argument instead of letting ssh do it incorrectly.
            // https://github.com/openssh/openssh-portable/blob/4f29309c4cb19bcb1774931db84cacc414f17d29/session.c#L1666
            psi.ArgumentList.Add(shellArg);
            return await TryRunCoreAsync(psi, ct);
        }
    }

    public bool IsLocal()
    {
        return Ip == ".";
    }

    private static async Task<CommandResult> TryRunCoreAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        psi.RedirectStandardError = true;
        psi.RedirectStandardOutput = true;
        using var p = Process.Start(psi)!;
        try
        {
            var consumeStdErrLine = p.StandardError.ReadLineAsync(ct).AsTask();
            var consumeStdOutLine = p.StandardOutput.ReadLineAsync(ct).AsTask();
            var stdErr = new StringBuilder();
            var stdOut = new StringBuilder();
            while (true)
            {
                if (consumeStdErrLine == null && consumeStdOutLine == null) break;

                var consumed = await Task.WhenAny(new[] { consumeStdErrLine, consumeStdOutLine }.Where(x => x != null)!);

                var line = await consumed;
                if (consumed == consumeStdErrLine)
                {
                    if (line != null)
                    {
                        stdErr.AppendLine(line);
                        Console.Error.WriteLine("    STDERR: " + line);
                        consumeStdErrLine = p.StandardError.ReadLineAsync(ct).AsTask();
                    }
                    else
                    {
                        consumeStdErrLine = null;
                    }
                }
                else
                {
                    if (line != null)
                    {
                        stdOut.AppendLine(line);
                        Console.Error.WriteLine("    STDOUT: " + line);
                        consumeStdOutLine = p.StandardOutput.ReadLineAsync(ct).AsTask();
                    }
                    else
                    {
                        consumeStdOutLine = null;
                    }
                }

            }
            await p.WaitForExitAsync(ct);
            return new CommandResult(p.ExitCode, stdOut.ToString(), stdErr.ToString());
        }
        finally
        {
            p.Kill();
        }
    }

    public async Task UploadAsync(string source, string destination, string[] extraRcloneArgs, string username, bool copyAs, CancellationToken ct = default)
    {
        var keyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
        if (!File.Exists(keyFile))
            throw new Exception("Could not upload file, SSH private key not found at " + keyFile);
        var tempConfig = $"""
            [remotemachine]
            type = sftp
            host = {Ip}
            user = {username}
            port = 22
            key_file = {keyFile}
            use_insecure_cipher = false
            md5sum_command = md5sum
            sha1sum_command = sha1sum
            shell_type = unix
            """;
        var tempConfigBytes = Encoding.UTF8.GetBytes(tempConfig);
        var configPath = Path.Combine(Path.GetTempPath(), "remote-configurator-rclone-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(tempConfigBytes)) + ".conf");


        var psi = new ProcessStartInfo("rclone");
        psi.ArgumentList.Add(copyAs ? "copyto" : "copy");
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add("remotemachine:" + destination);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(configPath);
        foreach (var item in extraRcloneArgs)
        {
            psi.ArgumentList.Add(item);
        }
        File.WriteAllBytes(configPath, tempConfigBytes);
        try
        {
            var r = await TryRunCoreAsync(psi, ct);
            if (r.ExitCode != 0) throw new Exception("RClone exit code: " + r.ExitCode);
        }
        finally
        {
            File.Delete(configPath);
        }


    }
}

public record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public override string ToString()
    {
        return ($"Exit code {ExitCode}.\n{StdErr}\n{StdOut}").Trim();
    }
}

internal static class ExtensionMethods
{
    public static void AppendLineUnix(this StringBuilder sb, string t)
    {
        sb.Append(t);
        sb.Append('\n');
    }

    public static void AppendBashEscaped(this StringBuilder sb, string arg)
    {
        if (arg.AsSpan().ContainsAny(@"$ '\"))
        {
            sb.Append('"');
            for (int j = 0; j < arg.Length; j++)
            {
                var ch = arg[j];
                if (ch is '"' or '\\' or '$')
                {
                    sb.Append('\\');
                }
                sb.Append(ch);
            }
            sb.Append('"');
        }
        else
        {
            sb.Append(arg);
        }
    }

}