param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = '',
    [switch]$DryRun,
    [switch]$DraftOnly,
    [string]$ApiBase = 'https://api.github.com',
    [string]$UploadBase = 'https://uploads.github.com'
)

# Publishes a release: bump, build, check, upload, publish.
#
#   powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.0.2
#   powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.0.2 -DryRun
#   powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.0.2 -DraftOnly
#
# The release is created as a draft, the files are uploaded and verified while it is still
# hidden, and only then is it published. Anything that goes wrong before that point removes the
# draft again, so nothing is public, no tag exists, and the same command can simply be re-run.
#
# -DryRun stops after the local builds and checksums; -DraftOnly goes on to upload to the draft
# and verify it, then removes it, which exercises the publish half for real. -DryRun wins if
# both are given.
#
# Needs a token with release access, from GH_TOKEN or a stored git credential for github.com.
#
# -ApiBase and -UploadBase exist so scripts/publish-probe.ps1 can send the publish call to a local
# stand-in instead of GitHub. They default to the real hosts and no real release passes them.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$repo = 'thecloudyy/SpicetifyUpdater'
$portable = Join-Path $root "dist\Spicetify-Updater-$Version-win-x64.exe"
$setup = Join-Path $root "dist\Spicetify-Updater-$Version-win-x64-setup.exe"
$checksumName = 'SHA256SUMS.txt'
$checksumPath = Join-Path $root "dist\$checksumName"

function Step([string]$text) {
    Write-Host ''
    Write-Host "== $text"
}

function Set-Setting([string]$path, [string]$pattern, [string]$value) {
    $text = Get-Content $path -Raw
    $updated = [regex]::Replace($text, $pattern, $value)
    if ($updated -eq $text) { throw "nothing to replace in $path for pattern $pattern" }

    [IO.File]::WriteAllText($path, $updated)
}

function Get-Token {
    if ($env:GH_TOKEN) { return $env:GH_TOKEN.Trim() }

    $lines = "protocol=https`nhost=github.com`n`n" | & git credential fill 2>$null
    $password = $lines | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if ($password) { return $password.Substring('password='.Length).Trim() }

    throw 'no GitHub token: set GH_TOKEN or store a credential for github.com'
}

function Get-Iscc {
    foreach ($candidate in 'Inno Setup 7', 'Inno Setup 6') {
        $path = Join-Path $env:ProgramFiles "$candidate\ISCC.exe"
        if (Test-Path $path) { return $path }
    }

    $found = Get-Command iscc -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }

    throw 'ISCC.exe not found: install Inno Setup 6 or 7, or put iscc on PATH'
}

function Read-Release($headers, [string]$id) {
    return Invoke-RestMethod -Uri "$ApiBase/repos/$repo/releases/$id" -Headers $headers
}

# Also lifted out of this file by scripts/verify-release.ps1, so that the post-publish check
# decodes a download exactly the way this script would (a byte[] response once broke it).
function Get-Text($url, $headers) {
    $response = Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing

    # GitHub serves release files as application/octet-stream, and Windows PowerShell hands
    # those back as bytes, which have none of the string methods used on the result.
    if ($response.Content -is [byte[]]) { return [Text.Encoding]::UTF8.GetString($response.Content) }
    return [string]$response.Content
}

function New-Draft($headers, [string]$sha) {
    return Invoke-RestMethod -Method Post -Uri "$ApiBase/repos/$repo/releases" `
        -Headers $headers -ContentType 'application/json' -Body (@{
            tag_name         = "v$Version"
            target_commitish = $sha
            name             = "Spicetify Updater $Version"
            body             = $Notes
            draft            = $true
            prerelease       = $false
        } | ConvertTo-Json)
}

function Send-Asset($headers, [int64]$releaseId, [string]$path, [string]$label) {
    $name = [IO.Path]::GetFileName($path)
    $url = "$UploadBase/repos/$repo/releases/$releaseId/assets" +
           "?name=$([Uri]::EscapeDataString($name))&label=$([Uri]::EscapeDataString($label))"

    # uploads.github.com drops a request now and then (408, 5xx, a closed connection). The file
    # has already been built and checked by this point, so three attempts are worth much more
    # than rerunning the whole release. Anything else, like a bad token, fails immediately.
    $asset = $null
    foreach ($attempt in 1..3) {
        try {
            $asset = Invoke-RestMethod -Method Post -Uri $url -Headers $headers -InFile $path -ContentType 'application/octet-stream'
            break
        }
        catch {
            $status = 0
            if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
            $fatal = $status -ge 400 -and $status -lt 500 -and $status -notin 408, 429
            if ($fatal -or $attempt -eq 3) { throw }

            # A lost response can still mean the upload landed, so look before sending it again.
            $landed = (Read-Release $headers $releaseId).assets | Where-Object { $_.name -eq $name }
            if ($landed -and $landed.state -eq 'uploaded') { $asset = $landed; break }

            Write-Host "  ..    upload of $name failed ($status), retrying ($attempt of 3)"
            Start-Sleep -Seconds (2 * $attempt)
        }
    }

    Write-Host ("  ok    uploaded {0} ({1:N0} bytes)" -f $asset.name, $asset.size)
}

# Lifted out of this file by scripts/publish-probe.ps1, so it must stand alone and take everything
# it needs: this is the one call that makes a release public, and both a rejected PATCH and a
# response that is still a draft have to come back as a failure.
function Publish-Release($headers, [string]$apiBase, [string]$repo, [string]$id) {
    $release = Invoke-RestMethod -Method Patch -Uri "$apiBase/repos/$repo/releases/$id" `
        -Headers $headers -ContentType 'application/json' -Body (@{ draft = $false } | ConvertTo-Json)
    if ($release.draft) { throw 'the release is still a draft' }

    return $release
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

$bumped = $false
$committed = $false
$release = $null
$published = $null
$ok = $true

Push-Location $root
try {
    Step 'preconditions'
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "version must look like 1.0.2, got '$Version'" }
    if ((git status --porcelain)) { throw 'the working tree has changes, commit or stash them first' }

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'main') { throw "releases are cut from main, this checkout is on $branch" }

    git fetch origin main --quiet
    if ($LASTEXITCODE -ne 0) { throw 'git fetch failed' }

    $ahead = (git rev-list --count 'origin/main..HEAD').Trim()
    $behind = (git rev-list --count 'HEAD..origin/main').Trim()
    if ($ahead -ne '0' -or $behind -ne '0') {
        throw "main is $ahead commit(s) ahead and $behind behind origin/main, push or pull first"
    }

    # Ask origin, not the local clone, so a stale local tag cannot block or fake a release.
    # 0 means the tag is there, 2 means it is not; anything else is a failure to ask, which must
    # not be mistaken for a free version.
    & git ls-remote --exit-code --tags origin "refs/tags/v$Version" *> $null
    if ($LASTEXITCODE -eq 0) { throw "tag v$Version already exists on origin" }
    if ($LASTEXITCODE -ne 2) { throw "git ls-remote failed ($LASTEXITCODE), cannot check whether v$Version exists" }

    $project = Join-Path $root 'SpicetifyUpdater.csproj'
    $declared = [regex]::Match((Get-Content $project -Raw), '<Version>([^<]+)</Version>')
    if (-not $declared.Success) { throw 'no <Version> in SpicetifyUpdater.csproj' }
    $current = $declared.Groups[1].Value.Trim()
    if (([version]$Version).CompareTo([version]$current) -le 0) {
        throw "version $Version is not newer than the current $current"
    }

    # Resolved here rather than at publish time: a missing token must not be discovered
    # after the commit and push have already happened.
    $token = Get-Token
    Write-Host "  ok    $current -> $Version on $branch, token available"

    Step 'bump'
    Set-Setting $project '<Version>[^<]+</Version>' "<Version>$Version</Version>"
    Set-Setting $project '<AssemblyVersion>[^<]+</AssemblyVersion>' "<AssemblyVersion>$Version.0</AssemblyVersion>"
    Set-Setting $project '<FileVersion>[^<]+</FileVersion>' "<FileVersion>$Version.0</FileVersion>"
    Set-Setting (Join-Path $root 'installer.iss') '#define MyAppVersion "[^"]+"' "#define MyAppVersion ""$Version"""
    $bumped = $true
    Write-Host "  ok    csproj and installer.iss now say $Version"

    Step 'build'
    & dotnet build SpicetifyUpdater.csproj -c Release -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

    $app = Join-Path $root 'bin\Release\Spicetify-Updater.exe'
    $built = (Get-Item $app).VersionInfo.FileVersion
    if ($built -notlike "$Version.*") { throw "the built exe reports $built, expected $Version" }

    Step 'installer'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'dist') | Out-Null
    Copy-Item $app $portable -Force
    & (Get-Iscc) installer.iss
    if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

    Step 'checks'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\run-checks.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'the checks failed, nothing was published' }
    Write-Host '  ok    checks passed'

    Step 'checksums'
    $lines = foreach ($file in $portable, $setup) {
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host ("  {0}  {1}" -f $hash, (Split-Path -Leaf $file))
        "$hash  $(Split-Path -Leaf $file)"
    }
    $checksumText = ($lines -join "`n") + "`n"
    [IO.File]::WriteAllText($checksumPath, $checksumText)

    if ($DryRun) {
        Step 'dry run'
        Write-Host '  nothing committed or published'
        exit 0
    }

    $expected = @{}
    foreach ($file in $portable, $setup, $checksumPath) {
        $expected[(Split-Path -Leaf $file)] = (Get-Item $file).Length
    }

    $headers = @{
        Authorization = "token $token"
        Accept        = 'application/vnd.github+json'
        'User-Agent'  = 'SpicetifyUpdater-release'
    }

    # The draft has to point at the release commit, so it is created after the commit is pushed.
    if (-not $DraftOnly) {
        Step 'commit'
        git add SpicetifyUpdater.csproj installer.iss
        git commit -m "Release $Version" -m "Published by scripts/release.ps1."
        if ($LASTEXITCODE -ne 0) { throw 'git commit failed' }
        $committed = $true
        git push origin main
        if ($LASTEXITCODE -ne 0) { throw 'git push failed' }
    }

    Step 'draft release'
    $release = New-Draft $headers (git rev-parse HEAD).Trim()
    Write-Host "  ok    draft $($release.id)"
    Send-Asset $headers $release.id $portable 'Portable app (no install)'
    Send-Asset $headers $release.id $setup 'Windows installer (Inno Setup)'
    # Last on purpose: the checksum file only ever describes files already uploaded.
    Send-Asset $headers $release.id $checksumPath 'SHA-256 checksums'
    Assert-Assets (Read-Release $headers $release.id) $expected

    if ($DraftOnly) {
        Write-Host '  ok    a draft release and its uploads work'
        exit 0
    }

    Step 'publish'
    $published = Publish-Release $headers $ApiBase $repo $release.id
    Write-Host "  ok    published $($published.html_url)"

    # Fetched anonymously from the release's download URLs, the way a user would, so this proves
    # the published bytes and not just the API's idea of them. The release is public by now, so a
    # failure here is reported rather than rolled back.
    Step 'verify published'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\verify-release.ps1') -Tag "v$Version"
    if ($LASTEXITCODE -ne 0) { throw 'the published release does not verify from its download URLs' }
    Write-Host '  ok    published release downloads match SHA256SUMS.txt'

    Step 'done'
    Write-Host "  v$Version is published: $($published.html_url)"
    exit 0
}
catch {
    Write-Host "  FAIL  $($_.Exception.Message)"
    if ($published) { Write-Host "  the release is public: $($published.html_url)" }
    $ok = $false
}
finally {
    # A draft is safe to remove; a published release is not, and a lost response from the publish
    # call could have published it, so ask before deleting.
    if ($release -and -not $published) {
        try {
            if ((Read-Release $headers $release.id).draft) {
                Invoke-RestMethod -Method Delete -Uri "$ApiBase/repos/$repo/releases/$($release.id)" -Headers $headers | Out-Null
                Write-Host '  draft deleted, nothing published'
            }
            else {
                Write-Host "  the release is public: https://github.com/$repo/releases"
            }
        }
        catch {
            Write-Host "  draft $($release.id) is still there: https://github.com/$repo/releases"
        }
    }

    # A bump that never became a commit must not leave the checkout dirty, or the retry this
    # script promises is rejected by its own clean-tree check.
    if ($bumped -and -not $committed) {
        git checkout -- SpicetifyUpdater.csproj installer.iss
        Write-Host '  version files restored'
    }

    Pop-Location
}

if (-not $ok) {
    Write-Host ''
    Write-Host 'RELEASE ABORTED'
    exit 1
}
