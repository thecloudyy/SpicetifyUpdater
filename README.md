# Spicetify Updater

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
  reads from the release page (so the name and hash checks need no API requests). Releases are
  stamped with `python scripts/add_checksums.py [tag]` after their files are uploaded.
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

That writes `dist\Spicetify-Updater-Setup-<version>.exe`.

To regenerate `build\icon.ico` from `build\icon.png`, build a 256×256 ICO with a PNG payload.

## Checks

```
powershell -ExecutionPolicy Bypass -File scripts/run-checks.ps1
```

Builds the app, compiles `scripts/Checks.cs` against the built exe and runs it: checksum-file
parsing, download verification (size, sha256, empty and missing files), archive integrity, the
cache-tag rule, then live lookups against GitHub plus a real download from a release, and finally
`--selftest`. It exits non-zero on the first failure and needs no credentials.

GitHub Actions runs the same script on `windows-latest` for every push and pull request, so a change
that breaks the release lookup or download verification fails before a release is cut.

## Layout

- `src/Program.cs` — the whole app: WPF UI, GitHub lookups, downloads, zip extraction and the
  spicetify commands
- `SpicetifyUpdater.csproj` — `net48`, `WinExe`
- `build/` — app icon
- `scripts/` — `run-checks.ps1` and `Checks.cs` (the checks above), `add_checksums.py` (publishes
  `SHA256SUMS.txt` for a release)

Three switches help without clicking around: `--selftest <path>` writes the detected status as JSON,
`--selfupdate [logpath]` performs the app self-update and exits, and `--render <path.png>` draws the
window to a PNG and exits.

An earlier Electron-based version of this app is still in the git history.

Spicetify itself is a separate project by its own authors; this app only drives it.
