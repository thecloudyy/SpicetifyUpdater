# Checks a published release from the outside: downloads the SHA256SUMS.txt it carries, then
# downloads every file that file lists and compares each hash. Nothing local is trusted, so this
# answers "are the published bytes the bytes the release says they are".
#
#   powershell -ExecutionPolicy Bypass -File scripts/verify-release.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/verify-release.ps1 -Tag v1.0.2
#
# Without -Tag it checks the version in SpicetifyUpdater.csproj. Needs the network, no credentials
# and no API quota. The download is decoded by the release script's own Get-Text, so that code is
# exercised too (it is where a byte[] response once broke the comparison).

param([string]$Tag)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = 'thecloudyy/SpicetifyUpdater'

if (-not $Tag) {
    $declared = [regex]::Match((Get-Content (Join-Path $root 'SpicetifyUpdater.csproj') -Raw), '<Version>([^<]+)</Version>')
    if (-not $declared.Success) { throw 'no <Version> in SpicetifyUpdater.csproj, pass -Tag instead' }
    $Tag = 'v' + $declared.Groups[1].Value.Trim()
}

$source = Get-Content (Join-Path $PSScriptRoot 'release.ps1') -Raw
$match = [regex]::Match($source, '(?s)function Get-Text\(.*?\n\}\r?\n')
if (-not $match.Success) { throw 'Get-Text not found in scripts/release.ps1' }
Invoke-Expression $match.Value

$base = "https://github.com/$repo/releases/download/$Tag"
$headers = @{ 'User-Agent' = 'SpicetifyUpdater-release-check' }

Write-Host "release: $Tag"
try {
    $sums = Get-Text "$base/SHA256SUMS.txt" $headers
}
catch {
    Write-Host "  FAIL  could not fetch SHA256SUMS.txt from ${Tag}: $($_.Exception.Message)"
    Write-Host 'RELEASE CHECK FAILED'
    exit 1
}
if (-not $sums) {
    Write-Host "  FAIL  the $Tag release has no SHA256SUMS.txt"
    Write-Host 'RELEASE CHECK FAILED'
    exit 1
}

$temp = Join-Path $env:TEMP ('verify-release-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Force -Path $temp | Out-Null

$failures = 0
$checked = 0
try {
    foreach ($line in ($sums -split "`n")) {
        $line = $line.Trim()
        if (-not $line) { continue }

        $parts = $line -split '\s+', 2
        if ($parts.Count -ne 2) { Write-Host "  FAIL  unreadable line: $line"; $failures++; continue }

        $expected = $parts[0].ToLowerInvariant()
        $name = $parts[1].Trim()
        $path = Join-Path $temp $name
        Write-Host "== $name"

        try {
            Invoke-WebRequest -Uri "$base/$([Uri]::EscapeDataString($name))" -Headers $headers -OutFile $path -UseBasicParsing
        }
        catch {
            Write-Host "  FAIL  could not download it: $($_.Exception.Message)"
            $failures++
            continue
        }

        $checked++
        $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item $path).Length
        if ($actual -eq $expected) {
            Write-Host ('  ok    {0:N0} bytes, sha256 matches' -f $size)
        }
        else {
            Write-Host "  FAIL  sha256 is $actual but SHA256SUMS.txt says $expected"
            $failures++
        }
    }
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    Write-Host "RELEASE CHECK FAILED ($failures problem(s), $checked file(s) checked)"
    exit 1
}
if ($checked -eq 0) {
    Write-Host 'RELEASE CHECK FAILED: SHA256SUMS.txt lists no files'
    exit 1
}

Write-Host "RELEASE CHECK OK ($checked file(s))"
