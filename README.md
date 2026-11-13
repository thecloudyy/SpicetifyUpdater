# Spicetify Updater

A small desktop app that installs and updates the [Spicetify](https://spicetify.app) CLI and the
[Spicetify Marketplace](https://github.com/spicetify/marketplace) for you.

It reads the official GitHub releases, shows your installed version next to the latest one, and
downloads the correct build for your platform — no terminal needed.

## Install

Download `Spicetify-Updater-<version>.exe` from the [latest release](../../releases/latest) and run it.
It's a portable executable: nothing is installed, no Node.js required.

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
`apply`, `restore`), so behaviour matches the official installers.

## Development

Requires Node.js 18+.

```bash
npm install
npm start                 # run the Electron app
npm run serve             # run the UI in a browser instead
npm run dist              # build the portable exe into dist/
```

## Layout

- `electron/main.js` — desktop window host
- `server.js` — local HTTP server and API (`/api/status`, `/api/action`, `/api/events`)
- `lib/spicetify.js` — detection, downloads, extraction and the spicetify commands
- `public/` — the UI

Spicetify itself is a separate project by its own authors; this app only drives it.
