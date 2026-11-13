# Spicetify Updater

A tiny desktop app that installs and updates the [Spicetify](https://spicetify.app) CLI and the
[Spicetify Marketplace](https://github.com/spicetify/marketplace) for you.

It reads the official GitHub releases, shows your installed version next to the latest one, and
downloads the correct build — no terminal needed.

## Install

Download `Spicetify-Updater-2.0.0.exe` from the [latest release](../../releases/latest) and run it.

- **57 KB** single executable, no installer
- Native WPF window — no Electron, no bundled browser, no Node.js
- Needs only .NET Framework 4.8, which ships with Windows 10 and 11

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

To regenerate `build\icon.ico` from `build\icon.png`, build a 256×256 ICO with a PNG payload.

## Layout

- `src/Program.cs` — the whole app: WPF UI, GitHub lookups, downloads, zip extraction and the
  spicetify commands
- `SpicetifyUpdater.csproj` — `net48`, `WinExe`
- `build/` — app icon

There is also a `--selftest <path>` switch that writes the detected status as JSON and exits, useful
for checking the detection logic without opening a window.

An earlier Electron-based version of this app is still in the git history.

Spicetify itself is a separate project by its own authors; this app only drives it.
