"use strict";
const electron = require("electron");
const api = {
  minimize: () => electron.ipcRenderer.invoke("window-minimize"),
  maximize: () => electron.ipcRenderer.invoke("window-maximize"),
  close: () => electron.ipcRenderer.invoke("window-close"),
  platform: process.platform,
  widget: {
    toggleClickthrough: (enabled) => electron.ipcRenderer.invoke("toggle-clickthrough", enabled),
    setWidgetSize: (sizeMode) => electron.ipcRenderer.invoke("set-widget-size", sizeMode),
    getMetricsData: () => electron.ipcRenderer.invoke("get-metrics-data"),
    onMetricsUpdate: (callback) => {
      const listener = (_event, data) => {
        callback(data);
      };
      electron.ipcRenderer.on("metrics-update", listener);
      return () => {
        electron.ipcRenderer.removeListener("metrics-update", listener);
      };
    }
  },
  publishMetrics: (data) => electron.ipcRenderer.send("metrics-update", data)
};
electron.contextBridge.exposeInMainWorld("electronAPI", Object.freeze(api));
