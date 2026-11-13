'use strict';

const { app, BrowserWindow, Menu, dialog, shell } = require('electron');
const path = require('path');
const { startServer } = require('../server.js');

// Windows must be grouped under our own AppUserModelID, otherwise the taskbar
// shows the generic Electron icon.
if (process.platform === 'win32') app.setAppUserModelId('app.spicetify.updater');

let mainWindow = null;
let httpServer = null;

async function createWindow() {
  const { url, server } = await startServer({ port: 0 });
  httpServer = server;

  mainWindow = new BrowserWindow({
    width: 1120,
    height: 860,
    minWidth: 720,
    minHeight: 620,
    backgroundColor: '#08090a',
    title: 'Spicetify Updater',
    autoHideMenuBar: true,
    show: false,
    icon: path.join(__dirname, '..', 'build', 'icon.png'),
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      spellcheck: false,
    },
  });

  Menu.setApplicationMenu(null);
  mainWindow.once('ready-to-show', () => mainWindow.show());
  mainWindow.on('closed', () => {
    mainWindow = null;
  });

  // Keep the window on our own UI; open real links in the default browser.
  mainWindow.webContents.setWindowOpenHandler(({ url: target }) => {
    if (/^https?:/i.test(target)) shell.openExternal(target);
    return { action: 'deny' };
  });

  await mainWindow.loadURL(url);
}

app.whenReady().then(createWindow).catch((err) => {
  dialog.showErrorBox('Spicetify Updater could not start', String(err && err.message ? err.message : err));
  app.quit();
});

app.on('window-all-closed', () => {
  if (httpServer) httpServer.close();
  app.quit();
});

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) createWindow();
});
