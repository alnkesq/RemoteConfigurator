$ErrorActionPreference = 'Stop'

# Early script, before winget and PowerShell Core are ready.
# Invoke in .sshrecipe via:
# powershell -NoProfile -ExecutionPolicy Unrestricted ./InstallWinGet.ps1

cd $env:Temp

$ProgressPreference = 'SilentlyContinue'
iwr https://github.com/microsoft/winget-cli/releases/download/v1.11.400/DesktopAppInstaller_Dependencies.zip -OutFile DesktopAppInstaller_Dependencies.zip
Expand-Archive .\DesktopAppInstaller_Dependencies.zip
del DesktopAppInstaller_Dependencies.zip
cd DesktopAppInstaller_Dependencies\x64

dir *.appx | % { Add-AppxPackage $_.FullName }
del *.appx
Add-AppxPackage "https://aka.ms/getwinget"

