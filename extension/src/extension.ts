import * as vscode from "vscode";
import { registerCommands } from "./commands/registerCommands";

export function activate(context: vscode.ExtensionContext): void {
  registerCommands(context);

  if (context.extensionMode === vscode.ExtensionMode.Development) {
    setTimeout(() => {
      void vscode.commands.executeCommand("apiGenerator.createApi");
    }, 300);
  }
}

export function deactivate(): void {
  // No-op.
}
