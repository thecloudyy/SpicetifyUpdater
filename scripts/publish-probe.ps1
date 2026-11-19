# Exercises the publish call in scripts/release.ps1 without GitHub: a local stand-in answers the
# PATCH, and the probe checks that a published release is accepted while a rejected PATCH and a
# response that is still a draft are both reported as failures.
#
#   powershell -ExecutionPolicy Bypass -File scripts/publish-probe.ps1
#
# Offline, so scripts/run-checks.ps1 runs it too and CI covers it. Needs python, like
# scripts/add_checksums.py.

$ErrorActionPreference = 'Stop'

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { throw 'python not found: the publish probe needs it for its local API server' }

# The call under test is the shipped one; only the host it talks to changes.
$source = Get-Content (Join-Path $PSScriptRoot 'release.ps1') -Raw
$match = [regex]::Match($source, '(?s)function Publish-Release\(.*?\n\}\r?\n')
if (-not $match.Success) { throw 'Publish-Release not found in scripts/release.ps1' }

# Any free port, so a probe running next to another one cannot serve the wrong answers.
$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()

$base = "http://127.0.0.1:$port"
$server = Start-Process -FilePath $python -ArgumentList @("`"$(Join-Path $PSScriptRoot 'publish-stub.py')`"", $port) -PassThru -WindowStyle Hidden

try {
    $ready = $false
    foreach ($attempt in 1..40) {
        try { Invoke-RestMethod "$base/stats" | Out-Null; $ready = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw "the local API server did not start on port $port" }

    Invoke-Expression $match.Value

    $headers = @{ Authorization = 'token test' }

    $cases = @(
        @{ id = 'ok';     throws = $false; note = 'published, not a draft' },
        @{ id = 'broken'; throws = $true;  note = 'rejected PATCH reported' },
        @{ id = 'draft';  throws = $true;  note = 'still a draft reported' }
    )

    $failures = 0
    foreach ($case in $cases) {
        Write-Host "== $($case.id)"
        $threw = $false
        $release = $null
        try { $release = Publish-Release $headers $base 'x' $case.id }
        catch { $threw = $true; Write-Host "  threw: $($_.Exception.Message)" }

        $sent = (Invoke-RestMethod "$base/stats").($case.id)
        $ok = $threw -eq $case.throws -and $sent -eq 1
        if (-not $threw -and $release.draft -ne $false) { $ok = $false }
        if (-not $ok) { $failures++ }
        Write-Host ("  {0}  {1} PATCH(es), {2}" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $sent, $case.note)
    }

    if ($failures) {
        Write-Host "$failures case(s) failed"
        exit 1
    }

    Write-Host 'PUBLISH CALL OK'
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
