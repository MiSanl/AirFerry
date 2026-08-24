const { app, BrowserWindow, shell } = require("electron")
const path = require("node:path")

const appRoot = path.resolve(__dirname, "..")
const senderIcon = path.join(appRoot, "sender", "assets", "icon512.png")
const senderPage = app.isPackaged
  ? path.join(process.resourcesPath, "web", "index.html")
  : path.join(appRoot, "web", "dist", "index.html")

function createWindow() {
  const window = new BrowserWindow({
    width: 1280,
    height: 900,
    minWidth: 900,
    minHeight: 650,
    show: false,
    icon: senderIcon,
    title: "AirFerry Sender",
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  })

  window.setMenuBarVisibility(false)
  window.once("ready-to-show", () => window.show())
  window.webContents.setWindowOpenHandler(({ url }) => {
    void shell.openExternal(url)
    return { action: "deny" }
  })
  void window.loadFile(senderPage)
}

app.whenReady().then(() => {
  createWindow()
  app.on("activate", () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow()
  })
})

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") app.quit()
})
