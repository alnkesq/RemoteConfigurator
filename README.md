# RemoteConfigurator

`RemoteConfigurator` sets up a local or remote machine (e.g. VPS or Raspberry Pi) via SSH or local execution.
It keeps track of which commands have already been executed, so that configuration changes don't require a full reexecution, similarly to a `Dockerfile` (but without containers).
The target system can be Linux or Windows.

## Example (Linux)

`raspberrypi.sshrecipe`:
```bash
IP=192.168.0.70
# Note: Comments can only start at the beginning of a line. If '#' is found later, it will be considered part of the arguments.

# For root-only systems, omit this line.
USER=pi
ARM=1

mkdir -p example-directory

# cd is handled internally by RemoteConfigurator (not by the remote target's Bash). It's always "executed" and doesn't require any network roundtrips.
cd example-directory

# Calls another .sshrecipe file. Path is relative to the current .sshrecipe.
minimal.sshrecipe

# Uploads a local file (path relative to .sshrecipe file) to the specified remote directory (path relative to the remote current directory). If extension is .sh, chmod +x is automatically executed.
# Will be re-uploaded automatically if the local file changed since the previous execution.
# Requires rclone on the local machine (not on the remote one). Requires a .ssh/id_ed25519 private key on the local machine.
Upload myscript.sh .

# Same as above, but the target is a remote file path rather than a remote directory path.
UploadAs myscript.sh destination.sh

# Launches a tmux session with the given name, working directory, and command (if a session with the same name doesn't already exist). Always runs, unless --skip-tmux is used.
LaunchTmux session-name . ./myscript.sh --var 1

# Sets a local variable with the given name. Always runs (local only, no network roundtrips). These are *not* environment variables.
Set EXAMPLE value

# Alternate syntax for Set
EXAMPLE=value

# Variables are usually global, but you can make them specific to the current script. Once the currently called script returns to the caller, the caller will see its intact previous variables.
SetLocal EXAMPLE value

# Use a local variable. Quoting is performed automatically if the value requires quoting. RemoteConfigurator uses Windows-style variable substitution (%name%), to clarify that expansion is done locally be RemoteConfigurator, not remotely by Bash.
./example.sh --arg %EXAMPLE%

# Built-in variables:
%PWD% # Current directory.
%IP% # The IP of the target machine. Use "." for local execution (without SSH).
%USER% # The user name that will be used for ssh user@IP. If not set, root@IP will be used.
%USER_OR_ROOT% # The user name, or "root" if %USER% is not set
%HOME% # The home directory, e.g. /home/user or /root
%ROOT_HOME% # Always /root
%LOCAL_PATH% # When running locally, the directory that contains the .sshrecipe file.
%VERSION% # The script-local SetVersion that was set at the beginning of a callable script.
%WINDOWS% # Whether the system runs Windows rather than Linux.

# Runs a command only if EXAMPLE is set (or not set).
IfDef    EXAMPLE ./example.sh
IfNotDef EXAMPLE ./example.sh

# Bump a number between square brackets to force a re-execution (e.g. you tweaked some environmental context that causes a re-execution to be necessary)
./example.sh [2]

# Already executed command are tracked by the tuple of (full command line, current directory, remote machine, bump version), but if this is not enough (e.g. the same identical command must be re-executed multiple times at different points of the script), then you can add a nickname to the command, which contributes to the identification tuple.
sudo apt-get clean [clean-after-libreoffice-removal, 2]

# Not the real sudo, simply instructs RemoteConfigurator to SSH as root for this instruction rather than the usual %USER%
# Note that %USER% and %HOME% are still those of the normal user, not root.
sudo apt update

# If "snap remove" returns exit code 127, then treat it as a success even if it's non-zero. Comma separated.
sudo AllowExitCode 127 snap remove thunderbird 

# Downloads the file at the specified URL to powershell.tar.gz
IfDef ARM Download https://github.com/PowerShell/PowerShell/releases/download/v7.4.5/powershell-7.4.5-linux-arm64.tar.gz powershell.tar.gz [download-powershell, 6]

# Shortcut for 'ln -f -s'
sudo CreateSymLink %HOME%/powershell/pwsh /bin/pwsh

# Appends a line to a file. Note that you can't use normal bash syntax or pipe redirections, unless you wrap it in bash -c "script" as a single argument.
AppendFile %HOME%/.bashrc "export POWERSHELL_TELEMETRY_OPTOUT=1"

# Similar to AppendFile, but erases previous file contents.
WriteFile example.txt "file contents"

# Replaces all occurences of "old" to "new" in file.txt
ReplaceInPlace file.txt old new

# Sleeps the specified amount of seconds
Sleep 3

# Waits for the SSH target to become available again (for example, if your previous command was a sudo reboot). In such case, to avoid race conditions with the shutdown, you might want to Sleep a few seconds anyways before the AwaitReconnect.
AwaitReconnect

# At the beginning of a callable script file, you can use SetVersion. All the commands in this script will be keyed by the specified string, so that if you bump that version, the whole callable script will be reexecuted.
Set DOTNET_FULL_VERSION 9.0.201
SetVersion %DOTNET_FULL_VERSION%

# Abruptly aborts the script. Useful for debugging purposes.
Abort

```

## Example (Windows)

`gaming.sshrecipe`:
```bash
# No SSH, RemoteConfigurator will target the machine it's running from.
IP=.
WINDOWS=1

# All subsequent commands will run from the directory that the .sshrecipe file resides in (otherwise the default would be "C:\Users\YourName" for consistency with SSH)
cd %LOCAL_PATH%

winget install Microsoft.VisualStudioCode --scope machine

# Applies a .reg file
MySettings.reg

# Calls a powershell script. Requires Microsoft Powershell 7+ (pwsh.exe).
myscript.ps1

# If you need to use the legacy Windows Powershell (powershell.exe), use instead:
powershell -NoProfile -ExecutionPolicy Unrestricted .\myscript.ps1

# Calls a cmd.exe script:
myscript.cmd

# Calls a binary executable
winget install Microsoft.VisualStudioCode --scope machine
```

Note: when running locally on Windows, you might want to launch `RemoteConfigurator` with elevated privileges (sudo is only relevant with SSH setups).

## Built-in scripts
See `examples/` for more examples and reusable scripts (`dotnet.sshrecipe`, `duckdb.sshrecipe`, etc.)

## Command line options

`RemoteConfigurator script.sshrecipe [options]`

* `--kill-tmux session-name`: Terminates (`CTRL+C`) the `tmux` session with the given name.
* `--force-kill-tmux session-name`: Kills the `tmux` session with the given name.
* `--restart-tmux session-name`: Terminates and then restarts the `tmux` session with the given name (as defined by `LaunchTmux`).
* `--inline-tmux session-name`: Runs the command specified by the corresponding `LaunchTmux`, but without actual `tmux` (prints real time stderr/stdout)
* `--print-tmux session-name`: Prints the last few stdout/stderr lines of the `tmux` session with the given name.
* `--export-sh destination.sh`: Converts the `.sshrecipe` script to a normal `.sh` file that can be run without RemoteConfigurator (without however incremental execution).
* `--quit-process procname`: Terminates (`CTRL+C`) the process with the given name.
* `--kill-process procname`: Kills the process with the given name.
* `--list-tmux`: Prints a list of `tmux` sessions running on the remote machine.
* `--skip-tmux a,b,c`: Skips launch of the specified sessions. Useful during development/debugging to avoid useless roundtrips.

Commands that were already executed are stored in `%AppData%\Alnkesq\RemoteConfigurator\AlreadyExecuted\TARGET-MACHINE-IP.txt`. You can override the directory by setting the `ALNKESQ_REMOTE_CONFIGURATOR_ALREADY_EXECUTED_DIRECTORY` environment variable.