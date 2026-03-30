import * as vscode from "vscode";
import { ApiGeneratorSidebarProvider } from "../webview/ApiGeneratorPanel";
import { CliService } from "../services/CliService";
import { LlmSettingsStore } from "../services/LlmSettingsStore";
import { ProfileStore } from "../services/ProfileStore";
import { WorkspaceService } from "../services/WorkspaceService";
import { SchemaDesignerService } from "../services/SchemaDesignerService";

export function registerCommands(context: vscode.ExtensionContext): void {
  const workspaceService = new WorkspaceService();
  const profileStore = new ProfileStore(context, context.extensionUri);
  const llmSettingsStore = new LlmSettingsStore(context);
  const cliService = new CliService(workspaceService, context.extensionUri);
  const schemaDesignerService = new SchemaDesignerService(workspaceService);
  const sidebarProvider = new ApiGeneratorSidebarProvider({
    cliService,
    llmSettingsStore,
    profileStore,
    workspaceService,
    schemaDesignerService,
    extensionUri: context.extensionUri
  });

  context.subscriptions.push(
    vscode.window.registerWebviewViewProvider("apiGenerator.sidebar", sidebarProvider)
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("apiGenerator.createApi", () => sidebarProvider.showPanel("create")),
    vscode.commands.registerCommand("apiGenerator.generateFromSql", () => sidebarProvider.showPanel("generate")),
    vscode.commands.registerCommand("apiGenerator.learnCompanyStandard", () => sidebarProvider.showPanel("learn")),
    vscode.commands.registerCommand("apiGenerator.addEndpoint", () => sidebarProvider.showPanel("endpoint")),
    vscode.commands.registerCommand("apiGenerator.generateDocumentation", () => sidebarProvider.showPanel("document"))
  );
}