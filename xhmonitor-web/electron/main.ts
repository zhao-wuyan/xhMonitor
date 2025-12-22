import { app, BrowserWindow, ipcMain } from 'electron'
import path from 'node:path'

process.env.DIST = path.join(__dirname, '../dist')
process.env.VITE_PUBLIC = app.isPackaged ? process.env.DIST : path.join(__dirname, '../public')

let win: BrowserWindow | null

const devServerUrl = process.env.VITE_DEV_SERVER_URL
const allowedOrigins = new Set<string>()

if (devServerUrl) {
  try {
    allowedOrigins.add(new URL(devServerUrl).origin)
  } catch {
    // Ignore malformed dev URL; navigation will be blocked by default.
  }
}

const isAllowedUrl = (url: string) => {
  if (url.startsWith('file://')) return true
  try {
    return allowedOrigins.has(new URL(url).origin)
  } catch {
    return false
  }
}

const isTrustedSender = (event: Electron.IpcMainInvokeEvent) => {
  const senderUrl = event.senderFrame?.url
  return senderUrl ? isAllowedUrl(senderUrl) : false
}

ipcMain.handle('window-minimize', (event) => {
  if (!isTrustedSender(event)) return
  win?.minimize()
})

ipcMain.handle('window-maximize', (event) => {
  if (!isTrustedSender(event)) return
  if (win?.isMaximized()) win.unmaximize()
  else win?.maximize()
})

ipcMain.handle('window-close', (event) => {
  if (!isTrustedSender(event)) return
  win?.close()
})

function createWindow() {
  win = new BrowserWindow({
    width: 1200,
    height: 800,
    minWidth: 800,
    minHeight: 600,
    frame: false,
    transparent: true,
    backgroundColor: '#00000000',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true,
    },
  })

  win.on('closed', () => {
    win = null
  })

  if (devServerUrl) {
    win.loadURL(devServerUrl)
  } else {
    win.loadFile(path.join(process.env.DIST, 'index.html'))
  }
}

app.on('web-contents-created', (_event, contents) => {
  contents.setWindowOpenHandler(() => ({ action: 'deny' }))
  contents.on('will-navigate', (event, url) => {
    if (!isAllowedUrl(url)) {
      event.preventDefault()
    }
  })
})

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit()
  }
})

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) {
    createWindow()
  }
})

app.whenReady().then(createWindow)
