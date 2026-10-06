"use strict";

// E2E harness: runs the real compiled extension against a vscode stub and renders its webview in Chromium.
// Webview <-> extension messages travel over a local HTTP bridge, so every click goes through the real
// message handlers, the real CLI process and the real file system.
const fs = require("node:fs");
const http = require("node:http");
const os = require("node:os");
const path = require("node:path");
const Module = require("node:module");

const extensionRoot = path.resolve(__dirname, "..", "..");
const repoRoot = path.resolve(extensionRoot, "..");
const stubPath = path.join(__dirname, "vscode-stub.js");

const originalResolve = Module._resolveFilename;
Module._resolveFilename = function resolve(request, ...rest) {
  return request === "vscode" ? stubPath : originalResolve.call(this, request, ...rest);
};

function loadPlaywright() {
  const candidates = ["playwright-core", "playwright", "/opt/node22/lib/node_modules/playwright"];
  for (const candidate of candidates) {
    try {
      return require(candidate);
    } catch {
      // Try the next candidate.
    }
  }
  throw new Error("playwright-core is not installed. Run npm install in the extension folder.");
}

function findChromium() {
  if (process.env.CHROMIUM_PATH) {
    return process.env.CHROMIUM_PATH;
  }
  const root = process.env.PLAYWRIGHT_BROWSERS_PATH || "/opt/pw-browsers";
  const candidates = [path.join(root, "chromium")];
  if (fs.existsSync(root)) {
    for (const entry of fs.readdirSync(root).filter((name) => /^chromium-\d+$/.test(name)).sort().reverse()) {
      candidates.push(path.join(root, entry, "chrome-linux", "chrome"));
    }
  }
  return candidates.find((candidate) => fs.existsSync(candidate) && fs.statSync(candidate).isFile());
}

const BRIDGE_SCRIPT = `
(() => {
  const request = (method, url, body) => {
    const xhr = new XMLHttpRequest();
    xhr.open(method, url, false);
    xhr.setRequestHeader("Content-Type", "application/json");
    xhr.send(body === undefined ? null : JSON.stringify(body));
    return xhr.responseText;
  };
  window.acquireVsCodeApi = () => ({
    postMessage: (message) => { request("POST", "/__bridge/message", message); },
    getState: () => { const text = request("GET", "/__bridge/state"); return text ? JSON.parse(text) : undefined; },
    setState: (state) => { request("POST", "/__bridge/state", state); return state; }
  });
  window.confirm = () => true;
  // VS Code injects its theme as CSS variables on <html>; mirror the Dark Modern defaults.
  const theme = {
    "--vscode-font-family": "-apple-system, 'Segoe UI', Ubuntu, sans-serif",
    "--vscode-font-size": "13px",
    "--vscode-editor-font-family": "Menlo, Consolas, 'DejaVu Sans Mono', monospace",
    "--vscode-editor-font-size": "13px",
    "--vscode-foreground": "#cccccc",
    "--vscode-descriptionForeground": "#9d9d9d",
    "--vscode-editor-background": "#1f1f1f",
    "--vscode-editor-foreground": "#cccccc",
    "--vscode-editorWidget-background": "#202020",
    "--vscode-sideBar-background": "#181818",
    "--vscode-panel-border": "#2b2b2b",
    "--vscode-widget-border": "#313131",
    "--vscode-focusBorder": "#0078d4",
    "--vscode-input-background": "#313131",
    "--vscode-input-foreground": "#cccccc",
    "--vscode-input-border": "#3c3c3c",
    "--vscode-button-background": "#0078d4",
    "--vscode-button-foreground": "#ffffff",
    "--vscode-button-hoverBackground": "#026ec1",
    "--vscode-button-border": "#ffffff12",
    "--vscode-button-secondaryBackground": "#313131",
    "--vscode-button-secondaryForeground": "#cccccc",
    "--vscode-button-secondaryHoverBackground": "#3c3c3c",
    "--vscode-badge-background": "#616161",
    "--vscode-badge-foreground": "#f8f8f8",
    "--vscode-list-hoverBackground": "#2a2d2e",
    "--vscode-textCodeBlock-background": "#2b2b2b"
  };
  const applyTheme = () => {
    for (const [name, value] of Object.entries(theme)) {
      document.documentElement.style.setProperty(name, value);
    }
  };
  document.addEventListener("DOMContentLoaded", applyTheme, { once: true });
})();`;

class ExtensionUiHarness {
  constructor(options = {}) {
    this.options = options;
    this.artifactsDir = options.artifactsDir || path.join(repoRoot, "artifacts", "ui-e2e");
    this.pageErrors = [];
    this.consoleErrors = [];
    this.posted = [];
    this.inflight = 0;
    this.renderVersion = 0;
    this.loadedVersion = 0;
    this.pendingMessages = [];
    this.state = undefined;
    this.globalState = new Map();
    this.secrets = new Map();
  }

  async start() {
    fs.mkdirSync(this.artifactsDir, { recursive: true });
    this.workspace = fs.mkdtempSync(path.join(os.tmpdir(), "apigen-ws-"));
    fs.mkdirSync(path.join(this.workspace, "examples"), { recursive: true });
    fs.copyFileSync(path.join(repoRoot, "examples", "users.sql"), path.join(this.workspace, "examples", "users.sql"));
    for (const name of ["edge-cases.sql", "recipes.sql"]) {
      fs.copyFileSync(path.join(repoRoot, "tools", "cases", name), path.join(this.workspace, "examples", name));
    }

    this.fakeExtensionRoot = fs.mkdtempSync(path.join(os.tmpdir(), "apigen-ext-"));
    fs.symlinkSync(path.join(extensionRoot, "media"), path.join(this.fakeExtensionRoot, "media"));
    fs.mkdirSync(path.join(this.fakeExtensionRoot, "bundled"));
    fs.symlinkSync(path.join(repoRoot, "profiles"), path.join(this.fakeExtensionRoot, "bundled", "profiles"));

    process.env.API_GENERATOR_CLI_PATH = process.env.API_GENERATOR_CLI_PATH
      || path.join(repoRoot, "generator-engine", "bin", "Debug", "net8.0", "ApiGenerator.Cli.dll");

    this.vscode = require(stubPath);
    this.stub = this.vscode.__harness;
    this.stub.workspaceFolders = [{ uri: this.vscode.Uri.file(this.workspace), name: "workspace", index: 0 }];
    this.stub.on("panel", (panel) => {
      this.panel = panel;
    });
    this.stub.on("html", (webview) => {
      if (this.panel && webview === this.panel.webview) {
        this.renderVersion += 1;
        this.scheduleReload();
      }
    });
    this.stub.on("post", (webview, message) => {
      if (this.panel && webview === this.panel.webview) {
        this.posted.push(message);
        this.pendingMessages.push(message);
        void this.flushMessages();
      }
    });

    await this.startServer();
    await this.startBrowser();

    const extension = require(path.join(extensionRoot, "dist", "extension.js"));
    this.context = {
      subscriptions: [],
      extensionUri: this.vscode.Uri.file(this.fakeExtensionRoot),
      extensionPath: this.fakeExtensionRoot,
      extensionMode: this.vscode.ExtensionMode.Production,
      globalState: {
        get: (key, fallback) => (this.globalState.has(key) ? this.globalState.get(key) : fallback),
        update: async (key, value) => { this.globalState.set(key, value); }
      },
      secrets: {
        get: async (key) => this.secrets.get(key),
        store: async (key, value) => { this.secrets.set(key, value); },
        delete: async (key) => { this.secrets.delete(key); }
      }
    };
    extension.activate(this.context);
  }

  startServer() {
    this.server = http.createServer((request, response) => {
      let body = "";
      request.on("data", (chunk) => { body += chunk; });
      request.on("end", () => {
        const url = new URL(request.url, "http://127.0.0.1");
        if (url.pathname === "/webview") {
          response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
          response.end(this.panel ? this.panel.webview.html : "");
          return;
        }
        if (url.pathname.startsWith("/media/")) {
          const file = path.join(extensionRoot, "media", path.basename(url.pathname));
          response.writeHead(fs.existsSync(file) ? 200 : 404, { "Content-Type": "image/svg+xml" });
          response.end(fs.existsSync(file) ? fs.readFileSync(file) : "");
          return;
        }
        if (url.pathname === "/__bridge/state") {
          if (request.method === "POST") {
            this.state = body ? JSON.parse(body) : undefined;
            response.end("");
          } else {
            response.end(this.state === undefined ? "" : JSON.stringify(this.state));
          }
          return;
        }
        if (url.pathname === "/__bridge/message") {
          const message = JSON.parse(body);
          this.inflight += 1;
          Promise.resolve(this.panel.webview._receive(message))
            .catch((error) => this.pageErrors.push(`extension handler error: ${error && error.stack || error}`))
            .finally(() => { this.inflight -= 1; });
          response.end("");
          return;
        }
        response.writeHead(404);
        response.end("");
      });
    });
    return new Promise((resolve) => {
      this.server.listen(0, "127.0.0.1", () => {
        this.baseUrl = `http://127.0.0.1:${this.server.address().port}`;
        resolve();
      });
    });
  }

  async startBrowser() {
    const { chromium } = loadPlaywright();
    this.browser = await chromium.launch({ executablePath: findChromium(), headless: true });
    this.browserContext = await this.browser.newContext({ bypassCSP: true, viewport: { width: 1280, height: 900 } });
    await this.browserContext.addInitScript(BRIDGE_SCRIPT);
    this.page = await this.browserContext.newPage();
    this.page.on("pageerror", (error) => this.pageErrors.push(`page error: ${error.message}`));
    this.page.on("console", (message) => {
      if (message.type() === "error") {
        this.consoleErrors.push(message.text());
      }
    });
  }

  scheduleReload() {
    clearTimeout(this.reloadTimer);
    this.reloading = true;
    this.reloadTimer = setTimeout(() => void this.reload(), 25);
  }

  async reload() {
    const version = this.renderVersion;
    try {
      await this.page.goto(`${this.baseUrl}/webview?v=${version}`, { waitUntil: "load" });
    } catch (error) {
      if (!String(error).includes("interrupted")) {
        this.pageErrors.push(`navigation error: ${error.message}`);
      }
    }
    if (version === this.renderVersion) {
      this.loadedVersion = version;
      this.reloading = false;
      await this.flushMessages();
    }
  }

  async flushMessages() {
    if (this.reloading || this.flushing) {
      return;
    }
    this.flushing = true;
    try {
      while (this.pendingMessages.length > 0 && !this.reloading) {
        const message = this.pendingMessages.shift();
        await this.page.evaluate((data) => window.dispatchEvent(new MessageEvent("message", { data })), message);
      }
    } finally {
      this.flushing = false;
    }
  }

  async waitIdle(timeoutMs = 180000) {
    const started = Date.now();
    let stableSince = 0;
    while (Date.now() - started < timeoutMs) {
      const idle = this.inflight === 0 && !this.reloading && this.pendingMessages.length === 0 && !this.flushing
        && this.loadedVersion === this.renderVersion;
      if (idle) {
        stableSince = stableSince || Date.now();
        if (Date.now() - stableSince > 150) {
          return;
        }
      } else {
        stableSince = 0;
      }
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    throw new Error(`UI did not become idle (inflight=${this.inflight}, reloading=${this.reloading}).`);
  }

  async openMode(mode) {
    const commandIds = {
      create: "apiGenerator.createApi",
      generate: "apiGenerator.generateFromSql",
      learn: "apiGenerator.learnCompanyStandard",
      endpoint: "apiGenerator.addEndpoint",
      document: "apiGenerator.generateDocumentation"
    };
    await this.vscode.commands.executeCommand(commandIds[mode]);
    await this.waitIdle();
  }

  async screenshot(name) {
    const file = path.join(this.artifactsDir, `${name}.png`);
    await this.page.screenshot({ path: file, fullPage: true });
    return file;
  }

  setCliPath(cliPath) {
    if (cliPath === undefined) {
      delete process.env.API_GENERATOR_CLI_PATH;
    } else {
      process.env.API_GENERATOR_CLI_PATH = cliPath;
    }
  }

  lastResult() {
    return [...this.posted].reverse().find((message) => message.type === "result");
  }

  async stop() {
    await this.browser?.close();
    await new Promise((resolve) => (this.server ? this.server.close(resolve) : resolve()));
    fs.rmSync(this.workspace, { recursive: true, force: true });
    fs.rmSync(this.fakeExtensionRoot, { recursive: true, force: true });
  }
}

module.exports = { ExtensionUiHarness, repoRoot };
