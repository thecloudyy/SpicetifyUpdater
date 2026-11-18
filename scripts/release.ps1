param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = '',
    [switch]$DryRun,
    [switch]$DraftOnly
)

# Publishes a release: bump, build, check, upload, publish.
#
#   powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.0.2
#   powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.0.2 -DryRun
#   powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.0.2 -DraftOnly
#
# The release is created as a draft, the files are uploaded and verified while it is still
# hidden, and only then is it published. A failure in that half leaves nothing public and no
# tag, so the same command can simply be run again.
#
# -DryRun stops after the local builds and checksums; -DraftOnly also creates a draft, uploads
# to it, verifies the assets and deletes the draft, which exercises the publish half for real
# without announcing anything.
#
# Needs a token with release access, from GH_TOKEN, `gh auth token`, or a stored git credential.

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
        $token = & gh auth token 2>$null
        if ($token) { return $token.Trim() }
    }

    $lines = "protocol=https`nhost=github.com`n`n" | & git credential fill 2>$null
    $password = $lines | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if ($password) { return $password.Substring('password='.Length).Trim() }

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

function New-Header([string]$token) {
    return @{
        Authorization = "token $token"
        Accept        = 'application/vnd.github+json'
        'User-Agent'  = 'SpicetifyUpdater-release'
    }
}

function New-Draft($headers, [string]$sha) {
    return Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$repo/releases" `
        -Headers $headers -ContentType 'application/json' -Body (@{
            tag_name         = "v$Version"
            target_commitish = $sha
            name             = "Spicetify Updater $Version"
            body             = $Notes
            draft            = $true
            prerelease       = $false
        } | ConvertTo-Json)
}

function Add-Assets($headers, [int64]$releaseId, [string]$portable, [string]$setup, [string]$checksums) {
    foreach ($item in @(
            @($portable, 'Portable app (no install)'),
            @($setup, 'Windows installer (Inno Setup)'),
            # Last on purpose: the checksum file only ever describes files already uploaded.
            @($checksums, 'SHA-256 checksums'))) {
        $path = $item[0]
        $name = [IO.Path]::GetFileName($path)
        $url = "https://uploads.github.com/repos/$repo/releases/$releaseId/assets" +
               "?name=$([Uri]::EscapeDataString($name))&label=$([Uri]::EscapeDataString($item[1]))"
        $asset = Invoke-RestMethod -Method Post -Uri $url -Headers $headers -InFile $path -ContentType 'application/octet-stream'
        Write-Host ("  ok    uploaded {0} ({1:N0} bytes)" -f $asset.name, $asset.size)
    }
}

function Assert-Assets($release, [hashtable]$expected) {
    foreach ($name in $expected.Keys) {
        $asset = $release.assets | Where-Object { $_.name -eq $name }
        if (-not $asset) { throw "$name is missing from the release" }
        if ($asset.state -ne 'uploaded') { throw "$name is $($asset.state)" }
        if ($asset.size -ne $expected[$name]) { throw "$name is $($asset.size) bytes on GitHub but $($expected[$name]) locally" }
    }

    Write-Host "  ok    $($expected.Count) assets uploaded with the expected sizes"
}

Push-Location $root
try {
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

    # Ask origin, not the local clone, so a stale local tag cannot block or fake a release.
    & git ls-remote --exit-code --tags origin "refs/tags/v$Version" *> $null
    if ($LASTEXITCODE -eq 0) { Fail "tag v$Version already exists on origin" }

    $current = Read-Setting (Join-Path $root 'SpicetifyUpdater.csproj') '<Version>([^<]+)</Version>' 'csproj Version'
    if (([version]$Version).CompareTo([version]$current) -le 0) {
        Fail "version $Version is not newer than the current $current"
    }

    # Resolved here rather than at publish time: a missing token must not be discovered
    # after the commit and push have already happened.
    $token = Get-Token
    Write-Host "  ok    $current -> $Version on $branch, token available"

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

    Step 'installer'
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $portable = Join-Path $dist $portableName
    $setup = Join-Path $dist $setupName
    Copy-Item $app $portable -Force

    & (Get-Iscc) installer.iss
    if ($LASTEXITCODE -ne 0) { Fail 'ISCC failed' }
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
    $checksumText = ($lines -join "`n") + "`n"
    $checksumPath = Join-Path $dist $checksumName
    [IO.File]::WriteAllText($checksumPath, $checksumText)

    $expected = @{}
    $expected[$portableName] = (Get-Item $portable).Length
    $expected[$setupName] = (Get-Item $setup).Length
    $expected[$checksumName] = (Get-Item $checksumPath).Length

    if ($DryRun) {
        Step 'dry run'
        git checkout -- SpicetifyUpdater.csproj installer.iss
        Write-Host '  version files restored, nothing committed or published'
        exit 0
    }

    $headers = New-Header $token

    if ($DraftOnly) {
        Step 'draft check'
        $release = New-Draft $headers (git rev-parse HEAD).Trim()
        try {
            Add-Assets $headers $release.id $portable $setup $checksumPath
            Assert-Assets (Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/$($release.id)" -Headers $headers) $expected
            Write-Host '  ok    a draft release and its uploads work'
        }
        finally {
            Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$repo/releases/$($release.id)" -Headers $headers | Out-Null
            git checkout -- SpicetifyUpdater.csproj installer.iss
            Write-Host '  draft deleted, version files restored, nothing published'
        }
        exit 0
    }

    Step 'commit'
    git add SpicetifyUpdater.csproj installer.iss
    git commit -m "Release $Version" -m "Published by scripts/release.ps1."
    if ($LASTEXITCODE -ne 0) { Fail 'git commit failed' }
    git push origin main
    if ($LASTEXITCODE -ne 0) { Fail 'git push failed' }

    # From here the release is a draft until everything has been uploaded and verified, so a
    # failure leaves nothing public and no tag, and the command can just be run again.
    $release = $null
    try {
        Step 'draft release'
        $release = New-Draft $headers (git rev-parse HEAD).Trim()
        Write-Host "  ok    draft $($release.id)"

        Step 'upload'
        Add-Assets $headers $release.id $portable $setup $checksumPath

        Step 'verify draft'
        Assert-Assets (Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/$($release.id)" -Headers $headers) $expected

        Step 'publish'
        $published = Invoke-RestMethod -Method Patch -Uri "https://api.github.com/repos/$repo/releases/$($release.id)" `
            -Headers $headers -ContentType 'application/json' -Body (@{ draft = $false } | ConvertTo-Json)
        if ($published.draft) { throw 'the release is still a draft' }
        Write-Host "  ok    published $($published.html_url)"

        Step 'verify published'
        $live = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/tags/v$Version" -Headers $headers
        Assert-Assets $live $expected

        $checksumAsset = $live.assets | Where-Object { $_.name -eq $checksumName }
        $downloaded = (Invoke-WebRequest -Uri $checksumAsset.browser_download_url -Headers $headers -UseBasicParsing).Content
        if ($downloaded.Trim() -ne $checksumText.Trim()) { throw 'the published checksum file does not match the built files' }
        Write-Host '  ok    published checksums match the uploaded files'
    }
    catch {
        Write-Host "  FAIL  $($_.Exception.Message)"
        if ($release) { Write-Host "  nothing is public: draft $($release.id) at https://github.com/$repo/releases" }
        Write-Host ''
        Write-Host 'RELEASE ABORTED'
        exit 1
    }

    Step 'done'
    Write-Host "  v$Version is published: $($published.html_url)"
    exit 0
}
finally {
    Pop-Location
}
