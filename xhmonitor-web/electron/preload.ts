import { contextBridge, ipcRenderer } from 'electron'
import type { MetricsData } from '../src/types'

const api = {
  minimize: () => ipcRenderer.invoke('window-minimize'),
  maximize: () => ipcRenderer.invoke('window-maximize'),
  close: () => ipcRenderer.invoke('window-close'),
  platform: process.platform,
  widget: {
    toggleClickthrough: (enabled: boolean) => ipcRenderer.invoke('toggle-clickthrough', enabled),
    setWidgetSize: (sizeMode: 'compact' | 'expanded') => ipcRenderer.invoke('set-widget-size', sizeMode),
    getMetricsData: () => ipcRenderer.invoke('get-metrics-data'),
    onMetricsUpdate: (callback: (data: MetricsData) => void) => {
      const listener = (_event: Electron.IpcRendererEvent, data: MetricsData) => {
        callback(data)
      }
      ipcRenderer.on('metrics-update', listener)
      return () => {
        ipcRenderer.removeListener('metrics-update', listener)
      }
    },
  },
  publishMetrics: (data: MetricsData) => ipcRenderer.send('metrics-update', data),
}

contextBridge.exposeInMainWorld('electronAPI', Object.freeze(api))
