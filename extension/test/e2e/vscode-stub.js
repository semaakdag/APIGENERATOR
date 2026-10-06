"use strict";

// Minimal stand-in for the `vscode` module so the real compiled extension can run in Node during E2E tests.
// It covers exactly the API surface the extension uses; the test harness drives the dialogs and webviews.
const path = require("node:path");
const { EventEmitter } = require("node:events");

class Uri {
  constructor(fsPath) {
    this.fsPath = path.resolve(fsPath);
    this.scheme = "file";
    this.path = this.fsPath;
  }

  static file(fsPath) {
    return new Uri(fsPath);
  }

  static joinPath(base, ...segments) {
    return new Uri(path.join(base.fsPath, ...segments));
  }

  toString() {
    return `file://${this.fsPath}`;
  }
}

const harness = new EventEmitter();
harness.openDialogAnswers = [];
harness.messages = [];
harness.openedDocuments = [];
harness.outputLines = [];
harness.commands = new Map();
harness.webviewViewProviders = new Map();
harness.workspaceFolders = [];
harness.executedCommands = [];

function createWebview() {
  const receiveListeners = [];
  const webview = {
    options: {},
    cspSource: "http://127.0.0.1",
    _html: "",
    get html() {
      return this._html;
    },
    set html(value) {
      this._html = value;
      harness.emit("html", webview, value);
    },
    asWebviewUri(uri) {
      return `/media/${path.basename(uri.fsPath)}`;
    },
    postMessage(message) {
      harness.emit("post", webview, message);
      return Promise.resolve(true);
    },
    onDidReceiveMessage(listener) {
      receiveListeners.push(listener);
      return { dispose() {} };
    },
    _receive(message) {
      return Promise.all(receiveListeners.map((listener) => listener(message)));
    }
  };
  return webview;
}

const window = {
  createOutputChannel(name) {
    return {
      name,
      append(value) {
        harness.outputLines.push(String(value));
      },
      appendLine(value) {
        harness.outputLines.push(`${value}\n`);
      },
      show() {},
      dispose() {}
    };
  },
  createWebviewPanel(viewType, title) {
    const disposeListeners = [];
    const panel = {
      viewType,
      title,
      webview: createWebview(),
      reveal() {},
      onDidDispose(listener) {
        disposeListeners.push(listener);
        return { dispose() {} };
      },
      dispose() {
        disposeListeners.forEach((listener) => listener());
      }
    };
    harness.emit("panel", panel);
    return panel;
  },
  registerWebviewViewProvider(viewId, provider) {
    harness.webviewViewProviders.set(viewId, provider);
    return { dispose() {} };
  },
  async showOpenDialog(options) {
    harness.messages.push({ kind: "openDialog", options });
    const answer = harness.openDialogAnswers.shift();
    return answer ? answer.map((item) => Uri.file(item)) : undefined;
  },
  async showErrorMessage(message) {
    harness.messages.push({ kind: "error", message });
    return undefined;
  },
  async showInformationMessage(message) {
    harness.messages.push({ kind: "info", message });
    return undefined;
  },
  async showWarningMessage(message) {
    harness.messages.push({ kind: "warning", message });
    return undefined;
  },
  async showTextDocument(document) {
    harness.openedDocuments.push(document.uri ? document.uri.fsPath : String(document));
    return {};
  }
};

const workspace = {
  get workspaceFolders() {
    return harness.workspaceFolders;
  },
  async openTextDocument(uri) {
    return { uri: typeof uri === "string" ? Uri.file(uri) : uri };
  }
};

const commands = {
  registerCommand(id, callback) {
    harness.commands.set(id, callback);
    return { dispose() {} };
  },
  async executeCommand(id, ...args) {
    harness.executedCommands.push({ id, args });
    const callback = harness.commands.get(id);
    return callback ? callback(...args) : undefined;
  }
};

module.exports = {
  Uri,
  window,
  workspace,
  commands,
  ViewColumn: { One: 1, Two: 2, Beside: -2 },
  ExtensionMode: { Production: 1, Development: 2, Test: 3 },
  __harness: harness
};
