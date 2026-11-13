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
| `Spicetify-Updater-<version>-win-x64.exe` | The same app as a standalone portable exe, ~60 KB. |

Either way it is a native WPF window: no Electron, no bundled browser, no Node.js, and it needs only
.NET Framework 4.8, which ships with Windows 10 and 11.

## Updating the app itself

The app checks its own GitHub releases on launch and shows `Updater v1.0.0 · v1.1.0 available` in the
footer with an **Update to v1.1.0** button. It downloads the new exe, swaps it in after exiting and
relaunches — no installer needed. If it lives in a folder it cannot write to, it says so and points
you at the setup instead of leaving a half-written file.

## What it does

- **Spicetify CLI** — downloads `spicetify-<version>-windows-x64.zip` into `%LOCALAPPDATA%\spicetify`
  and adds that folder to your user `PATH`.
- **Marketplace** — downloads `marketplace.zip` into `%APPDATA%\spicetify\CustomApps\marketplace`,
  registers it as a custom app and writes the placeholder theme.
- **Repair** — reinstalls the full CLI package, restores Spotify's original files and re-applies.
  Useful when Spotify won't load after an update.
- **Close Spotify automatically** — stops the client before patching and reopens it afterwards.
- **Maintenance** — Backup & Apply, Apply, and Restore.

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

## Layout

- `src/Program.cs` — the whole app: WPF UI, GitHub lookups, downloads, zip extraction and the
  spicetify commands
- `SpicetifyUpdater.csproj` — `net48`, `WinExe`
- `build/` — app icon

Two switches help without opening a window: `--selftest <path>` writes the detected status as JSON,
and `--selfupdate [logpath]` performs the app self-update and exits.

An earlier Electron-based version of this app is still in the git history.

Spicetify itself is a separate project by its own authors; this app only drives it.
