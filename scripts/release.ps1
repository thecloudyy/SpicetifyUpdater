param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = '',
    [switch]$DryRun
)

# Publishes a release end to end.
#
#   powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.0.2
#   powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.0.2 -Notes "What changed"
#   powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.0.2 -DryRun
#
# Order matters and is the point of this script: the portable exe and the setup are built and
# hashed first, the checks run, the commit and tag are pushed, and only then are the files
# uploaded - with SHA256SUMS.txt last, so it always describes files that are already attached.
# A release therefore cannot ship with a missing or stale checksum file.
#
# Needs a GitHub token with release write access, taken from GH_TOKEN, `gh auth token`, or a
# stored git credential for github.com. -DryRun does everything except commit, push and publish.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$repo = 'thecloudyy/SpicetifyUpdater'
$portableName = "Spicetify-Updater-$Version-win-x64.exe"
$setupName = "Spicetify-Updater-$Version-win-x64-setup.exe"
$checksumName = 'SHA256SUMS.txt'

function Step([string]$text) {
    Write-Host ''
    Write-Host "== $text"
}

function Fail([string]$text) {
    Write-Host "  FAIL  $text"
    Write-Host ''
    Write-Host 'RELEASE ABORTED'
    exit 1
}

function Read-Setting([string]$path, [string]$pattern, [string]$label) {
    if (-not (Test-Path $path)) { throw "${label}: file not found at $path" }

    $match = [regex]::Match((Get-Content $path -Raw), $pattern)
    if (-not $match.Success) { throw "${label}: not found in $path" }

    return $match.Groups[1].Value.Trim()
}

function Set-Setting([string]$path, [string]$pattern, [string]$value) {
    $text = Get-Content $path -Raw
    $updated = [regex]::Replace($text, $pattern, $value)
    if ($updated -eq $text) { throw "nothing to replace in $path for pattern $pattern" }

    [IO.File]::WriteAllText($path, $updated)
}

function Get-Token {
    if ($env:GH_TOKEN) { return $env:GH_TOKEN.Trim() }

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
        try {
            $token = & gh auth token 2>$null
            if ($token) { return $token.Trim() }
        }
        catch { }
    }

    try {
        $lines = ("protocol=https`nhost=github.com`n`n" | & git credential fill 2>$null)
        $password = $lines | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
        if ($password) { return $password.Substring('password='.Length).Trim() }
    }
    catch { }

    Fail 'no GitHub token: set GH_TOKEN, run "gh auth login", or store a credential for github.com'
}

function Get-Iscc {
    foreach ($candidate in 'Inno Setup 7', 'Inno Setup 6') {
        $path = Join-Path $env:ProgramFiles "$candidate\ISCC.exe"
        if (Test-Path $path) { return $path }
    }

    $found = Get-Command iscc -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }

    Fail 'ISCC.exe not found: install Inno Setup 6 or 7, or put iscc on PATH'
}

$pushLocation = $false
try {
    Push-Location $root
    $pushLocation = $true

    Step 'preconditions'
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "version must look like 1.0.2, got '$Version'" }

    if ((git status --porcelain)) { Fail 'the working tree has changes, commit or stash them first' }

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'main') { Fail "releases are cut from main, this checkout is on $branch" }

    git fetch origin main --quiet
    if ($LASTEXITCODE -ne 0) { Fail 'git fetch failed' }

    $ahead = (git rev-list --count 'origin/main..HEAD').Trim()
    $behind = (git rev-list --count 'HEAD..origin/main').Trim()
    if ($ahead -ne '0' -or $behind -ne '0') {
        Fail "main is $ahead commit(s) ahead and $behind behind origin/main, push or pull first"
    }

    # Ask origin rather than the local clone, so a stale local tag can neither block a
    # release nor let one be cut for a version that already exists.
    & git ls-remote --exit-code --tags origin "refs/tags/v$Version" *> $null
    if ($LASTEXITCODE -eq 0) { Fail "tag v$Version already exists on origin" }

    $current = Read-Setting (Join-Path $root 'SpicetifyUpdater.csproj') '<Version>([^<]+)</Version>' 'csproj Version'
    $newer = ([version]$Version).CompareTo([version]$current)
    if ($newer -le 0) { Fail "version $Version is not newer than the current $current" }

    $token = $null
    if (-not $DryRun) { $token = Get-Token }
    Write-Host "  ok    $current -> $Version on $branch"

    Step 'bump'
    $project = Join-Path $root 'SpicetifyUpdater.csproj'
    Set-Setting $project '<Version>[^<]+</Version>' "<Version>$Version</Version>"
    Set-Setting $project '<AssemblyVersion>[^<]+</AssemblyVersion>' "<AssemblyVersion>$Version.0</AssemblyVersion>"
    Set-Setting $project '<FileVersion>[^<]+</FileVersion>' "<FileVersion>$Version.0</FileVersion>"
    Set-Setting (Join-Path $root 'installer.iss') '#define MyAppVersion "[^"]+"' "#define MyAppVersion ""$Version"""
    Write-Host "  ok    csproj and installer.iss now say $Version"

    Step 'build'
    & dotnet build SpicetifyUpdater.csproj -c Release -v q
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet build failed' }

    $app = Join-Path $root 'bin\Release\Spicetify-Updater.exe'
    $built = (Get-Item $app).VersionInfo.FileVersion
    if ($built -notlike "$Version.*") { Fail "the built exe reports $built, expected $Version" }
    Write-Host "  ok    exe reports $built"

    Step 'installer'
    $iscc = Get-Iscc
    & $iscc installer.iss
    if ($LASTEXITCODE -ne 0) { Fail 'ISCC failed' }

    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $portable = Join-Path $dist $portableName
    $setup = Join-Path $dist $setupName
    Copy-Item $app $portable -Force
    foreach ($file in $portable, $setup) {
        if (-not (Test-Path $file)) { Fail "expected build output missing: $file" }
        Write-Host ("  ok    {0} ({1:N0} bytes)" -f (Split-Path -Leaf $file), (Get-Item $file).Length)
    }

    Step 'checks'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\run-checks.ps1')
    if ($LASTEXITCODE -ne 0) { Fail 'the checks failed, nothing was published' }
    Write-Host '  ok    checks passed'

    Step 'checksums'
    $lines = foreach ($file in $portable, $setup) {
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host ("  {0}  {1}" -f $hash, (Split-Path -Leaf $file))
        "$hash  $(Split-Path -Leaf $file)"
    }
    $checksumPath = Join-Path $dist $checksumName
    [IO.File]::WriteAllText($checksumPath, ($lines -join "`n") + "`n")
    Write-Host "  ok    $checksumName written next to the builds"

    if ($DryRun) {
        Step 'dry run'
        git checkout -- SpicetifyUpdater.csproj installer.iss
        Write-Host "  version files restored, nothing committed, pushed or published"
        Write-Host "  would publish v$Version with $portableName, $setupName and $checksumName"
        exit 0
    }

    Step 'commit and tag'
    git add SpicetifyUpdater.csproj installer.iss
    git commit -m "Release $Version" -m "Published by scripts/release.ps1."
    if ($LASTEXITCODE -ne 0) { Fail 'git commit failed' }

    git tag -a "v$Version" -m "Spicetify Updater $Version"
    if ($LASTEXITCODE -ne 0) { Fail 'git tag failed' }

    git push origin main
    if ($LASTEXITCODE -ne 0) { Fail 'git push of main failed' }

    git push origin "v$Version"
    if ($LASTEXITCODE -ne 0) { Fail 'git push of the tag failed' }
    Write-Host "  ok    main and v$Version pushed"

    Step 'release'
    $headers = @{
        Authorization = "token $token"
        Accept        = 'application/vnd.github+json'
        'User-Agent'  = 'SpicetifyUpdater-release'
    }
    $sha = (git rev-parse HEAD).Trim()
    $body = @{
        tag_name         = "v$Version"
        target_commitish = $sha
        name             = "Spicetify Updater $Version"
        body             = $Notes
        draft            = $false
        prerelease       = $false
    } | ConvertTo-Json -Depth 5

    $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$repo/releases" `
        -Headers $headers -Body $body -ContentType 'application/json'
    Write-Host "  ok    release created: $($release.html_url)"

    function Send-Asset([string]$path, [string]$label) {
        $name = [IO.Path]::GetFileName($path)
        $url = "https://uploads.github.com/repos/$repo/releases/$($release.id)/assets" +
               "?name=$([Uri]::EscapeDataString($name))&label=$([Uri]::EscapeDataString($label))"
        $asset = Invoke-RestMethod -Method Post -Uri $url -Headers $headers -InFile $path `
            -ContentType 'application/octet-stream'
        Write-Host ("  ok    uploaded {0} ({1:N0} bytes)" -f $asset.name, $asset.size)
        return $asset
    }

    Step 'assets'
    Send-Asset $portable 'Portable app (no install)' | Out-Null
    Send-Asset $setup 'Windows installer (Inno Setup)' | Out-Null

    # Last on purpose: the checksum file can only ever describe files already uploaded.
    Send-Asset $checksumPath 'SHA-256 checksums' | Out-Null

    Step 'verify'
    $published = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/tags/v$Version" -Headers $headers
    $expected = @{}
    $expected[$portableName] = (Get-Item $portable).Length
    $expected[$setupName] = (Get-Item $setup).Length
    $expected[$checksumName] = (Get-Item $checksumPath).Length
    foreach ($asset in $published.assets) {
        if ($asset.state -ne 'uploaded') { Fail "$($asset.name) is $($asset.state)" }
        if ($expected.ContainsKey($asset.name) -and $asset.size -ne $expected[$asset.name]) {
            Fail "$($asset.name) is $($asset.size) bytes on GitHub but $(($expected[$asset.name])) locally"
        }
        Write-Host ("  ok    {0} {1}" -f $asset.name, $asset.state)
    }
    foreach ($name in $expected.Keys) {
        if (-not ($published.assets | Where-Object { $_.name -eq $name })) { Fail "$name is missing from the release" }
    }

    $downloaded = (Invoke-WebRequest -Uri "https://github.com/$repo/releases/download/v$Version/$checksumName" -Headers $headers -UseBasicParsing).Content
    if ($downloaded.Trim() -ne (Get-Content $checksumPath -Raw).Trim()) {
        Fail 'the published checksum file does not match the local one'
    }
    Write-Host '  ok    published checksums match the uploaded files'

    Step 'done'
    Write-Host "  v$Version is published: $($release.html_url)"
    exit 0
}
finally {
    if ($pushLocation) { Pop-Location }
}
