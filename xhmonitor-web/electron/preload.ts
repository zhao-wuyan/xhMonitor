import { contextBridge, ipcRenderer } from 'electron'

const api = {
  minimize: () => ipcRenderer.invoke('window-minimize'),
  maximize: () => ipcRenderer.invoke('window-maximize'),
  close: () => ipcRenderer.invoke('window-close'),
  platform: process.platform,
}

contextBridge.exposeInMainWorld('electronAPI', Object.freeze(api))
