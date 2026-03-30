import * as vscode from "vscode";

export class WorkspaceService {
  public tryGetWorkspaceFolder(): vscode.WorkspaceFolder | undefined {
    return vscode.workspace.workspaceFolders?.[0];
  }

  public getWorkspaceFolder(): vscode.WorkspaceFolder {
    const folder = this.tryGetWorkspaceFolder();
    if (!folder) {
      throw new Error("Open a workspace folder before running the API Generator.");
    }

    return folder;
  }
}