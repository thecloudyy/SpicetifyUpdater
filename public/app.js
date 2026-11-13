'use strict';

const $ = (id) => document.getElementById(id);
const logEl = $('log');
const barEl = $('bar');
const barFill = $('bar-fill');

let busy = false;
let status = null;
let hasLog = false;

function version(value) {
  return value ? `v${value}` : '—';
}

function addLine(text, level = 'info') {
  if (!hasLog) {
    logEl.innerHTML = '';
    hasLog = true;
  }
  const line = document.createElement('span');
  line.className = `line ${level === 'info' ? 'dim' : level}`;
  const time = new Date().toLocaleTimeString([], { hour12: false });
  line.textContent = `[${time}] ${text}`;
  logEl.appendChild(line);
  logEl.scrollTop = logEl.scrollHeight;
}

function setBusy(value, label) {
  busy = value;
  document.querySelectorAll('button').forEach((btn) => {
    if (btn.id === 'clear' || btn.id === 'refresh') return;
    btn.disabled = value;
  });
  $('state-label').textContent = value ? label || 'Working…' : 'Idle';
  $('state-label').classList.toggle('busy', value);
  if (!value) {
    barEl.classList.remove('indeterminate');
    barFill.style.width = '0%';
    render();
  }
}

function setPill(el, text, cls) {
  el.textContent = text;
  el.className = `pill ${cls}`;
}

function render() {
  if (!status) return;
  const { cli, marketplace } = status;

  /* CLI */
  $('cli-installed').textContent = cli.installed ? version(cli.version || '?') : 'Not installed';
  $('cli-latest').textContent = version(cli.latest);
  $('cli-path').textContent = cli.installed ? status.installedPath : '';
  $('cli-path').title = cli.installed ? status.installedPath : '';

  const cliPrimary = $('cli-primary');
  const cliSecondary = $('cli-secondary');

  if (!cli.installed) {
    setPill($('cli-pill'), 'Not installed', 'warn');
    cliPrimary.textContent = 'Install CLI';
    cliPrimary.dataset.action = 'install-cli';
    cliPrimary.disabled = busy;
    cliSecondary.hidden = true;
  } else if (cli.updateAvailable) {
    setPill($('cli-pill'), 'Update available', 'update');
    cliPrimary.textContent = `Update to ${version(cli.latest)}`;
    cliPrimary.dataset.action = 'update-cli';
    cliPrimary.disabled = busy;
    cliSecondary.hidden = false;
    cliSecondary.disabled = busy;
  } else {
    setPill($('cli-pill'), 'Up to date', 'ok');
    cliPrimary.textContent = 'Up to date';
    cliPrimary.dataset.action = '';
    cliPrimary.disabled = true;
    cliSecondary.hidden = false;
    cliSecondary.disabled = busy;
  }

  /* Marketplace */
  $('mp-installed').textContent = marketplace.installed
    ? marketplace.version
      ? version(marketplace.version)
      : 'Installed'
    : 'Not installed';
  $('mp-latest').textContent = version(marketplace.latest);
  $('mp-path').textContent = marketplace.installed ? status.marketplacePath : '';
  $('mp-path').title = marketplace.installed ? status.marketplacePath : '';

  const mpPrimary = $('mp-primary');
  const mpSecondary = $('mp-secondary');
  const needsCli = !cli.installed;

  if (needsCli) {
    setPill($('mp-pill'), 'Needs the CLI', 'warn');
    mpPrimary.textContent = 'Install the CLI first';
    mpPrimary.dataset.action = '';
    mpPrimary.disabled = true;
    mpSecondary.hidden = true;
  } else if (!marketplace.installed) {
    setPill($('mp-pill'), 'Not installed', 'warn');
    mpPrimary.textContent = 'Install Marketplace';
    mpPrimary.dataset.action = 'install-marketplace';
    mpPrimary.disabled = busy;
    mpSecondary.hidden = true;
  } else if (marketplace.updateAvailable) {
    setPill($('mp-pill'), 'Update available', 'update');
    mpPrimary.textContent = `Update to ${version(marketplace.latest)}`;
    mpPrimary.dataset.action = 'update-marketplace';
    mpPrimary.disabled = busy;
    mpSecondary.hidden = false;
    mpSecondary.disabled = busy;
  } else {
    setPill($('mp-pill'), 'Up to date', 'ok');
    mpPrimary.textContent = 'Up to date';
    mpPrimary.dataset.action = '';
    mpPrimary.disabled = true;
    mpSecondary.hidden = false;
    mpSecondary.disabled = busy;
  }

  const spotifyNote = status.spotify.found ? 'Spotify found' : 'Spotify not detected';
  $('env').textContent = `${status.platform} · ${spotifyNote}`;
}

async function loadStatus(force = false, silent = false) {
  if (!silent) $('refresh').disabled = true;
  try {
    const res = await fetch(`/api/status${force ? '?force=1' : ''}`);
    const data = await res.json();
    if (!data.ok) throw new Error(data.error || 'Could not read status');
    status = data.status;
    render();
    if (data.status.releaseError) addLine(`GitHub check failed: ${data.status.releaseError}`, 'error');
  } catch (err) {
    addLine(`Could not read status: ${err.message}`, 'error');
  } finally {
    $('refresh').disabled = false;
  }
}

async function act(action, options = {}) {
  if (busy || !action) return;
  setBusy(true, 'Starting…');
  barEl.classList.remove('indeterminate');
  try {
    const res = await fetch('/api/action', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ action, options }),
    });
    const data = await res.json();
    if (!res.ok) {
      addLine(data.error || 'The action could not be started', 'error');
      setBusy(false);
    }
  } catch (err) {
    addLine(`Could not reach the local server: ${err.message}`, 'error');
    setBusy(false);
  }
}

/* events */
function connect() {
  const source = new EventSource('/api/events');

  source.onmessage = (event) => {
    const msg = JSON.parse(event.data);
    switch (msg.type) {
      case 'start':
        setBusy(true, 'Working…');
        break;
      case 'log':
        addLine(msg.text, msg.level);
        if (msg.level !== 'error') barEl.classList.add('indeterminate');
        break;
      case 'progress':
        if (typeof msg.ratio === 'number') {
          barEl.classList.remove('indeterminate');
          barFill.style.width = `${Math.round(Math.max(0.02, Math.min(1, msg.ratio)) * 100)}%`;
        }
        break;
      case 'busy':
        setBusy(true, 'Working…');
        break;
      case 'finish':
        if (msg.status) {
          status = msg.status;
          render();
        }
        addLine(msg.ok ? 'Finished successfully' : 'Finished with errors', msg.ok ? 'success' : 'error');
        if (!msg.status) loadStatus(false, true);
        break;
      case 'idle':
        setBusy(false);
        break;
      default:
        break;
    }
  };

  source.onerror = () => {
    addLine('Lost connection to the local server. Restart the app if this persists.', 'error');
  };
}

/* wiring */
$('refresh').addEventListener('click', () => loadStatus(true));
$('clear').addEventListener('click', () => {
  logEl.innerHTML = '';
  hasLog = false;
});
$('cli-primary').addEventListener('click', (e) => act(e.currentTarget.dataset.action));
$('cli-secondary').addEventListener('click', () => act('install-cli'));
function spotifyOption(extra = {}) {
  return { closeSpotify: $('close-spotify').checked, ...extra };
}

$('mp-primary').addEventListener('click', (e) =>
  act(e.currentTarget.dataset.action, spotifyOption({ setCurrentTheme: $('mp-theme').checked }))
);
$('mp-secondary').addEventListener('click', () =>
  act('install-marketplace', spotifyOption({ setCurrentTheme: $('mp-theme').checked }))
);
$('repair').addEventListener('click', () => act('repair', spotifyOption()));
$('backup-apply').addEventListener('click', () => act('backup-apply', spotifyOption()));
$('apply').addEventListener('click', () => act('apply', spotifyOption()));
$('restore').addEventListener('click', () => act('restore', spotifyOption()));

connect();
loadStatus();
