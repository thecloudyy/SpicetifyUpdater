# Exercises the upload retry loop in scripts/release.ps1 without GitHub and without building
# anything: a local server fails on cue, and the probe checks that a transient error is retried,
# a client error is not, and a lost response whose upload landed is not sent again.
#
#   powershell -ExecutionPolicy Bypass -File scripts/retry-probe.ps1
#
# Offline and takes about fifteen seconds, so scripts/run-checks.ps1 runs it too and CI covers it.
# Needs python, like scripts/add_checksums.py.

$ErrorActionPreference = 'Stop'

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { throw 'python not found: the retry probe needs it for its local upload server' }

# The loop under test is the shipped one; only the host it talks to changes.
$source = Get-Content (Join-Path $PSScriptRoot 'release.ps1') -Raw
$match = [regex]::Match($source, '(?s)function Send-Asset\(.*?\n\}\r?\n')
if (-not $match.Success) { throw 'Send-Asset not found in scripts/release.ps1' }

# Any free port, so a probe running next to another one cannot serve the wrong answers.
$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()

$base = "http://127.0.0.1:$port"
$server = Start-Process -FilePath $python -ArgumentList @("`"$(Join-Path $PSScriptRoot 'flaky-upload.py')`"", $port) -PassThru -WindowStyle Hidden
$payloadDir = Join-Path $env:TEMP ('retry-probe-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))

try {
    $ready = $false
    foreach ($attempt in 1..40) {
        try { Invoke-RestMethod "$base/stats" | Out-Null; $ready = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw "the local upload server did not start on port $port" }

    Invoke-Expression $match.Value.Replace('https://uploads.github.com/repos/$repo', "$base/repos/x")

    $repo = 'x'
    $headers = @{ Authorization = 'token test' }
    $landedName = $null
    function Read-Release($headers, [string]$id) {
        if ($landedName) { return @{ assets = @(@{ name = $landedName; state = 'uploaded'; size = 7 }) } }
        return @{ assets = @() }
    }

    New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

    $cases = @(
        @{ name = 'flaky';  tries = 3; throws = $false; note = 'retried twice, then succeeded' },
        @{ name = 'fatal';  tries = 1; throws = $true;  note = 'client error, not retried' },
        @{ name = 'dead';   tries = 3; throws = $true;  note = 'gave up after three attempts' },
        @{ name = 'landed'; tries = 1; throws = $false; note = 'already on the release, not resent' }
    )

    $failures = 0
    foreach ($case in $cases) {
        Write-Host "== $($case.name)"
        $file = Join-Path $payloadDir $case.name
        [IO.File]::WriteAllText($file, 'payload')
        $landedName = if ($case.name -eq 'landed') { 'landed' } else { $null }

        $threw = $false
        try { Send-Asset $headers 1 $file 'label' }
        catch { $threw = $true; Write-Host "  threw: $($_.Exception.Message)" }

        $tries = (Invoke-RestMethod "$base/stats").($case.name)
        $ok = $tries -eq $case.tries -and $threw -eq $case.throws
        if (-not $ok) { $failures++ }
        Write-Host ("  {0}  {1} attempt(s), {2}" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $tries, $case.note)
    }

    $landedName = $null
    if ($failures) {
        Write-Host "$failures case(s) failed"
        exit 1
    }

    Write-Host 'RETRY LOOP OK'
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
}
