'use strict';

const http = require('http');
const fs = require('fs');
const path = require('path');
const { exec } = require('child_process');
const spicetify = require('./lib/spicetify');

const PORT_FLAG = process.argv.reduce((acc, arg) => {
  if (arg.startsWith('--port=')) return Number(arg.slice('--port='.length));
  return acc;
}, null);
const PORT = Number.isInteger(PORT_FLAG) && PORT_FLAG > 0 ? PORT_FLAG : 4173;
const PUBLIC_DIR = path.join(__dirname, 'public');
const OPEN_BROWSER = process.argv.includes('--open');

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
};

const clients = new Set();
let running = null;

function broadcast(message) {
  const payload = `data: ${JSON.stringify(message)}\n\n`;
  for (const res of clients) res.write(payload);
}

function log(text, level = 'info') {
  broadcast({ type: 'log', level, text, time: new Date().toISOString() });
  process.stdout.write(`${text}\n`);
}

function progress(ratio, received, total) {
  broadcast({ type: 'progress', ratio, received, total });
}

function sendJson(res, status, data) {
  const body = JSON.stringify(data);
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(body);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => {
      try {
        resolve(chunks.length ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : {});
      } catch (err) {
        reject(new Error('Invalid JSON body'));
      }
    });
    req.on('error', reject);
  });
}

const TOUCHES_SPOTIFY = new Set(['repair', 'apply', 'backup-apply', 'install-marketplace', 'update-marketplace']);

async function handleAction(action, options) {
  let reopen = false;
  if (TOUCHES_SPOTIFY.has(action) && options.closeSpotify !== false) {
    if (await spicetify.isSpotifyRunning()) {
      log('Closing Spotify');
      const closed = await spicetify.stopSpotify();
      if (!closed) log('Spotify is still running – file changes may fail', 'error');
      reopen = closed;
    } else {
      log('Spotify is not running');
    }
  }

  try {
    return await runAction(action, options);
  } finally {
    if (reopen) {
      if (spicetify.startSpotify()) {
        log('Reopened Spotify');
        await new Promise((resolve) => setTimeout(resolve, 1500));
      } else {
        log('Could not reopen Spotify – start it yourself', 'error');
      }
    }
  }
}

async function runAction(action, options) {
  switch (action) {
    case 'install-cli':
    case 'update-cli':
      log(`Starting Spicetify CLI ${action === 'update-cli' ? 'update' : 'install'}`, 'heading');
      return spicetify.installCli({ log, progress });
    case 'install-marketplace':
    case 'update-marketplace':
      log(`Starting Marketplace ${action === 'update-marketplace' ? 'update' : 'install'}`, 'heading');
      return spicetify.installMarketplace({ log, progress, setCurrentTheme: options.setCurrentTheme !== false });
    case 'apply':
      log('Running spicetify apply', 'heading');
      return spicetify.runCliCommand(['apply'], { log });
    case 'backup-apply': {
      log('Running spicetify backup', 'heading');
      const backup = await spicetify.runCliCommand(['backup'], { log, allowFailure: true });
      if (backup.code !== 0) log('A Spotify backup already exists – continuing with apply', 'dim');
      log('Running spicetify apply', 'heading');
      return spicetify.runCliCommand(['apply'], { log });
    }
    case 'repair':
      log('Repairing the Spicetify install', 'heading');
      return spicetify.repair({ log, progress });
    case 'restore':
      log('Running spicetify restore', 'heading');
      return spicetify.runCliCommand(['restore'], { log });
    default:
      throw new Error(`Unknown action: ${action}`);
  }
}

async function handleApi(req, res, url) {
  if (url.pathname === '/api/events') {
    res.writeHead(200, {
      'Content-Type': 'text/event-stream',
      'Cache-Control': 'no-cache',
      Connection: 'keep-alive',
    });
    res.write(': connected\n\n');
    clients.add(res);
    if (running) broadcast({ type: 'busy', action: running });
    req.on('close', () => clients.delete(res));
    return;
  }

  if (url.pathname === '/api/status') {
    try {
      const status = await spicetify.getStatus({ force: url.searchParams.has('force') });
      return sendJson(res, 200, { ok: true, status, busy: running });
    } catch (err) {
      return sendJson(res, 500, { ok: false, error: err.message });
    }
  }

  if (url.pathname === '/api/action' && req.method === 'POST') {
    if (running) return sendJson(res, 409, { ok: false, error: `Already running: ${running}` });
    let body;
    try {
      body = await readBody(req);
    } catch (err) {
      return sendJson(res, 400, { ok: false, error: err.message });
    }
    const action = String(body.action || '');
    running = action;
    sendJson(res, 202, { ok: true, action });
    broadcast({ type: 'start', action });

    handleAction(action, body.options || {})
      .then(async () => {
        log('Done', 'success');
        const status = await spicetify.getStatus({ force: true }).catch(() => null);
        broadcast({ type: 'finish', ok: true, action, status });
      })
      .catch(async (err) => {
        log(err.message, 'error');
        broadcast({ type: 'finish', ok: false, action, error: err.message });
      })
      .finally(() => {
        running = null;
        broadcast({ type: 'idle' });
      });
    return;
  }

  sendJson(res, 404, { ok: false, error: 'Not found' });
}

function serveStatic(req, res, url) {
  const requested = url.pathname === '/' ? '/index.html' : url.pathname;
  const filePath = path.join(PUBLIC_DIR, path.normalize(requested).replace(/^([/\\])+/, ''));
  if (!filePath.startsWith(PUBLIC_DIR)) {
    res.writeHead(403).end('Forbidden');
    return;
  }
  fs.readFile(filePath, (err, data) => {
    if (err) {
      res.writeHead(404, { 'Content-Type': 'text/plain' }).end('Not found');
      return;
    }
    res.writeHead(200, { 'Content-Type': MIME[path.extname(filePath)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
    res.end(data);
  });
}

function handleRequest(req, res) {
  const url = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
  if (url.pathname.startsWith('/api/')) {
    handleApi(req, res, url).catch((err) => sendJson(res, 500, { ok: false, error: err.message }));
    return;
  }
  serveStatic(req, res, url);
}

function startServer({ port = PORT, open = false } = {}) {
  return new Promise((resolve, reject) => {
    const server = http.createServer(handleRequest);
    server.once('error', reject);
    server.listen(port, '127.0.0.1', () => {
      const address = `http://127.0.0.1:${server.address().port}`;
      console.log(`Spicetify Updater running at ${address}`);
      if (open) {
        const command =
          process.platform === 'win32' ? `start "" "${address}"` : process.platform === 'darwin' ? `open "${address}"` : `xdg-open "${address}"`;
        exec(command, () => {});
      }
      resolve({ server, port: server.address().port, url: address });
    });
  });
}

if (require.main === module) {
  startServer({ port: PORT, open: OPEN_BROWSER }).catch((err) => {
    console.error(`Could not start the updater: ${err.message}`);
    process.exit(1);
  });
}

module.exports = { startServer };
