# Exercises the publish call in scripts/release.ps1 without GitHub: a local stand-in answers the
# PATCH, and the probe checks that a published release is accepted while a rejected PATCH and a
# response that is still a draft are both reported as failures. It then covers the cleanup the
# script runs when the publish call fails: a lost response whose release did go public must be
# reported and left alone, while a release that never went public must be deleted.
#
#   powershell -ExecutionPolicy Bypass -File scripts/publish-probe.ps1
#
# Offline, so scripts/run-checks.ps1 runs it too and CI covers it. Needs python, like
# scripts/add_checksums.py.

$ErrorActionPreference = 'Stop'

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { throw 'python not found: the publish probe needs it for its local API server' }

# The calls under test are the shipped ones; only the host they talk to changes.
$source = Get-Content (Join-Path $PSScriptRoot 'release.ps1') -Raw
$lifted = ''
foreach ($name in 'Publish-Release', 'Remove-Draft') {
    $match = [regex]::Match($source, "(?s)function $name\(.*?\n\}\r?\n")
    if (-not $match.Success) { throw "$name not found in scripts/release.ps1" }
    $lifted += $match.Value + "`n"
}

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

    Invoke-Expression $lifted

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

    # The response is dropped after the stub has published the release, which is exactly the case
    # the cleanup cannot tell from a failed publish by the error alone.
    Write-Host '== lost'
    $threw = $false
    try { Publish-Release $headers $base 'x' 'lost' | Out-Null }
    catch { $threw = $true; Write-Host "  threw: $($_.Exception.Message)" }

    $reported = (& { Remove-Draft $headers @{ id = 'lost' } $base 'x' } 6>&1 | Out-String).Trim()
    if ($reported) { Write-Host "  said: $reported" }

    $deletes = Invoke-RestMethod "$base/deletes"
    $ok = $threw -and $reported -match 'the release is public' -and -not $deletes.lost
    if (-not $ok) { $failures++ }
    Write-Host ("  {0}  publish threw, no DELETE sent, public release reported" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }))

    # The other half of the branch: a release that never went public is still deleted.
    Write-Host '== broken is deleted'
    $reported = (& { Remove-Draft $headers @{ id = 'broken' } $base 'x' } 6>&1 | Out-String).Trim()
    if ($reported) { Write-Host "  said: $reported" }

    $deletes = Invoke-RestMethod "$base/deletes"
    $ok = $reported -match 'draft deleted' -and $deletes.broken -eq 1
    if (-not $ok) { $failures++ }
    Write-Host ("  {0}  1 DELETE sent, unpublished draft removed" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }))

    if ($failures) {
        Write-Host "$failures case(s) failed"
        exit 1
    }

    Write-Host 'PUBLISH CALL OK'
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
