# Exercises the exe resolution in scripts/self-update-probe.ps1 without a build, a release or the
# network: the installed-then-built fallback must pick the built exe when nothing is installed,
# keep preferring an installed one, honour -Exe, and refuse clearly when there is nothing to probe.
#
#   powershell -ExecutionPolicy Bypass -File scripts/resolve-probe.ps1
#
# Offline and instant: the only things it touches are empty files in a temp directory, so
# scripts/run-checks.ps1 runs it too and CI covers it.

$ErrorActionPreference = 'Stop'

# The decision under test is the shipped one; nothing about it changes here.
$source = Get-Content (Join-Path $PSScriptRoot 'self-update-probe.ps1') -Raw
$match = [regex]::Match($source, '(?s)function Resolve-Exe\(.*?\n\}\r?\n')
if (-not $match.Success) { throw 'Resolve-Exe not found in scripts/self-update-probe.ps1' }
Invoke-Expression $match.Value

$dir = Join-Path $env:TEMP ('resolve-probe-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
$installed = Join-Path $dir 'installed\Spicetify-Updater.exe'
$built = Join-Path $dir 'built\Spicetify-Updater.exe'
$chosenByExe = Join-Path $dir 'given\Spicetify-Updater.exe'
$absent = Join-Path $dir 'nowhere\Spicetify-Updater.exe'

function Make([string]$path) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, '')
}

$cases = @(
    @{ name = 'installed is preferred';     installed = $true;  built = $true;  exe = $null;       expect = $installed },
    @{ name = 'built when not installed';   installed = $false; built = $true;  exe = $null;       expect = $built },
    @{ name = '-Exe overrides both';        installed = $true;  built = $true;  exe = $chosenByExe; expect = $chosenByExe },
    @{ name = 'nothing to probe';           installed = $false; built = $false; exe = $null;       expect = $null;
       message = 'no Spicetify-Updater.exe to probe'; names = @($installed, $built) },
    @{ name = '-Exe that does not exist';   installed = $true;  built = $true;  exe = $absent;     expect = $null;
       message = 'no such exe' }
)

$failures = 0
try {
    foreach ($case in $cases) {
        Write-Host "== $($case.name)"

        foreach ($path in $installed, $built, $chosenByExe) {
            if (Test-Path $path) { Remove-Item $path -Force }
        }
        if ($case.installed) { Make $installed }
        if ($case.built) { Make $built }
        if ($case.exe -eq $chosenByExe) { Make $chosenByExe }

        $picked = $null
        $message = $null
        try { $picked = Resolve-Exe $case.exe $installed $built }
        catch { $message = $_.Exception.Message }

        if ($case.expect) {
            $ok = $picked -eq $case.expect
            $detail = if ($ok) { "picked $(Split-Path -Leaf (Split-Path -Parent $picked))" }
                      else { "picked '$picked', expected '$($case.expect)'" }
        }
        else {
            $ok = $message -and $message.Contains($case.message)
            foreach ($name in $case.names) { $ok = $ok -and $message.Contains($name) }
            $detail = if ($ok) { "refused: $message" } else { "wrong failure: '$message'" }
        }

        if (-not $ok) { $failures++ }
        Write-Host ("  {0}  {1}" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $detail)
    }
}
finally {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

if ($failures) {
    Write-Host "$failures case(s) failed"
    exit 1
}

Write-Host 'EXE RESOLUTION OK'
