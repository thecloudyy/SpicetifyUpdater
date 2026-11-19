# Spicetify Updater

[![checks](https://github.com/thecloudyy/SpicetifyUpdater/actions/workflows/checks.yml/badge.svg?branch=main)](https://github.com/thecloudyy/SpicetifyUpdater/actions/workflows/checks.yml)

A tiny desktop app that installs and updates the [Spicetify](https://spicetify.app) CLI and the
[Spicetify Marketplace](https://github.com/spicetify/marketplace) for you.

It reads the official GitHub releases, shows your installed version next to the latest one, and
downloads the correct build — no terminal needed.

## Install

Two ways, both from the [latest release](../../releases/latest):

| File | What it is |
| --- | --- |
| `Spicetify-Updater-<version>-win-x64-setup.exe` | Inno Setup installer. Start-menu entry, optional desktop shortcut, registered uninstaller, per-user install so no admin prompt appears. |
| `Spicetify-Updater-<version>-win-x64.exe` | The same app as a standalone portable exe, ~105 KB. |

Either way it is a native WPF window: no Electron, no bundled browser, no Node.js, and it needs only
.NET Framework 4.8, which ships with Windows 10 and 11.

## Updating the app itself

The app checks its own GitHub releases on launch and, when something newer exists, puts an
**Update to v1.1.0** button in the header. Pressing it downloads the release's setup, quits the app and
runs the Inno installer silently (no wizard), then opens the installed copy again. When there is nothing
newer it just says so in the status bar. If a release only ships a portable exe, that exe is swapped in place
instead; if the folder is not writable it says so instead of leaving a half-written file.

## What it does

- **Spicetify CLI** — downloads `spicetify-<version>-windows-x64.zip` into `%LOCALAPPDATA%\spicetify`
  and adds that folder to your user `PATH`.
- **Marketplace** — downloads `marketplace.zip` into `%APPDATA%\spicetify\CustomApps\marketplace`,
  registers it as a custom app and writes the placeholder theme. Your current theme setting is left
  untouched.
- **Repair** — reinstalls the full CLI package, restores Spotify's original files and re-applies.
  Useful when Spotify won't load after an update.
- **Close Spotify automatically** — stops the client before patching and reopens it afterwards.
- **Maintenance** — Backup & Apply, Apply, and Restore.
- **Checked downloads** — every archive is compared with the release's published size and sha256
  before anything is extracted, so a truncated or tampered download stops with a message instead of
  a broken install. Each release here ships a `SHA256SUMS.txt` next to its files, which the app
  reads from the release page (so the name and hash checks need no API requests). `scripts/release.ps1`
  produces it for every release, and `python scripts/add_checksums.py [tag]` can add one to an
  older release.
- **Update checks** — the latest tag comes from GitHub's `releases/latest` redirect, and the
  files to download come from the release page plus a `HEAD` for their size, so checking and
  installing both cost 0 of the 60 hourly requests. The API is only used if a release page
  cannot be read, and its response is cached with its ETag for that case.

Installs and applies go through the normal `spicetify` commands (`spicetify -v`, `config`, `backup`,
`apply`, `restore`), so behaviour matches the official installers. It is a single file because the
UI is built directly in WPF rather than embedding a browser.

## Build

Requires the .NET SDK (the .NET Framework 4.8 targeting pack comes with it).

```bash
dotnet build SpicetifyUpdater.csproj -c Release
```

The executable lands in `bin\Release\Spicetify-Updater.exe`.

To build the installer, install [Inno Setup 6](https://jrsoftware.org/isdl.php) and compile
`installer.iss` (bump `MyAppVersion` there and `<Version>` in the csproj together):

```bash
iscc installer.iss
```

That writes `dist\Spicetify-Updater-<version>-win-x64-setup.exe`.

## Release

```
powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.0.2
```

Bumps the three version fields, builds the exe and the installer, runs the checks, commits and
pushes, then publishes through a draft: the portable exe, the setup and `SHA256SUMS.txt` are
uploaded in that order (checksums last, so the file only ever describes attachments that already
exist), the uploaded sizes are verified, and only then is the release published, after which
`scripts/verify-release.ps1` fetches the published files the way a user would and checks them
against `SHA256SUMS.txt`. A failure before that last step removes the
draft it created and leaves no tag and nothing public; the version files are restored if the
bump had not been committed yet, so the same command can simply be run again.

`-Notes "…"` sets the release body. `-DryRun` stops after the checksums are written; `-DraftOnly`
also uploads to a throwaway draft, verifies it and deletes it, which exercises the publish half
without announcing anything.

To regenerate `build\icon.ico` from `build\icon.png`, build a 256×256 ICO with a PNG payload.

## Checks

```
powershell -ExecutionPolicy Bypass -File scripts/run-checks.ps1
```

First it checks that the version in `SpicetifyUpdater.csproj` matches `MyAppVersion` in
`installer.iss`, then builds the app, compiles `scripts/Checks.cs` against the built exe and runs it:
checksum-file parsing, download verification (size, sha256, empty and missing files), archive
integrity, the cache-tag rule, then live lookups against GitHub plus a real download from a release,
and finally `--selftest`. It exits non-zero on the first failure and needs no credentials.

It also runs `scripts/retry-probe.ps1`, which points `release.ps1`'s upload retry loop at a local
stand-in server (it needs python, like `scripts/add_checksums.py`): a transient failure must be
retried, a client error must not be, and an upload whose response was lost must not be sent again.
That part is offline and adds about fifteen seconds. `scripts/resolve-probe.ps1` runs with it: it
lifts the exe resolution out of `self-update-probe.ps1` and, against empty files in a temp directory,
checks that an installed copy is preferred, that the built exe is used when nothing is installed,
that `-Exe` wins, and that having neither is refused with both paths named. That is offline too.

For the self-update path there is a second probe, which needs an installed app rather than a fresh
build, so it is not part of the run above:

```
powershell -ExecutionPolicy Bypass -File scripts/self-update-probe.ps1 -Current
```

It compiles `scripts/SelfUpdateProbe.cs` and drives the probed build's own self-update members — the
status the window would show, the latest tag read from github.com, the setup asset and its checksum
from `SHA256SUMS.txt`, a real download verified against it, and a one-byte-corrupted copy refused.
It probes the installed app, falls back to `bin\Release` when that is not installed, or uses one
passed with `-Exe`, and spends no API quota (`-Installed` and `-Built` override the two candidates,
so pointing `-Installed` at a path that does not exist exercises the fallback); without
`-Current` it expects an update to be offered, with `-Current` it expects the app to be up to date,
which is how an upgrade is shown to have closed the loop.

Two more checks look at a release from outside, so they are not part of that run either:

```
powershell -ExecutionPolicy Bypass -File scripts/verify-release.ps1 [-Tag v1.0.2]
powershell -ExecutionPolicy Bypass -File scripts/launch-check.ps1 [-Exe path\to\Spicetify-Updater.exe]
```

`verify-release.ps1` trusts nothing local: it downloads the published `SHA256SUMS.txt`, then every
file that file lists, and compares each hash — defaulting to the version in the csproj, so it also
answers whether the release for the current source is intact. `launch-check.ps1` starts the installed
app, confirms it stays up and responds, checks that nothing was logged as an error, and closes the
copy it started.

GitHub Actions runs `run-checks.ps1` on `windows-latest` for every push and pull request, so a change
that breaks the release lookup, download verification or upload retry fails before a release is cut.

## Layout

- `src/Program.cs` — the whole app: WPF UI, GitHub lookups, downloads, zip extraction and the
  spicetify commands
- `SpicetifyUpdater.csproj` — `net48`, `WinExe`
- `build/` — app icon
- `scripts/` — `release.ps1` (cuts a release), `run-checks.ps1` and `Checks.cs` (the checks above),
  `add_checksums.py` (adds `SHA256SUMS.txt` to a release that predates the script)
- `scripts/` probes — `retry-probe.ps1` with `flaky-upload.py` (upload retries), `resolve-probe.ps1`
  (which exe `self-update-probe.ps1` picks), `self-update-probe.ps1` with `SelfUpdateProbe.cs` (the
  self-update path), `verify-release.ps1` (published assets against their checksums), `launch-check.ps1`
  (the app starts cleanly)

Three switches help without clicking around: `--selftest <path>` writes the detected status as JSON,
`--selfupdate [logpath]` performs the app self-update and exits, and `--render <path.png>` draws the
window to a PNG and exits.

An earlier Electron-based version of this app is still in the git history.

Spicetify itself is a separate project by its own authors; this app only drives it.
