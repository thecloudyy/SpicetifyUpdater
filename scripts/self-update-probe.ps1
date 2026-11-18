# Compiles scripts/SelfUpdateProbe.cs and runs it against a Spicetify Updater build. The probe
# drives that binary's own private self-update members (status, latest tag, setup asset, download
# and checksum verification), so it reports on the shipping code rather than a reimplementation.
#
#   powershell -ExecutionPolicy Bypass -File scripts/self-update-probe.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/self-update-probe.ps1 -Current
#   powershell -ExecutionPolicy Bypass -File scripts/self-update-probe.ps1 -Exe path\to\Spicetify-Updater.exe
#
# Without -Exe it uses the installed app when there is one, otherwise bin\Release. Without
# -Current it expects an update to be offered; with -Current it expects the app to be up to date,
# which is how you confirm an upgrade closed the loop. Needs the network, spends no API quota.

param(
    [string]$Exe,
    [switch]$Current
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Exe) {
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\Spicetify Updater\Spicetify-Updater.exe'
    $built = Join-Path $root 'bin\Release\Spicetify-Updater.exe'
    if (Test-Path $installed) { $Exe = $installed }
    elseif (Test-Path $built) { $Exe = $built }
    else { throw 'no Spicetify-Updater.exe to probe: pass -Exe, or build or install the app first' }
}
if (-not (Test-Path $Exe)) { throw "no such exe: $Exe" }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$out = Join-Path $root 'dist\checks'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$probe = Join-Path $out 'SelfUpdateProbe.exe'

Write-Host '== compile probe'
& $csc -nologo "-out:$probe" (Join-Path $PSScriptRoot 'SelfUpdateProbe.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$arguments = @($Exe)
if ($Current) { $arguments += '--current' }

Write-Host ''
& $probe @arguments
exit $LASTEXITCODE
