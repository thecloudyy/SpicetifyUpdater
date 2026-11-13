'use strict';

const os = require('os');
const fs = require('fs');
const path = require('path');
const https = require('https');
const { spawn } = require('child_process');

const IS_WIN = process.platform === 'win32';
const HOME = os.homedir();

// Spicetify 2.4x keeps the whole package (binary + jsHelper + css-map) in one
// folder: %LOCALAPPDATA%\spicetify on Windows, ~/.spicetify elsewhere.
// ~/.spicetify is the legacy Windows location and is migrated away from.
const LEGACY_CLI_ROOT = IS_WIN ? path.join(HOME, '.spicetify') : null;
const CLI_ROOT = IS_WIN
  ? path.join(process.env.LOCALAPPDATA || path.join(HOME, 'AppData', 'Local'), 'spicetify')
  : path.join(HOME, '.spicetify');
const BIN_NAME = IS_WIN ? 'spicetify.exe' : 'spicetify';
const EXE = path.join(CLI_ROOT, BIN_NAME);

const CLI_REPO = 'spicetify/cli';
const MARKETPLACE_REPO = 'spicetify/marketplace';
const COLORS_URL = 'https://raw.githubusercontent.com/spicetify/marketplace/main/resources/color.ini';

const UA = 'SpicetifyUpdater/1.0 (+https://spicetify.app)';

/* ------------------------------------------------------------------ network */

function request(url, { method = 'GET', headers = {}, redirects = 5 } = {}) {
  return new Promise((resolve, reject) => {
    const req = https.request(url, { method, headers: { 'User-Agent': UA, ...headers } }, (res) => {
      const { statusCode = 0, headers: resHeaders } = res;
      if (statusCode >= 300 && statusCode < 400 && resHeaders.location) {
        res.resume();
        if (redirects <= 0) return reject(new Error('Too many redirects'));
        const next = new URL(resHeaders.location, url).toString();
        return resolve(request(next, { method, headers, redirects: redirects - 1 }));
      }
      if (statusCode !== 200) {
        res.resume();
        return reject(new Error(`HTTP ${statusCode} for ${url}`));
      }
      resolve(res);
    });
    req.on('error', reject);
    req.setTimeout(30000, () => req.destroy(new Error('Request timed out')));
    req.end();
  });
}

async function getJson(url) {
  const res = await request(url, { headers: { Accept: 'application/vnd.github+json' } });
  const chunks = [];
  for await (const chunk of res) chunks.push(chunk);
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

async function download(url, destPath, onProgress) {
  const res = await request(url);
  const total = Number(res.headers['content-length'] || 0);
  let received = 0;
  await new Promise((resolve, reject) => {
    const out = fs.createWriteStream(destPath);
    res.on('data', (chunk) => {
      received += chunk.length;
      if (onProgress) onProgress(received, total);
    });
    res.on('error', reject);
    out.on('error', reject);
    out.on('finish', resolve);
    res.pipe(out);
  });
  return destPath;
}

/* ---------------------------------------------------------------- versioning */

function parseVersion(text) {
  const m = String(text || '').match(/(\d+)\.(\d+)\.(\d+)/);
  return m ? m[0] : null;
}

function compareVersions(a, b) {
  const pa = String(a || '0').split('.').map(Number);
  const pb = String(b || '0').split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i += 1) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d !== 0) return d > 0 ? 1 : -1;
  }
  return 0;
}

/* --------------------------------------------------------------- processes */

function run(exe, args, { onLine, env, cwd } = {}) {
  return new Promise((resolve) => {
    let child;
    try {
      child = spawn(exe, args, {
        cwd: cwd || HOME,
        env: { ...process.env, PATH: `${CLI_ROOT}${path.delimiter}${process.env.PATH || ''}`, ...env },
        windowsHide: true,
      });
    } catch (err) {
      return resolve({ code: -1, output: err.message });
    }
    let output = '';
    const handle = (buf) => {
      const text = buf.toString('utf8');
      output += text;
      if (onLine) {
        text.split(/\r?\n/).forEach((line) => {
          if (line.trim()) onLine(line.replace(/\x1b\[[0-9;]*m/g, '').trimEnd());
        });
      }
    };
    child.stdout.on('data', handle);
    child.stderr.on('data', handle);
    child.on('error', (err) => resolve({ code: -1, output: output + err.message }));
    child.on('close', (code) => resolve({ code: code === null ? -1 : code, output }));
  });
}

function powershell(script) {
  return run('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script]);
}

/* -------------------------------------------------------------------- state */

function cliInstalledPath() {
  if (fs.existsSync(EXE)) return EXE;
  if (LEGACY_CLI_ROOT) {
    const legacy = path.join(LEGACY_CLI_ROOT, BIN_NAME);
    if (fs.existsSync(legacy)) return legacy;
  }
  return null;
}

async function getUserDataDir() {
  const exe = cliInstalledPath();
  if (exe) {
    const res = await run(exe, ['path', 'userdata']);
    const candidate = res.output.trim().split(/\r?\n/).pop();
    if (res.code === 0 && candidate && fs.existsSync(candidate)) return candidate;
  }
  const fallback = IS_WIN
    ? path.join(process.env.APPDATA || path.join(HOME, 'AppData', 'Roaming'), 'spicetify')
    : path.join(process.env.XDG_CONFIG_HOME || path.join(HOME, '.config'), 'spicetify');
  return fallback;
}

function marketplaceDir(userdata) {
  return path.join(userdata, 'CustomApps', 'marketplace');
}

function readMarketplaceVersion(userdata) {
  const marker = path.join(marketplaceDir(userdata), '.marketplace-version');
  try {
    return fs.readFileSync(marker, 'utf8').trim() || null;
  } catch {
    return null;
  }
}

function spotifyInfo() {
  if (!IS_WIN) return { found: fs.existsSync('/Applications/Spotify.app'), path: '/Applications/Spotify.app' };
  const candidates = [
    path.join(process.env.APPDATA || '', 'Spotify', 'Spotify.exe'),
    path.join(process.env.LOCALAPPDATA || '', 'Microsoft', 'WindowsApps', 'SpotifyAB.SpotifyMusic_zpdnekdrzrea0', 'Spotify.exe'),
    'C:\\Program Files\\WindowsApps\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\\Spotify.exe',
  ];
  const found = candidates.find((p) => p && fs.existsSync(p));
  return { found: Boolean(found), path: found || null };
}

async function isSpotifyRunning() {
  if (!IS_WIN) return false;
  const res = await powershell('(Get-Process Spotify -ErrorAction SilentlyContinue | Measure-Object).Count');
  return Number(res.output.trim()) > 0;
}

async function stopSpotify() {
  if (!IS_WIN) return false;
  const res = await powershell(
    'Get-Process Spotify -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 800; (Get-Process Spotify -ErrorAction SilentlyContinue | Measure-Object).Count'
  );
  return Number(res.output.trim()) === 0;
}

function startSpotify() {
  const exe = spotifyInfo().path;
  if (!exe || !fs.existsSync(exe)) return false;
  // Launch through the shell the same way a Start-menu click does; spawning
  // Spotify.exe directly can leave a second top-level window behind.
  const command = IS_WIN ? 'cmd.exe' : 'sh';
  const args = IS_WIN ? ['/c', 'start', '', exe] : ['-c', `"${exe}" >/dev/null 2>&1 &`];
  const child = spawn(command, args, { detached: true, stdio: 'ignore', windowsHide: true });
  child.unref();
  return true;
}

function onUserPath() {
  const entries = (process.env.PATH || '').split(path.delimiter).map((e) => e.replace(/[\\/]+$/, '').toLowerCase());
  return entries.includes(CLI_ROOT.toLowerCase());
}

async function isCliOnMachinePath() {
  if (IS_WIN) {
    const res = await run('powershell.exe', [
      '-NoProfile',
      '-NonInteractive',
      '-Command',
      `$p = [Environment]::GetEnvironmentVariable('Path','User') + ';' + [Environment]::GetEnvironmentVariable('Path','Machine'); if ($p.Split(';') | Where-Object { $_.Trim().TrimEnd('\\') -eq '${CLI_ROOT.replace(/'/g, "''")}' }) { 'yes' } else { 'no' }`,
    ]);
    return res.output.includes('yes');
  }
  return /\.spicetify/.test(process.env.PATH || '');
}

const releaseCache = new Map();

async function latestRelease(repo, { force = false } = {}) {
  const cached = releaseCache.get(repo);
  if (!force && cached && Date.now() - cached.at < 5 * 60 * 1000) return cached.data;
  const data = await getJson(`https://api.github.com/repos/${repo}/releases/latest`);
  const result = {
    tag: String(data.tag_name || '').replace(/^v/, ''),
    url: data.html_url,
    publishedAt: data.published_at || null,
    assets: (data.assets || []).map((a) => ({ name: a.name, url: a.browser_download_url, size: a.size })),
  };
  releaseCache.set(repo, { at: Date.now(), data: result });
  return result;
}

function archTag() {
  if (process.arch === 'arm64') return 'arm64';
  return 'amd64';
}

function cliAsset(release) {
  if (IS_WIN) {
    const arch = process.arch === 'arm64' ? 'arm64' : process.arch === 'ia32' ? 'x32' : 'x64';
    const byName = (suffix) => release.assets.find((a) => a.name.endsWith(suffix));
    return byName(`windows-${arch}.zip`) || byName('windows-x64.zip') || release.assets.find((a) => a.name.includes('windows'));
  }
  const platform = process.platform === 'darwin' ? 'darwin' : 'linux';
  return (
    release.assets.find((a) => a.name.endsWith(`${platform}-${archTag()}.tar.gz`)) ||
    release.assets.find((a) => a.name.includes(platform))
  );
}

/* ------------------------------------------------------------------ helpers */

function tempDir(prefix) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  return dir;
}

async function extractZip(zipPath, destDir) {
  fs.mkdirSync(destDir, { recursive: true });
  const escapedZip = zipPath.replace(/'/g, "''");
  const escapedDest = destDir.replace(/'/g, "''");
  const res = await powershell(
    `$ErrorActionPreference='Stop'; Expand-Archive -LiteralPath '${escapedZip}' -DestinationPath '${escapedDest}' -Force`
  );
  if (res.code !== 0) throw new Error(`Failed to extract archive: ${res.output.trim()}`);
}

async function extractTarGz(filePath, destDir) {
  fs.mkdirSync(destDir, { recursive: true });
  const res = await run('tar', ['-xzf', filePath, '-C', destDir]);
  if (res.code !== 0) throw new Error(`Failed to extract archive: ${res.output.trim()}`);
}

function findFile(dir, name) {
  const stack = [dir];
  while (stack.length) {
    const current = stack.pop();
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) stack.push(full);
      else if (entry.name === name) return full;
    }
  }
  return null;
}

function copyDirContents(from, to) {
  fs.mkdirSync(to, { recursive: true });
  for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
    const src = path.join(from, entry.name);
    const dst = path.join(to, entry.name);
    if (entry.isDirectory()) copyDirContents(src, dst);
    else fs.copyFileSync(src, dst);
  }
}

/* ------------------------------------------------------------------ actions */

async function installCli({ log, progress }) {
  const release = await latestRelease(CLI_REPO, { force: true });
  const asset = cliAsset(release);
  if (!asset) throw new Error(`No ${process.platform} build found in release v${release.tag}`);

  const work = tempDir('spicetify-cli-');
  const archive = path.join(work, asset.name);
  log(`Downloading ${asset.name}`);
  await download(asset.url, archive, (received, total) => progress(total ? received / total : null, received, total));

  log('Extracting');
  const unpacked = path.join(work, 'unpacked');
  if (asset.name.endsWith('.zip')) await extractZip(archive, unpacked);
  else await extractTarGz(archive, unpacked);

  log(`Installing into ${CLI_ROOT}`);
  fs.mkdirSync(CLI_ROOT, { recursive: true });
  copyDirContents(unpacked, CLI_ROOT);
  if (!IS_WIN) fs.chmodSync(EXE, 0o755);

  if (!fs.existsSync(EXE)) throw new Error('Spicetify executable missing after extraction');

  const version = await run(EXE, ['-v']);
  log(`spicetify -v -> ${version.output.trim() || '(no output)'}`);

  if (!(await isCliOnMachinePath())) {
    log(`Adding ${CLI_ROOT} to your user PATH`);
    if (IS_WIN) {
      const p = CLI_ROOT.replace(/'/g, "''");
      const legacy = LEGACY_CLI_ROOT ? LEGACY_CLI_ROOT.replace(/'/g, "''") : '';
      await powershell(
        `$p='${p}'; $legacy='${legacy}'; $old=[Environment]::GetEnvironmentVariable('Path','User'); if ($legacy) { $old = ($old -split ';' | Where-Object { $_ -and ($_.Trim().TrimEnd('\\') -ne $legacy) }) -join ';' }; if (-not ($old -split ';' | Where-Object { $_.Trim().TrimEnd('\\') -eq $p })) { $old = if ([string]::IsNullOrWhiteSpace($old)) { $p } else { $old.TrimEnd(';') + ';' + $p } }; [Environment]::SetEnvironmentVariable('Path', $old, 'User')`
      );
    } else {
      log(`Add this to your shell profile: export PATH="$PATH:${CLI_ROOT}"`);
    }
  }

  if (LEGACY_CLI_ROOT && fs.existsSync(LEGACY_CLI_ROOT)) {
    log(`Note: an older install is still in ${LEGACY_CLI_ROOT} – the up-to-date one is ${CLI_ROOT}`);
  }

  fs.rmSync(work, { recursive: true, force: true });
  log(`Spicetify CLI v${release.tag} installed`);
  return release.tag;
}

async function installMarketplace({ log, progress, setCurrentTheme = true }) {
  const exe = cliInstalledPath();
  if (!exe) throw new Error('Install the Spicetify CLI first');

  const userdata = await getUserDataDir();
  const appDir = marketplaceDir(userdata);
  const themeDir = path.join(userdata, 'Themes', 'marketplace');

  const release = await latestRelease(MARKETPLACE_REPO, { force: true });
  const asset = release.assets.find((a) => a.name === 'marketplace.zip') || release.assets[0];
  if (!asset) throw new Error('No marketplace build found in the latest release');

  const work = tempDir('spicetify-marketplace-');
  const archive = path.join(work, 'marketplace.zip');
  log(`Downloading ${asset.name}`);
  await download(asset.url, archive, (received, total) => progress(total ? received / total : null, received, total));

  log('Extracting');
  const unpacked = path.join(work, 'unpacked');
  await extractZip(archive, unpacked);
  const manifest = findFile(unpacked, 'manifest.json');
  if (!manifest) throw new Error('marketplace.zip did not contain a manifest.json');
  const sourceDir = path.dirname(manifest);

  log(`Installing into ${appDir}`);
  fs.rmSync(appDir, { recursive: true, force: true });
  fs.rmSync(themeDir, { recursive: true, force: true });
  copyDirContents(sourceDir, appDir);
  fs.mkdirSync(themeDir, { recursive: true });

  log('Downloading placeholder Marketplace theme');
  const colorPath = path.join(work, 'color.ini');
  await download(COLORS_URL, colorPath);
  fs.copyFileSync(colorPath, path.join(themeDir, 'color.ini'));

  log('Registering Marketplace as a custom app');
  await run(exe, ['config', 'custom_apps', 'spicetify-marketplace-', '-q'], { onLine: log });
  const add = await run(exe, ['config', 'custom_apps', 'marketplace'], { onLine: log });
  if (add.code !== 0) {
    log('Could not add marketplace to custom_apps automatically – set it manually with:');
    log('  spicetify config custom_apps marketplace');
  }
  await run(exe, ['config', 'inject_css', '1', 'replace_colors', '1'], { onLine: log });

  if (setCurrentTheme) {
    const theme = await run(exe, ['config', 'current_theme', 'marketplace'], { onLine: log });
    if (theme.code === 0) log('Marketplace theme set as current theme');
  }

  log('Backing up Spotify and applying changes');
  await run(exe, ['backup'], { onLine: log });
  const apply = await run(exe, ['apply'], { onLine: log });
  if (apply.code !== 0) log('spicetify apply returned a non-zero exit code – see the output above');

  fs.writeFileSync(path.join(appDir, '.marketplace-version'), `${release.tag}\n`);
  fs.rmSync(work, { recursive: true, force: true });
  log(`Marketplace v${release.tag} installed`);
  return release.tag;
}

/*
 * Repairs a Spicetify setup that leaves Spotify unable to start: reinstall the
 * complete CLI package (binary + jsHelper + css-map), restore the original
 * Spotify files, then apply the patches again.
 */
async function repair({ log, progress }) {
  const version = await installCli({ log, progress });
  const exe = cliInstalledPath();
  if (!exe) throw new Error('Spicetify CLI is not installed');

  log('Restoring the original Spotify files', 'heading');
  const restore = await run(exe, ['restore'], { onLine: log });
  if (restore.code !== 0) log('No usable backup to restore from – continuing with apply', 'dim');

  if (!fs.existsSync(path.join(CLI_ROOT, 'jsHelper'))) {
    log(`Warning: ${path.join(CLI_ROOT, 'jsHelper')} is still missing – run Repair again after closing terminals that hold the folder`, 'error');
  }

  log('Re-applying Spicetify', 'heading');
  const apply = await run(exe, ['apply'], { onLine: log });
  if (apply.code !== 0) throw new Error(`spicetify apply exited with code ${apply.code} – is Spotify running?`);
  return version;
}

async function runCliCommand(args, { log, allowFailure = false } = {}) {  const exe = cliInstalledPath();
  if (!exe) throw new Error('Spicetify CLI is not installed');
  const res = await run(exe, args, { onLine: log });
  if (res.code !== 0 && !allowFailure) throw new Error(`spicetify ${args.join(' ')} exited with code ${res.code}`);
  return res;
}

/* ------------------------------------------------------------------- status */

async function getStatus({ force = false } = {}) {
  const exe = cliInstalledPath();
  const userdata = await getUserDataDir();
  const appDir = marketplaceDir(userdata);

  let cliVersion = null;
  if (exe) {
    const res = await run(exe, ['-v']);
    cliVersion = parseVersion(res.output);
  }

  const marketplaceInstalled = fs.existsSync(path.join(appDir, 'manifest.json'));
  const spotify = spotifyInfo();

  let cliLatest = null;
  let marketplaceLatest = null;
  let releaseError = null;
  try {
    cliLatest = await latestRelease(CLI_REPO, { force });
  } catch (err) {
    releaseError = err.message;
  }
  try {
    marketplaceLatest = await latestRelease(MARKETPLACE_REPO, { force });
  } catch (err) {
    releaseError = releaseError || err.message;
  }

  return {
    platform: `${process.platform}-${process.arch}`,
    installedPath: exe,
    userdata,
    marketplacePath: appDir,
    onPath: await isCliOnMachinePath(),
    spotify,
    releaseError,
    cli: {
      installed: Boolean(exe),
      version: cliVersion,
      latest: cliLatest ? cliLatest.tag : null,
      releaseUrl: cliLatest ? cliLatest.url : 'https://github.com/spicetify/cli/releases',
      updateAvailable: Boolean(cliVersion && cliLatest && compareVersions(cliLatest.tag, cliVersion) > 0),
    },
    marketplace: {
      installed: marketplaceInstalled,
      version: marketplaceInstalled ? readMarketplaceVersion(userdata) : null,
      latest: marketplaceLatest ? marketplaceLatest.tag : null,
      releaseUrl: marketplaceLatest ? marketplaceLatest.url : 'https://github.com/spicetify/marketplace/releases',
      updateAvailable: Boolean(
        marketplaceInstalled && marketplaceLatest && compareVersions(marketplaceLatest.tag, readMarketplaceVersion(userdata) || '0') > 0
      ),
    },
  };
}

module.exports = {
  EXE,
  CLI_ROOT,
  installCli,
  installMarketplace,
  repair,
  isSpotifyRunning,
  stopSpotify,
  startSpotify,
  runCliCommand,
  getStatus,
  latestRelease,
  // exposed for offline checks / tooling
  download,
  extractZip,
  extractTarGz,
  findFile,
  cliAsset,
  run,
};
