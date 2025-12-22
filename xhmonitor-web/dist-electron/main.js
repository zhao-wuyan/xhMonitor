import { app, ipcMain, BrowserWindow, screen } from "electron";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
const __filename$1 = fileURLToPath(import.meta.url);
const __dirname$1 = path.dirname(__filename$1);
process.env.DIST = path.join(__dirname$1, "../dist");
process.env.VITE_PUBLIC = app.isPackaged ? process.env.DIST : path.join(__dirname$1, "../public");
let win;
let widgetWin;
let widgetSizeMode = "compact";
let latestMetricsData = null;
let widgetMoveSaveTimer = null;
const WIDGET_SIZES = {
  compact: { width: 220, height: 110 },
  expanded: { width: 300, height: 400 }
};
const WIDGET_MARGIN = 16;
const WIDGET_STATE_FILENAME = "widget-state.json";
const devServerUrl = process.env.VITE_DEV_SERVER_URL;
const allowedOrigins = /* @__PURE__ */ new Set();
if (devServerUrl) {
  try {
    allowedOrigins.add(new URL(devServerUrl).origin);
  } catch {
  }
}
const isAllowedUrl = (url) => {
  if (url.startsWith("file://")) return true;
  try {
    return allowedOrigins.has(new URL(url).origin);
  } catch {
    return false;
  }
};
const isTrustedSender = (event) => {
  const senderUrl = event.senderFrame?.url;
  return senderUrl ? isAllowedUrl(senderUrl) : false;
};
const getWidgetStatePath = () => path.join(app.getPath("userData"), WIDGET_STATE_FILENAME);
const readWidgetState = () => {
  try {
    const raw = fs.readFileSync(getWidgetStatePath(), "utf8");
    return JSON.parse(raw);
  } catch {
    return null;
  }
};
const writeWidgetState = () => {
  if (!widgetWin) return;
  const [x, y] = widgetWin.getPosition();
  const state = { x, y, sizeMode: widgetSizeMode };
  try {
    fs.writeFileSync(getWidgetStatePath(), JSON.stringify(state));
  } catch {
  }
};
const scheduleWidgetStateSave = () => {
  if (widgetMoveSaveTimer) clearTimeout(widgetMoveSaveTimer);
  widgetMoveSaveTimer = setTimeout(() => {
    writeWidgetState();
    widgetMoveSaveTimer = null;
  }, 250);
};
const resolveWidgetPosition = (size) => {
  const savedState = readWidgetState();
  if (savedState?.sizeMode) {
    widgetSizeMode = savedState.sizeMode;
  }
  if (savedState?.x !== void 0 && savedState?.y !== void 0) {
    const display = screen.getDisplayNearestPoint({ x: savedState.x, y: savedState.y });
    const bounds = display.workArea;
    const maxX = bounds.x + bounds.width - size.width;
    const maxY = bounds.y + bounds.height - size.height;
    const clampedX = Math.min(Math.max(savedState.x, bounds.x), maxX);
    const clampedY = Math.min(Math.max(savedState.y, bounds.y), maxY);
    return { x: clampedX, y: clampedY };
  }
  const { x, y, width, height } = screen.getPrimaryDisplay().workArea;
  return {
    x: x + width - size.width - WIDGET_MARGIN,
    y: y + height - size.height - WIDGET_MARGIN
  };
};
ipcMain.handle("window-minimize", (event) => {
  if (!isTrustedSender(event)) return;
  win?.minimize();
});
ipcMain.handle("window-maximize", (event) => {
  if (!isTrustedSender(event)) return;
  if (win?.isMaximized()) win.unmaximize();
  else win?.maximize();
});
ipcMain.handle("window-close", (event) => {
  if (!isTrustedSender(event)) return;
  win?.close();
});
ipcMain.handle("toggle-clickthrough", (event, enabled) => {
  if (!isTrustedSender(event)) return;
  widgetWin?.setIgnoreMouseEvents(!!enabled, { forward: true });
});
ipcMain.handle("set-widget-size", (event, sizeMode) => {
  if (!isTrustedSender(event)) return;
  if (!widgetWin) return;
  if (sizeMode !== "compact" && sizeMode !== "expanded") return;
  widgetSizeMode = sizeMode;
  const size = WIDGET_SIZES[sizeMode];
  const [currentX, currentY] = widgetWin.getPosition();
  const display = screen.getDisplayNearestPoint({ x: currentX, y: currentY });
  const bounds = display.workArea;
  const maxX = bounds.x + bounds.width - size.width;
  const maxY = bounds.y + bounds.height - size.height;
  const nextX = Math.min(Math.max(currentX, bounds.x), maxX);
  const nextY = Math.min(Math.max(currentY, bounds.y), maxY);
  widgetWin.setSize(size.width, size.height);
  widgetWin.setPosition(nextX, nextY);
  scheduleWidgetStateSave();
});
ipcMain.handle("get-metrics-data", (event) => {
  if (!isTrustedSender(event)) return;
  return latestMetricsData;
});
ipcMain.on("metrics-update", (event, data) => {
  if (!isTrustedSender(event)) return;
  latestMetricsData = data;
  widgetWin?.webContents.send("metrics-update", data);
});
function createWindow() {
  win = new BrowserWindow({
    width: 1200,
    height: 800,
    minWidth: 800,
    minHeight: 600,
    frame: false,
    transparent: true,
    backgroundColor: "#00000000",
    webPreferences: {
      preload: path.join(__dirname$1, "preload.js"),
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true
    }
  });
  win.on("closed", () => {
    win = null;
  });
  if (devServerUrl) {
    win.loadURL(devServerUrl);
  } else {
    win.loadFile(path.join(process.env.DIST, "index.html"));
  }
}
function createWidgetWindow() {
  const size = WIDGET_SIZES[widgetSizeMode];
  const position = resolveWidgetPosition(size);
  widgetWin = new BrowserWindow({
    width: size.width,
    height: size.height,
    x: position.x,
    y: position.y,
    alwaysOnTop: true,
    skipTaskbar: true,
    frame: false,
    transparent: true,
    resizable: false,
    backgroundColor: "#00000000",
    webPreferences: {
      preload: path.join(__dirname$1, "preload.js"),
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true
    }
  });
  widgetWin.on("closed", () => {
    widgetWin = null;
  });
  widgetWin.on("move", () => {
    scheduleWidgetStateSave();
  });
  if (devServerUrl) {
    widgetWin.loadURL(`${devServerUrl}/widget.html`);
  } else {
    widgetWin.loadFile(path.join(process.env.DIST, "widget.html"));
  }
}
app.on("web-contents-created", (_event, contents) => {
  contents.setWindowOpenHandler(() => ({ action: "deny" }));
  contents.on("will-navigate", (event, url) => {
    if (!isAllowedUrl(url)) {
      event.preventDefault();
    }
  });
});
app.on("window-all-closed", () => {
  if (process.platform !== "darwin") {
    app.quit();
  }
});
app.on("activate", () => {
  if (BrowserWindow.getAllWindows().length === 0) {
    createWindow();
  }
  if (!widgetWin) {
    createWidgetWindow();
  }
});
app.whenReady().then(() => {
  createWindow();
  createWidgetWindow();
});
