# Starts a Spicetify Updater build, confirms it stays up and responds, checks that nothing was
# logged as an error while it started, then closes it again. Useful straight after an install.
#
#   powershell -ExecutionPolicy Bypass -File scripts/launch-check.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/launch-check.ps1 -Exe path\to\Spicetify-Updater.exe
#
# Without -Exe it checks the installed app. It only closes the copy it started itself.

param([string]$Exe)

$ErrorActionPreference = 'Stop'

if (-not $Exe) {
    $Exe = Join-Path $env:LOCALAPPDATA 'Programs\Spicetify Updater\Spicetify-Updater.exe'
}
if (-not (Test-Path $Exe)) { throw "no such exe: $Exe (pass -Exe, or install the app first)" }

$since = (Get-Date).AddMinutes(-20)

function AppErrors {
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since; Level = 2 } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Spicetify|\.NET Runtime|Application Error' }
}

$before = @(AppErrors).Count
Write-Host "exe:                  $Exe"
Write-Host "version:              $((Get-Item $Exe).VersionInfo.FileVersion)"
Write-Host "existing instances:   $((Get-Process Spicetify-Updater -ErrorAction SilentlyContinue | Measure-Object).Count)"
Write-Host "error events (20 min): $before"

$app = Start-Process -FilePath $Exe -PassThru
Write-Host "started pid $($app.Id)"

Start-Sleep -Seconds 8
$app.Refresh()
if ($app.HasExited) {
    Write-Host "  FAIL  it exited within 8s, code $($app.ExitCode)"
    Write-Host 'LAUNCH CHECK FAILED'
    exit 1
}

Write-Host "after 8s:             running"
Write-Host "window title:         '$($app.MainWindowTitle)'"
Write-Host "responding:           $($app.Responding)"
Write-Host "working set:          $([math]::Round($app.WorkingSet64 / 1MB, 1)) MB"
Write-Host "child processes:      $((Get-CimInstance Win32_Process -Filter "ParentProcessId=$($app.Id)" | Measure-Object).Count)"

$errors = @(AppErrors)
Stop-Process -Id $app.Id
Start-Sleep -Seconds 2
$stillRunning = (Get-Process -Id $app.Id -ErrorAction SilentlyContinue) -ne $null

if ($errors.Count -gt $before) {
    Write-Host '  FAIL  new error events were logged:'
    $errors | ForEach-Object { Write-Host ('  ' + $_.TimeCreated + '  ' + $_.ProviderName + ': ' + ($_.Message -split "`r?`n")[0]) }
    Write-Host "  (the app was started and stopped anyway: closed=$(-not $stillRunning))"
    Write-Host 'LAUNCH CHECK FAILED'
    exit 1
}

Write-Host '  ok    no error events'
Write-Host "  ok    closed it again: $(-not $stillRunning)"
Write-Host 'LAUNCH CHECK OK'
