# Builds Spicetify Updater, compiles scripts/Checks.cs against the built exe and runs it.
# Also runs the app's own --selftest so the status path is covered.
#
#   powershell -ExecutionPolicy Bypass -File scripts/run-checks.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    Write-Host '== build'
    & dotnet build SpicetifyUpdater.csproj -c Release -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $app = Join-Path $root 'bin\Release\Spicetify-Updater.exe'
    $out = Join-Path $root 'dist\checks'
    New-Item -ItemType Directory -Force -Path $out | Out-Null

    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

    Write-Host '== compile checks'
    & $csc /nologo "/out:$out\Checks.exe" "/r:$app" /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll (Join-Path $root 'scripts\Checks.cs')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host '== run checks'
    & "$out\Checks.exe" $app
    $result = $LASTEXITCODE

    Write-Host ''
    Write-Host '== selftest'
    $json = Join-Path $out 'selftest.json'
    if (Test-Path $json) { Remove-Item $json -Force }
    Start-Process -FilePath $app -ArgumentList "--selftest `"$json`"" -Wait -WindowStyle Hidden | Out-Null

    if (Test-Path $json) {
        $status = Get-Content $json -Raw | ConvertFrom-Json
        $missing = @()
        foreach ($field in 'cliLatest', 'marketplaceLatest', 'selfVersion') {
            if (-not $status.$field) { $missing += $field }
        }
        Write-Host ("  cli {0}  marketplace {1}  self {2}" -f $status.cliLatest, $status.marketplaceLatest, $status.selfVersion)
        if ($status.releaseError) { $missing += 'releaseError: ' + $status.releaseError }
        if ($missing.Count -gt 0) {
            Write-Host ('  FAIL  selftest: ' + ($missing -join ', '))
            $result = 1
        }
        else {
            Write-Host '  ok    selftest reports live versions'
        }
    }
    else {
        Write-Host '  FAIL  selftest wrote no json'
        $result = 1
    }

    Write-Host ''
    if ($result -eq 0) { Write-Host 'EVERYTHING PASSED' } else { Write-Host 'SOMETHING FAILED' }
    exit $result
}
finally {
    Pop-Location
}
