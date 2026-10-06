import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { CliService } from "../services/CliService";
import { LlmSettingsStore, ResolvedLlmSettings, StoredLlmSettings } from "../services/LlmSettingsStore";
import { ProfileStore, StoredProfileSummary } from "../services/ProfileStore";
import { WorkspaceService } from "../services/WorkspaceService";
import { SchemaDesignerDocument, SchemaDesignerService, SchemaDesignerTable } from "../services/SchemaDesignerService";
import { RecentValues, RecentValuesStore } from "../services/RecentValuesStore";

interface PanelDependencies {
  cliService: CliService;
  llmSettingsStore: LlmSettingsStore;
  profileStore: ProfileStore;
  workspaceService: WorkspaceService;
  schemaDesignerService: SchemaDesignerService;
  recentValuesStore: RecentValuesStore;
  extensionUri: vscode.Uri;
}

interface RunPayload {
  schema?: string;
  output?: string;
  project?: string;
  profile?: string;
  framework?: string;
  connectionString?: string;
  overwriteMode?: string;
  featureWindowsAuthentication?: boolean;
  featureUnitTests?: boolean;
  featurePostmanCollection?: boolean;
  llmEnabled?: boolean;
  llmUrl?: string;
  llmModel?: string;
  llmMaxConcurrency?: number | string;
  llmToken?: string;
  entity?: string;
  recipe?: string;
  field?: string;
  preview?: boolean;
}

interface ModeDefinition {
  id: string;
  title: string;
  subtitle: string;
  actionLabel: string;
  badgeLabel: string;
}

const MODE_DEFINITIONS: Record<string, ModeDefinition> = {
  create: {
    id: "create",
    title: "API Oluştur",
    subtitle: "Bir SQL şemasından başlayıp temiz ve standart bir API iskeleti üretin.",
    actionLabel: "API Üret",
    badgeLabel: "Oluştur"
  },
  generate: {
    id: "generate",
    title: "SQL'den Üret",
    subtitle: "Var olan bir şema ve isteğe bağlı şirket profiliyle üretimi çalıştırın.",
    actionLabel: "API Üret",
    badgeLabel: "Üret"
  },
  learn: {
    id: "learn",
    title: "Şirket Standardını Öğren",
    subtitle: "Mevcut kod tabanını inceleyip kuralları yeniden kullanılabilir bir profile dönüştürün.",
    actionLabel: "Standardı Öğren",
    badgeLabel: "Öğren"
  },
  endpoint: {
    id: "endpoint",
    title: "Endpoint Ekle",
    subtitle: "Aynı standart profil yüzeyini koruyarak yeni endpoint akışını hazırlayın.",
    actionLabel: "Endpoint Ekle",
    badgeLabel: "Endpoint"
  },
  document: {
    id: "document",
    title: "Dokümantasyon Üret",
    subtitle: "Mevcut üretilmiş çözüm yapısı için dokümantasyon oluşturun.",
    actionLabel: "Doküman Üret",
    badgeLabel: "Doküman"
  }
};

export class ApiGeneratorSidebarProvider implements vscode.WebviewViewProvider {
  private view: vscode.WebviewView | undefined;
  private panel: vscode.WebviewPanel | undefined;
  private currentMode = "create";
  private profiles: StoredProfileSummary[] = [];
  private llmSettings: StoredLlmSettings = {
    enabled: false,
    url: "",
    model: "",
    maxConcurrency: undefined,
    hasToken: false
  };

  public constructor(private readonly dependencies: PanelDependencies) {}

  public resolveWebviewView(webviewView: vscode.WebviewView): void {
    this.view = webviewView;
    this.configureWebview(webviewView.webview);
    webviewView.webview.onDidReceiveMessage((message) => this.handleMessage(message));
    this.render();
    void this.refreshData();
  }

  public async show(mode: string): Promise<void> {
    this.currentMode = mode;
    await this.showPanel();
    await vscode.commands.executeCommand("workbench.view.explorer");
    try {
      await vscode.commands.executeCommand("apiGenerator.sidebar.focus");
    } catch {
      // Explorer focus is an acceptable fallback.
    }
    this.view?.show?.(true);
    this.render();
  }

  public async showPanel(mode = this.currentMode): Promise<void> {
    this.currentMode = mode;

    if (!this.panel) {
      this.panel = vscode.window.createWebviewPanel(
        "apiGenerator.panel",
        "API Generator",
        vscode.ViewColumn.One,
        {
          enableScripts: true,
          retainContextWhenHidden: true
        }
      );

      this.configureWebview(this.panel.webview);
      this.panel.onDidDispose(() => {
        this.panel = undefined;
      });
      this.panel.webview.onDidReceiveMessage((message) => this.handleMessage(message));
    }

    await this.refreshData();
    this.panel.title = "API Generator";
    this.panel.reveal(vscode.ViewColumn.One, false);
    this.render();
  }

  private async handleMessage(message: { type: string; payload?: any }): Promise<void> {
    if (message.type === "run") {
      const payload = message.payload ?? {};
      const llmSettings = await this.dependencies.llmSettingsStore.resolveForExecution({
        enabled: payload.llmEnabled,
        url: payload.llmUrl,
        model: payload.llmModel,
        maxConcurrency: this.parseOptionalPositiveInteger(payload.llmMaxConcurrency),
        token: payload.llmToken
      });

      if (llmSettings.enabled && (!llmSettings.url || !llmSettings.model || !llmSettings.token)) {
        this.postMessage({
          type: "result",
          payload: {
            exitCode: 1,
            stdout: "",
            stderr: "LLM desteği etkin, ancak URL, model veya token eksik."
          }
        });
        return;
      }

      this.llmSettings = {
        enabled: llmSettings.enabled,
        url: llmSettings.url,
        model: llmSettings.model,
        maxConcurrency: llmSettings.maxConcurrency,
        hasToken: llmSettings.token.trim().length > 0
      };

      const args = this.buildArgs(payload, llmSettings);
      if (payload.preview === true && this.supportsPreview()) {
        args.push("--dry-run");
      }

      if (payload.profile) {
        await this.dependencies.profileStore.rememberProfile(payload.profile);
      }

      const result = await this.dependencies.cliService.execute({
        command: this.mapModeToCommand(this.currentMode),
        args,
        llmToken: llmSettings.enabled ? llmSettings.token : undefined
      });

      if (this.currentMode === "learn") {
        await this.refreshProfiles();
      }

      if (result.exitCode === 0 && payload.preview !== true) {
        const recent = await this.dependencies.recentValuesStore.remember({
          schema: this.currentMode === "create" || this.currentMode === "generate" ? payload.schema : undefined,
          output: this.currentMode === "endpoint" ? undefined : payload.output,
          project: payload.project
        });
        this.postMessage({ type: "recent", payload: recent });
      }

      this.postMessage({
        type: "result",
        payload: { ...result, command: this.mapModeToCommand(this.currentMode), preview: payload.preview === true, project: payload.project }
      });
      return;
    }

    if (message.type === "load-entities") {
      const project = String(message.payload?.project || "").trim();
      const result = project.length === 0
        ? { exitCode: 1, stdout: "", stderr: "Önce çözüm klasörünü seçin.", events: [] }
        : await this.dependencies.cliService.execute({ command: "entities", args: ["--project", project] });
      const entitiesEvent = result.events.find((event) => event.event === "entities");
      this.postMessage({
        type: "entities",
        payload: {
          project,
          entities: result.exitCode === 0 && entitiesEvent ? entitiesEvent.entities : [],
          error: result.exitCode === 0 ? "" : (result.stderr || result.stdout || "Varlıklar okunamadı.")
        }
      });
      return;
    }

    if (message.type === "save-llm-settings") {
      this.llmSettings = await this.dependencies.llmSettingsStore.saveSettings({
        enabled: message.payload?.llmEnabled,
        url: message.payload?.llmUrl,
        model: message.payload?.llmModel,
        maxConcurrency: this.parseOptionalPositiveInteger(message.payload?.llmMaxConcurrency),
        token: message.payload?.llmToken
      });
      this.postMessage({
        type: "llm-settings",
        payload: this.llmSettings
      });
      return;
    }

    if (message.type === "load-schema-designer") {
      try {
        const document = await this.dependencies.schemaDesignerService.loadSchema(String(message.payload?.schema || ""));
        this.postSchemaDesignerData(document, `Seçilen şema dosyasından ${document.tables.length} tablo yüklendi.`);
      } catch (error) {
        this.postSchemaDesignerError(error);
      }
      return;
    }

    if (message.type === "append-schema-table") {
      try {
        const document = await this.dependencies.schemaDesignerService.appendTable(
          String(message.payload?.schema || ""),
          message.payload?.table as SchemaDesignerTable
        );
        this.postSchemaDesignerData(document, `'${message.payload?.table?.name || ""}' tablosu şema dosyasına eklendi.`);
      } catch (error) {
        this.postSchemaDesignerError(error);
      }
      return;
    }

    if (message.type === "delete-schema-table") {
      try {
        const document = await this.dependencies.schemaDesignerService.deleteTable(
          String(message.payload?.schema || ""),
          String(message.payload?.tableName || "")
        );
        this.postSchemaDesignerData(document, `'${message.payload?.tableName || ""}' tablosu şema dosyasından kaldırıldı.`);
      } catch (error) {
        this.postSchemaDesignerError(error);
      }
      return;
    }

    if (message.type === "open-file") {
      await this.openFileAsync(String(message.payload?.path || ""));
      return;
    }

    if (message.type === "build-cli") {
      await this.dependencies.cliService.ensureBuilt();
      return;
    }

    if (message.type === "refresh-profiles") {
      await this.refreshData();
      return;
    }

    if (message.type === "pick-project-folder") {
      await this.pickProjectFolderAsync(String(message.payload?.currentProject || ""));
      return;
    }

    if (message.type === "pick-project-file") {
      await this.pickProjectFileAsync(String(message.payload?.currentProject || ""));
      return;
    }

    if (message.type === "pick-schema-file") {
      await this.pickSchemaFileAsync(String(message.payload?.currentSchema || ""));
    }
  }

  private buildArgs(payload: RunPayload, llmSettings: ResolvedLlmSettings): string[] {
    const args: string[] = [];
    const add = (key: string, value: string | undefined): void => {
      if (value && value.trim().length > 0) {
        args.push(`--${key}`, value.trim());
      }
    };
    const addFeatureToggle = (key: string, enabled: boolean | undefined): void => {
      if (typeof enabled === "boolean") {
        args.push(`--${key}`, enabled ? "Enable" : "Disable");
      }
    };

    if (this.currentMode === "learn" || this.currentMode === "analyze") {
      add("project", payload.project);
      if (this.currentMode === "learn") {
        add("output", payload.output);
      }
      return args;
    }

    if (this.currentMode === "document") {
      add("output", payload.output);
      return args;
    }

    if (this.currentMode === "endpoint") {
      add("project", payload.project);
      add("entity", payload.entity);
      add("recipe", payload.recipe);
      add("field", payload.field);
      return args;
    }

    const usesDefaultFramework = !payload.framework || payload.framework.trim().length === 0;

    add("schema", payload.schema);
    add("output", payload.output);
    if (usesDefaultFramework) {
      add("profile", payload.profile);
    }
    add("framework", payload.framework);
    if (!payload.framework || payload.framework.trim().length === 0) {
      add("project", payload.project);
    }
    add("connection-string", payload.connectionString);
    add("overwrite-mode", payload.overwriteMode);

    if (payload.framework && payload.framework.trim().length > 0) {
      addFeatureToggle("windows-auth", payload.featureWindowsAuthentication);
      addFeatureToggle("unit-tests", payload.featureUnitTests);
      addFeatureToggle("postman-collection", payload.featurePostmanCollection);
    }

    if (llmSettings.enabled) {
      args.push("--llm-enabled");
      add("llm-url", llmSettings.url);
      add("llm-model", llmSettings.model);
      if (typeof llmSettings.maxConcurrency === "number" && llmSettings.maxConcurrency > 0) {
        args.push("--llm-max-concurrency", String(llmSettings.maxConcurrency));
      }
    }

    return args;
  }

  private async refreshProfiles(): Promise<void> {
    try {
      const workspaceFolder = this.dependencies.workspaceService.getWorkspaceFolder();
      this.profiles = await this.dependencies.profileStore.listProfiles(workspaceFolder.uri.fsPath);
    } catch {
      this.profiles = [];
    }

    this.render();
  }

  private async refreshData(): Promise<void> {
    await Promise.all([
      this.refreshProfiles(),
      this.refreshLlmSettings()
    ]);
  }

  private async refreshLlmSettings(): Promise<void> {
    this.llmSettings = await this.dependencies.llmSettingsStore.getSettings();
    this.render();
  }

  private parseOptionalPositiveInteger(value: unknown): number | undefined {
    if (typeof value === "number" && Number.isInteger(value) && value > 0) {
      return value;
    }

    if (typeof value === "string") {
      const trimmed = value.trim();
      if (trimmed.length === 0) {
        return undefined;
      }

      const parsed = Number.parseInt(trimmed, 10);
      if (Number.isInteger(parsed) && parsed > 0) {
        return parsed;
      }
    }

    return undefined;
  }

  private configureWebview(webview: vscode.Webview): void {
    webview.options = {
      enableScripts: true,
      localResourceRoots: [vscode.Uri.joinPath(this.dependencies.extensionUri, "media")]
    };
  }

  private postMessage(message: { type: string; payload?: unknown }): void {
    void this.view?.webview.postMessage(message);
    void this.panel?.webview.postMessage(message);
  }

  private postSchemaDesignerData(document: SchemaDesignerDocument, message: string): void {
    this.postMessage({
      type: "schema-designer-data",
      payload: {
        ...document,
        message
      }
    });
  }

  private postSchemaDesignerError(error: unknown): void {
    this.postMessage({
      type: "schema-designer-error",
      payload: {
        message: error instanceof Error ? error.message : "Şema tasarımcısı işlemi başarısız oldu."
      }
    });
  }
  private postProjectPickError(error: unknown): void {
    this.postMessage({
      type: "project-pick-error",
      payload: {
        message: error instanceof Error ? error.message : "Proje seçici açılamadı."
      }
    });
  }

  private postSchemaPickError(error: unknown): void {
    this.postMessage({
      type: "schema-pick-error",
      payload: {
        message: error instanceof Error ? error.message : "Şema dosyası seçici açılamadı."
      }
    });
  }

  private resolveProjectPickerDefaultUri(currentProjectPath?: string): vscode.Uri | undefined {
    const candidate = (currentProjectPath ?? "").trim();

    if (candidate.length > 0) {
      try {
        const fullPath = path.resolve(candidate);
        if (fs.existsSync(fullPath)) {
          const stats = fs.statSync(fullPath);
          if (stats.isFile()) {
            return vscode.Uri.file(path.dirname(fullPath));
          }

          if (stats.isDirectory()) {
            return vscode.Uri.file(fullPath);
          }
        }
      } catch {
        // Fall back to the workspace folder.
      }
    }

    try {
      return this.dependencies.workspaceService.getWorkspaceFolder().uri;
    } catch {
      return undefined;
    }
  }

  private async pickProjectFolderAsync(currentProjectPath?: string): Promise<void> {
    try {
      const selection = await vscode.window.showOpenDialog({
        canSelectFiles: false,
        canSelectFolders: true,
        canSelectMany: false,
        openLabel: "Referans Proje Klasörünü Seç",
        defaultUri: this.resolveProjectPickerDefaultUri(currentProjectPath)
      });

      if (selection && selection.length > 0) {
        this.postMessage({
          type: "project-picked",
          payload: {
            project: selection[0].fsPath
          }
        });
      }
    } catch (error) {
      this.postProjectPickError(error);
      void vscode.window.showErrorMessage(error instanceof Error ? error.message : "Referans proje klasörü seçici açılamadı.");
    }
  }

  private async pickProjectFileAsync(currentProjectPath?: string): Promise<void> {
    try {
      const selection = await vscode.window.showOpenDialog({
        canSelectFiles: true,
        canSelectFolders: false,
        canSelectMany: false,
        filters: {
          "Proje Dosyaları": ["csproj", "sln"]
        },
        openLabel: "Proje dosyasını seç",
        defaultUri: this.resolveProjectPickerDefaultUri(currentProjectPath)
      });

      if (selection && selection.length > 0) {
        this.postMessage({
          type: "project-picked",
          payload: {
            project: selection[0].fsPath
          }
        });
      }
    } catch (error) {
      this.postProjectPickError(error);
      void vscode.window.showErrorMessage(error instanceof Error ? error.message : "Proje dosyası seçici açılamadı.");
    }
  }

  private async pickSchemaFileAsync(currentSchemaPath?: string): Promise<void> {
    try {
      const selection = await vscode.window.showOpenDialog({
        canSelectFiles: true,
        canSelectFolders: false,
        canSelectMany: false,
        filters: {
          "SQL Dosyaları": ["sql"]
        },
        openLabel: "Şema dosyasını seç",
        defaultUri: this.resolveProjectPickerDefaultUri(currentSchemaPath)
      });

      if (selection && selection.length > 0) {
        this.postMessage({
          type: "schema-picked",
          payload: {
            schema: selection[0].fsPath
          }
        });
      }
    } catch (error) {
      this.postSchemaPickError(error);
      void vscode.window.showErrorMessage(error instanceof Error ? error.message : "Şema dosyası seçici açılamadı.");
    }
  }

  private async openFileAsync(filePath: string): Promise<void> {
    if (filePath.trim().length === 0 || !fs.existsSync(filePath)) {
      void vscode.window.showErrorMessage(`Dosya bulunamadı: ${filePath}`);
      return;
    }

    await vscode.commands.executeCommand("vscode.open", vscode.Uri.file(filePath));
  }

  private supportsPreview(): boolean {
    return this.currentMode === "create" || this.currentMode === "generate" || this.currentMode === "endpoint";
  }

  private mapModeToCommand(mode: string): "generate" | "learn" | "analyze" | "document" | "add-endpoint" {
    if (mode === "learn") {
      return "learn";
    }

    if (mode === "endpoint") {
      return "add-endpoint";
    }

    if (mode === "document") {
      return "document";
    }

    return "generate";
  }

  private getHtml(webview: vscode.Webview): string {
    const mode = this.getModeDefinition();
    const nonce = getNonce();
    const iconUri = webview.asWebviewUri(vscode.Uri.joinPath(this.dependencies.extensionUri, "media", "api-generator.svg"));
    const primaryFieldsHtml = this.getPrimaryFieldsHtml(mode.id);
    const configurationTabHtml = this.getConfigurationTabHtml(mode.id);
    const databaseTabHtml = this.getDatabaseTabHtml(mode.id);
    const runtimeTabHtml = this.getRuntimeTabHtml(mode.id);
    const llmTabHtml = this.getLlmTabHtml();
    const configurationTabLabel = this.getConfigurationTabLabel(mode.id);
    const frameworkFeatureDefaultsJson = JSON.stringify(this.getFrameworkFeatureDefaults()).replaceAll("<", "\\u003c");
    const frameworkBehaviorJson = JSON.stringify(this.getFrameworkBehavior()).replaceAll("<", "\\u003c");
    const workflowStepsHtml = this.getWorkflowStepsHtml(mode.id);
    const standardProfiles = this.getStandardProfiles().length;
    const frameworkProfiles = this.getFrameworkProfiles().length;

    return `<!DOCTYPE html>
<html lang="tr">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <meta
      http-equiv="Content-Security-Policy"
      content="default-src 'none'; img-src ${webview.cspSource} data:; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}';"
    />
    <title>API Generator</title>
    <style>
      :root {
        color-scheme: light dark;
      }

      * {
        box-sizing: border-box;
      }

      html,
      body {
        margin: 0;
        padding: 0;
        min-height: 100%;
        background: var(--vscode-editor-background);
        color: var(--vscode-foreground);
        font-family: var(--vscode-font-family);
        font-size: var(--vscode-font-size);
      }

      body {
        line-height: 1.45;
      }

      code,
      pre,
      .mono {
        font-family: var(--vscode-editor-font-family);
        font-size: var(--vscode-editor-font-size);
      }

      .shell {
        display: grid;
        gap: 12px;
        padding: 12px;
      }

      .hero,
      .panel {
        border: 1px solid var(--vscode-panel-border, var(--vscode-widget-border, transparent));
        border-radius: 12px;
        background: var(--vscode-editorWidget-background, var(--vscode-sideBar-background));
        overflow: hidden;
      }

      .hero {
        position: relative;
        display: grid;
        grid-template-columns: auto 1fr;
        gap: 12px;
        padding: 14px;
        background:
          linear-gradient(135deg, var(--vscode-editorWidget-background, var(--vscode-sideBar-background)) 0%, var(--vscode-sideBar-background) 100%);
      }

      .hero::after {
        content: "";
        position: absolute;
        inset: 0;
        pointer-events: none;
        background:
          linear-gradient(120deg, transparent 0%, transparent 55%, var(--vscode-button-background) 100%);
        opacity: 0.08;
      }

      .hero-icon {
        display: grid;
        place-items: center;
        width: 42px;
        height: 42px;
        border-radius: 12px;
        background: var(--vscode-input-background);
        border: 1px solid var(--vscode-input-border, transparent);
      }

      .hero-icon img {
        width: 24px;
        height: 24px;
      }

      .hero-copy {
        position: relative;
        z-index: 1;
      }

      .hero-kicker {
        display: inline-flex;
        align-items: center;
        gap: 8px;
        margin-bottom: 4px;
        color: var(--vscode-descriptionForeground);
        text-transform: uppercase;
        letter-spacing: 0.08em;
        font-size: 10px;
        font-weight: 700;
      }

      .hero-kicker::before {
        content: "";
        width: 8px;
        height: 8px;
        border-radius: 999px;
        background: var(--vscode-button-background);
      }

      .hero h1 {
        margin: 0 0 4px;
        font-size: 17px;
        line-height: 1.2;
        color: var(--vscode-editor-foreground);
      }

      .hero p {
        margin: 0;
        color: var(--vscode-descriptionForeground);
        font-size: 12px;
      }

      .mode-badge {
        display: inline-flex;
        margin-top: 10px;
        padding: 3px 8px;
        border-radius: 999px;
        border: 1px solid var(--vscode-button-border, transparent);
        background: var(--vscode-badge-background);
        color: var(--vscode-badge-foreground);
        font-size: 11px;
        font-weight: 700;
      }

      .workflow-strip {
        position: relative;
        z-index: 1;
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(130px, 1fr));
        gap: 8px;
        margin-top: 12px;
      }

      .workflow-step {
        display: grid;
        gap: 2px;
        padding: 9px 10px;
        border-radius: 10px;
        border: 1px solid var(--vscode-widget-border, transparent);
        background: var(--vscode-textCodeBlock-background);
      }

      .workflow-step-number {
        color: var(--vscode-descriptionForeground);
        font-size: 10px;
        font-weight: 700;
        letter-spacing: 0.06em;
        text-transform: uppercase;
      }

      .workflow-step-title {
        color: var(--vscode-editor-foreground);
        font-size: 12px;
        font-weight: 700;
      }

      .workflow-step-note {
        color: var(--vscode-descriptionForeground);
        font-size: 11px;
      }

      .panel-header {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        gap: 12px;
        padding: 14px 14px 0;
      }

      .panel-title-wrap {
        display: grid;
        gap: 4px;
      }

      .panel-title {
        margin: 0;
        font-size: 14px;
        font-weight: 700;
      }

      .panel-note {
        color: var(--vscode-descriptionForeground);
        font-size: 12px;
      }

      .panel-body {
        display: grid;
        gap: 12px;
        padding: 12px 14px 14px;
      }

      .primary-grid {
        display: grid;
        gap: 10px;
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }

      .tab-stack {
        display: grid;
        gap: 10px;
      }

      .field {
        display: grid;
        gap: 5px;
      }

      .field-label {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
        color: var(--vscode-foreground);
        font-size: 12px;
        font-weight: 600;
      }

      .field-caption {
        color: var(--vscode-descriptionForeground);
        font-size: 11px;
      }

      .field-input-row {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        gap: 8px;
        align-items: center;
      }

      .field-input-row .ghost-button {
        width: auto;
        white-space: nowrap;
      }

      input,
      textarea,
      select,
      button {
        width: 100%;
        border-radius: 8px;
        transition: border-color 120ms ease, box-shadow 120ms ease, background-color 120ms ease;
      }

      input,
      textarea,
      select {
        padding: 9px 10px;
        border: 1px solid var(--vscode-input-border, transparent);
        background: var(--vscode-input-background);
        color: var(--vscode-input-foreground);
        outline: none;
      }

      input:focus,
      textarea:focus,
      select:focus {
        border-color: var(--vscode-focusBorder);
        box-shadow: 0 0 0 1px var(--vscode-focusBorder);
      }

      textarea {
        min-height: 84px;
        resize: vertical;
        line-height: 1.4;
      }

      .primary-actions {
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        align-items: center;
        gap: 12px;
      }

      .primary-hint {
        color: var(--vscode-descriptionForeground);
        font-size: 11px;
      }

      button {
        padding: 9px 12px;
        border: 1px solid var(--vscode-button-border, transparent);
        background: var(--vscode-button-background);
        color: var(--vscode-button-foreground);
        font-weight: 600;
        cursor: pointer;
      }

      button:hover {
        background: var(--vscode-button-hoverBackground);
      }

      button.secondary {
        background: var(--vscode-button-secondaryBackground);
        color: var(--vscode-button-secondaryForeground);
      }

      button.secondary:hover {
        background: var(--vscode-button-secondaryHoverBackground);
      }

      .tab-shell {
        display: grid;
        gap: 10px;
      }

      .tab-strip {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(96px, 1fr));
        gap: 6px;
      }

      .tab-button {
        padding: 8px 10px;
        border-radius: 9px;
        border: 1px solid var(--vscode-input-border, transparent);
        background: transparent;
        color: var(--vscode-descriptionForeground);
        font-size: 12px;
        font-weight: 600;
      }

      .tab-button[aria-selected="true"] {
        background: var(--vscode-button-secondaryBackground);
        color: var(--vscode-button-secondaryForeground);
        border-color: var(--vscode-focusBorder);
      }

      .tab-panel {
        display: grid;
        gap: 12px;
        padding: 12px;
        border-radius: 10px;
        border: 1px solid var(--vscode-panel-border, var(--vscode-widget-border, transparent));
        background: var(--vscode-editor-background);
      }

      [hidden] {
        display: none !important;
      }

      .tab-panel[hidden] {
        display: none;
      }

      .tab-head {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        gap: 12px;
      }

      .tab-head h2 {
        margin: 0;
        font-size: 13px;
      }

      .meta-pills {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
        gap: 8px;
      }

      .section-grid,
      .database-layout {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 10px;
      }

      .section-block,
      .status-card {
        display: grid;
        gap: 8px;
        padding: 12px;
        border-radius: 10px;
        border: 1px solid var(--vscode-widget-border, transparent);
        background: var(--vscode-textCodeBlock-background);
      }

      .section-title,
      .status-card-title {
        font-size: 12px;
        font-weight: 700;
        color: var(--vscode-editor-foreground);
      }

      .section-note,
      .status-card-copy {
        color: var(--vscode-descriptionForeground);
        font-size: 11px;
      }

      .status-card-list {
        display: grid;
        gap: 4px;
        margin: 0;
        padding-left: 18px;
        color: var(--vscode-descriptionForeground);
        font-size: 11px;
      }

      .status-badge {
        display: inline-flex;
        align-items: center;
        padding: 4px 8px;
        border-radius: 999px;
        background: var(--vscode-badge-background);
        color: var(--vscode-badge-foreground);
        font-size: 11px;
        font-weight: 700;
      }

      .meta-pill,
      .stat {
        padding: 10px;
        border-radius: 8px;
        background: var(--vscode-textCodeBlock-background);
      }

      .meta-pill-label,
      .stat-label {
        color: var(--vscode-descriptionForeground);
        font-size: 10px;
        text-transform: uppercase;
        letter-spacing: 0.08em;
      }

      .meta-pill-value {
        display: block;
        margin-top: 4px;
        font-size: 13px;
        font-weight: 700;
      }

      .stat-value {
        display: block;
        margin-top: 4px;
        font-size: 20px;
        font-weight: 700;
        color: var(--vscode-editor-foreground);
      }

      .summary-grid {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 8px;
      }

      .summary-meta {
        display: grid;
        gap: 4px;
        color: var(--vscode-descriptionForeground);
        font-size: 12px;
      }

      .file-list {
        display: grid;
        gap: 8px;
        list-style: none;
        margin: 0;
        padding: 0;
      }

      .file-item {
        display: grid;
        grid-template-columns: auto 1fr;
        gap: 10px;
        align-items: start;
        padding: 10px 12px;
        border-radius: 8px;
        background: var(--vscode-textCodeBlock-background);
      }

      .file-status {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        min-width: 74px;
        padding: 2px 8px;
        border-radius: 999px;
        background: var(--vscode-badge-background);
        color: var(--vscode-badge-foreground);
        font-size: 11px;
        font-weight: 700;
        text-transform: uppercase;
      }

      .field-error {
        color: var(--vscode-errorForeground, #f48771);
        font-size: 12px;
      }

      [aria-invalid="true"] {
        outline: 1px solid var(--vscode-inputValidation-errorBorder, #be1100);
      }

      .warning-card {
        display: grid;
        gap: 6px;
        padding: 12px;
        border-radius: 8px;
        border: 1px solid var(--vscode-inputValidation-warningBorder, #b89500);
        background: var(--vscode-inputValidation-warningBackground, #352a05);
      }

      .warning-card ul {
        margin: 0;
        padding-left: 18px;
      }

      .file-status.status-would-create,
      .file-status.status-would-update {
        background: #6a4c93;
        color: #ffffff;
      }

      .error-card {
        display: grid;
        gap: 8px;
        padding: 12px;
        border-radius: 8px;
        border: 1px solid var(--vscode-inputValidation-errorBorder, #be1100);
        background: var(--vscode-inputValidation-errorBackground, #5a1d1d);
      }

      .error-card pre {
        margin: 0;
        white-space: pre-wrap;
        word-break: break-word;
        font-family: var(--vscode-editor-font-family);
      }

      .file-link {
        all: unset;
        cursor: pointer;
        word-break: break-all;
        color: var(--vscode-textLink-foreground, #3794ff);
      }

      .file-link:hover,
      .file-link:focus-visible {
        text-decoration: underline;
      }

      .file-status.status-created {
        background: #2e7d32;
        color: #ffffff;
      }

      .file-status.status-updated {
        background: #1565c0;
        color: #ffffff;
      }

      .file-status.status-conflict {
        background: #c62828;
        color: #ffffff;
      }

      .empty-state {
        color: var(--vscode-descriptionForeground);
        font-size: 12px;
      }

      .tab-actions {
        display: flex;
        justify-content: flex-end;
        gap: 8px;
        flex-wrap: wrap;
      }

      .toggle-row {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 12px;
        padding: 10px;
        border-radius: 8px;
        background: var(--vscode-textCodeBlock-background);
      }

      .toggle-copy {
        display: grid;
        gap: 2px;
      }

      .toggle-title {
        font-size: 12px;
        font-weight: 700;
      }

      .toggle-note,
      .token-note {
        color: var(--vscode-descriptionForeground);
        font-size: 11px;
      }

      .toggle-input {
        width: auto;
        accent-color: var(--vscode-button-background);
      }

      .info-card {
        display: grid;
        gap: 8px;
        padding: 10px;
        border-radius: 8px;
        background: var(--vscode-textCodeBlock-background);
      }

      .info-card-title {
        font-size: 12px;
        font-weight: 700;
      }

      .info-list {
        display: grid;
        gap: 6px;
        margin: 0;
        padding: 0;
        list-style: none;
        color: var(--vscode-descriptionForeground);
        font-size: 11px;
      }

      .ghost-button {
        width: auto;
        padding: 7px 10px;
        background: transparent;
        color: var(--vscode-descriptionForeground);
        border: 1px solid var(--vscode-input-border, transparent);
      }

      .ghost-button:hover {
        color: var(--vscode-foreground);
        background: var(--vscode-list-hoverBackground);
      }

      .designer-card,
      .table-item {
        display: grid;
        gap: 8px;
        padding: 10px;
        border-radius: 8px;
        background: var(--vscode-textCodeBlock-background);
      }

      .designer-grid {
        display: grid;
        gap: 8px;
      }

      .designer-toolbar {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
      }

      .designer-row {
        display: grid;
        grid-template-columns: minmax(0, 1.1fr) minmax(0, 1fr) auto auto auto;
        gap: 8px;
        align-items: center;
        padding: 8px;
        border-radius: 8px;
        background: var(--vscode-editorWidget-background, var(--vscode-sideBar-background));
      }

      .designer-checkbox {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        color: var(--vscode-descriptionForeground);
        font-size: 11px;
      }

      .designer-checkbox input {
        width: auto;
      }

      .table-item-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
      }

      .table-item-columns {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
      }

      .table-chip {
        display: inline-flex;
        align-items: center;
        padding: 3px 8px;
        border-radius: 999px;
        background: var(--vscode-editor-background);
        color: var(--vscode-descriptionForeground);
        font-size: 11px;
      }

      pre {
        margin: 0;
        padding: 0;
        overflow-x: auto;
        white-space: pre-wrap;
        word-break: break-word;
        background: transparent;
        color: var(--vscode-editor-foreground);
      }

      .subtle {
        color: var(--vscode-descriptionForeground);
      }

      @media (max-width: 720px) {
        .hero {
          grid-template-columns: 1fr;
        }

        .primary-grid,
        .primary-actions,
        .tab-strip,
        .meta-pills,
        .section-grid,
        .database-layout,
        .summary-grid,
        .designer-row {
          grid-template-columns: 1fr;
        }
      }
    </style>
  </head>
  <body>
    <div class="shell">
      <section class="hero">
        <div class="hero-icon">
          <img src="${iconUri}" alt="" />
        </div>
        <div class="hero-copy">
          <div class="hero-kicker">VS Code Akışı</div>
          <h1>${escapeHtml(mode.title)}</h1>
          <p>${escapeHtml(mode.subtitle)}</p>
          <div class="mode-badge">${escapeHtml(mode.badgeLabel)}</div>
          ${workflowStepsHtml}
        </div>
      </section>

      <section class="panel">
        <div class="panel-header">
          <div class="panel-title-wrap">
            <div class="panel-note">${standardProfiles} standart profil | ${frameworkProfiles} hazır paket</div>
            <h2 class="panel-title">Proje Kurulumu</h2>
          </div>
        </div>
        <div class="panel-body">
          <div class="primary-grid">
            ${primaryFieldsHtml}
          </div>

          <div class="primary-actions">
            <button id="run">${escapeHtml(mode.actionLabel)}</button>
            ${this.supportsPreview() ? '<button id="preview" type="button" class="ghost-button">Önizle</button>' : ""}
            <span class="primary-hint">${escapeHtml(this.getPrimaryHint(mode.id))}</span>
          </div>

          <div class="tab-shell">
            <div class="tab-strip" role="tablist" aria-label="Üretici bölümleri">
              <button type="button" class="tab-button" data-tab-button="config" aria-selected="true">${escapeHtml(configurationTabLabel)}</button>
              ${isGenerationMode(mode.id) ? `<button type="button" class="tab-button" data-tab-button="database" aria-selected="false">SQL Şeması</button>
              <button type="button" class="tab-button" data-tab-button="runtime" aria-selected="false">Çalışma Zamanı</button>
              <button type="button" class="tab-button" data-tab-button="llm" aria-selected="false">LLM</button>` : ""}
              <button type="button" class="tab-button" data-tab-button="results" aria-selected="false">Özet</button>
              <button type="button" class="tab-button" data-tab-button="output" aria-selected="false">Loglar</button>
            </div>

            <section id="tab-config" class="tab-panel" data-tab-panel="config">
              <div class="tab-head">
                <h2>${escapeHtml(configurationTabLabel)}</h2>
                <span class="subtle">Proje tipi, mevcut proje ve çakışma kuralları</span>
              </div>
              ${configurationTabHtml}
            </section>

            <section id="tab-database" class="tab-panel" data-tab-panel="database" hidden>
              <div class="tab-head">
                <h2>SQL Şeması</h2>
                <span class="subtle">Seçilen SQL dosyasını düzenleyin</span>
              </div>
              ${databaseTabHtml}
            </section>

            <section id="tab-runtime" class="tab-panel" data-tab-panel="runtime" hidden>
              <div class="tab-head">
                <h2>Çalışma Zamanı</h2>
                <span class="subtle">Bağlantı dizesi ve isteğe bağlı ekler</span>
              </div>
              ${runtimeTabHtml}
            </section>

            <section id="tab-llm" class="tab-panel" data-tab-panel="llm" hidden>
              <div class="tab-head">
                <h2>LLM Ayarları</h2>
                <span class="subtle">İsteğe bağlı yapay zeka düzeltme adımı</span>
              </div>
              ${llmTabHtml}
            </section>

            <section id="tab-results" class="tab-panel" data-tab-panel="results" hidden>
              <div class="tab-head">
                <h2>Üretim Özeti</h2>
                <span id="statusPill" class="status-badge">Boşta</span>
              </div>
              <div class="summary-grid">
                <div class="stat">
                  <span class="stat-label">Dosya</span>
                  <span id="metricFiles" class="stat-value">0</span>
                </div>
                <div class="stat">
                  <span class="stat-label">Oluşturuldu</span>
                  <span id="metricCreated" class="stat-value">0</span>
                </div>
                <div class="stat">
                  <span class="stat-label">Güncellendi</span>
                  <span id="metricUpdated" class="stat-value">0</span>
                </div>
                <div class="stat">
                  <span class="stat-label">Aynı Kaldı</span>
                  <span id="metricUnchanged" class="stat-value">0</span>
                </div>
                <div class="stat">
                  <span class="stat-label">Çakışma</span>
                  <span id="metricConflicts" class="stat-value">0</span>
                </div>
              </div>
              <div id="errorCard" class="error-card" role="alert" hidden>
                <strong>Komut başarısız oldu</strong>
                <pre id="errorText"></pre>
                <div class="tab-actions">
                  <button id="retryRun" type="button">Tekrar Dene</button>
                </div>
              </div>
              <div id="warningCard" class="warning-card" hidden>
                <strong id="warningTitle">Uyarılar</strong>
                <ul id="warningList"></ul>
              </div>
              <div id="summaryMeta" class="summary-meta">
                <span>Henüz komut çalıştırılmadı.</span>
              </div>
              <ul id="fileList" class="file-list"></ul>
              <div id="emptyState" class="empty-state">Üretilen dosyaları ve manifest detaylarını görmek için bir komut çalıştırın.</div>
            </section>

            <section id="tab-output" class="tab-panel" data-tab-panel="output" hidden>
              <div class="tab-head">
                <h2>CLI Logları</h2>
                <span class="subtle">stdout ve stderr</span>
              </div>
              <pre id="outputBox">Henüz CLI çıktısı yok.</pre>
            </section>
          </div>
        </div>
      </section>
    </div>

    ${this.getRecentDatalistsHtml()}

    <script nonce="${nonce}">
      const vscode = acquireVsCodeApi();
      const frameworkFeatureDefaults = ${frameworkFeatureDefaultsJson};
      const frameworkBehavior = ${frameworkBehaviorJson};
      const createDraftColumn = (name = "", sqlType = "NVARCHAR(100)", isNullable = false, isPrimaryKey = false) => ({
        name,
        sqlType,
        isNullable,
        isPrimaryKey
      });
      let viewState = vscode.getState() || { form: {}, manifest: null, output: "", activeTab: "config" };
      let schemaDesignerState = viewState.schemaDesigner || {
        loadedSchemaPath: "",
        resolvedPath: "",
        tables: [],
        status: "Tabloları yönetmek için bir şema dosyası seçip yükleyin.",
        draftTableName: "",
        draftColumns: [createDraftColumn("Id", "INT", false, true)]
      };
      const outputBox = document.getElementById("outputBox");
      const fileList = document.getElementById("fileList");
      const emptyState = document.getElementById("emptyState");
      const summaryMeta = document.getElementById("summaryMeta");
      const statusPill = document.getElementById("statusPill");
      const llmTokenStatus = document.getElementById("llmTokenStatus");
      const llmRequiredNote = document.getElementById("llmRequiredNote");
      const frameworkField = document.getElementById("framework");
      const defaultFrameworkProjectSection = document.getElementById("defaultFrameworkProjectSection");
      const defaultFrameworkFeatureNote = document.getElementById("defaultFrameworkFeatureNote");
      const frameworkFeatureChecklist = document.getElementById("frameworkFeatureChecklist");
      const projectField = document.getElementById("project");
      const profileSection = document.getElementById("profileSection");
      const profileField = document.getElementById("profile");
      const profileLabel = document.getElementById("profileLabel");
      const profileCaption = document.getElementById("profileCaption");
      const frameworkSummaryTitle = document.getElementById("frameworkSummaryTitle");
      const frameworkSummaryCopy = document.getElementById("frameworkSummaryCopy");
      const frameworkSummaryList = document.getElementById("frameworkSummaryList");
      const outputField = document.getElementById("output");
      const outputCaption = document.getElementById("outputCaption");
      const schemaDesignerStatus = document.getElementById("schemaDesignerStatus");
      const schemaDesignerResolvedPath = document.getElementById("schemaDesignerResolvedPath");
      const schemaTablesList = document.getElementById("schemaTablesList");
      const schemaTablesEmpty = document.getElementById("schemaTablesEmpty");
      const loadSchemaDesignerButton = document.getElementById("loadSchemaDesigner");
      const draftTableNameField = document.getElementById("draftTableName");
      const draftColumnsContainer = document.getElementById("draftColumns");
      const addDraftColumnButton = document.getElementById("addDraftColumn");
      const appendSchemaTableButton = document.getElementById("appendSchemaTable");
      const pickProjectFolderButton = document.getElementById("pickProjectFolder");
      const pickProjectFileButton = document.getElementById("pickProjectFile");
      const pickSchemaFileButton = document.getElementById("pickSchemaFile");
      const featureWindowsAuthenticationField = document.getElementById("featureWindowsAuthentication");
      const featureUnitTestsField = document.getElementById("featureUnitTests");
      const featurePostmanCollectionField = document.getElementById("featurePostmanCollection");
      const llmEnabledField = document.getElementById("llmEnabled");
      const llmUrlField = document.getElementById("llmUrl");
      const llmModelField = document.getElementById("llmModel");
      const llmConcurrencyModeField = document.getElementById("llmConcurrencyMode");
      const llmMaxConcurrencyField = document.getElementById("llmMaxConcurrency");
      const llmTokenField = document.getElementById("llmToken");
      const tabButtons = Array.from(document.querySelectorAll("[data-tab-button]"));
      const tabPanels = Array.from(document.querySelectorAll("[data-tab-panel]"));
      const featureFieldIds = ["featureWindowsAuthentication", "featureUnitTests", "featurePostmanCollection"];
      const checkboxFieldIds = ["llmEnabled", ...featureFieldIds];
      let hasSavedLlmToken = ${this.llmSettings.hasToken ? "true" : "false"};
      let generatedProjectOutput = "GeneratedApi";

      const getValue = (id) => {
        const element = document.getElementById(id);
        if (!element) {
          return "";
        }

        if (element.type === "checkbox") {
          return element.checked;
        }

        return element.value;
      };

      const setMetric = (id, value) => {
        const element = document.getElementById(id);
        if (element) {
          element.textContent = String(value);
        }
      };

      const persistState = (patch) => {
        viewState = {
          ...viewState,
          ...patch
        };
        vscode.setState(viewState);
      };

      const setActiveTab = (tabId, persist = true) => {
        let matched = false;

        for (const button of tabButtons) {
          const isActive = button.dataset.tabButton === tabId;
          button.setAttribute("aria-selected", isActive ? "true" : "false");
          if (isActive) {
            matched = true;
          }
        }

        if (!matched && tabId !== "config") {
          // The saved tab is not offered in this mode (for example the LLM tab outside generation).
          setActiveTab("config", persist);
          return;
        }

        for (const panel of tabPanels) {
          panel.hidden = panel.dataset.tabPanel !== tabId;
        }

        if (persist && matched) {
          persistState({ activeTab: tabId });
        }
      };

      const formFieldIds = ["schema", "output", "project", "entity", "recipe", "field", "profile", "framework", "connectionString", "overwriteMode", "featureWindowsAuthentication", "featureUnitTests", "featurePostmanCollection", "llmEnabled", "llmUrl", "llmModel", "llmConcurrencyMode", "llmMaxConcurrency", "llmToken"];

      const snapshotForm = () => {
        const nextState = {};
        for (const fieldId of formFieldIds) {
          nextState[fieldId] = getValue(fieldId);
        }
        return nextState;
      };

      const deriveProjectName = (value) => {
        const raw = String(value || "").trim();
        if (!raw) {
          return "";
        }

        const normalized = raw.replaceAll("\\\\", "/").replace(/\\/+$/, "");
        const segments = normalized.split("/").filter(Boolean);
        const lastSegment = segments.length > 0 ? segments[segments.length - 1] : normalized;
        const withoutExtension = lastSegment.replace(/\\.(csproj|sln)$/i, "");
        return withoutExtension || lastSegment;
      };

      const restoreForm = () => {
        const formState = viewState.form || {};
        for (const fieldId of formFieldIds) {
          const element = document.getElementById(fieldId);
          if (!element) {
            continue;
          }

          if (checkboxFieldIds.includes(fieldId) && typeof formState[fieldId] === "boolean") {
            element.checked = formState[fieldId];
            continue;
          }

          if (typeof formState[fieldId] === "string" && formState[fieldId].length > 0) {
            element.value = formState[fieldId];
          }
        }

        if (llmConcurrencyModeField && typeof formState.llmConcurrencyMode !== "string") {
          llmConcurrencyModeField.value = llmMaxConcurrencyField && String(llmMaxConcurrencyField.value || "").trim().length > 0
            ? "manual"
            : "auto";
        }
      };

      const setTokenStatus = (hasToken) => {
        if (!llmTokenStatus) {
          return;
        }

        llmTokenStatus.textContent = hasToken
          ? "Kayıtlı token var. Korumak için boş bırakın veya yenisini girin."
          : "Henüz kayıtlı token yok. LLM isteklerini etkinleştirmek için bir token girin.";
      };

      const parseOptionalPositiveInteger = (value) => {
        const raw = String(value || "").trim();
        if (raw.length === 0) {
          return undefined;
        }

        const parsed = Number.parseInt(raw, 10);
        if (!Number.isInteger(parsed) || parsed <= 0) {
          return null;
        }

        return parsed;
      };

      const getLlmConcurrencyMode = () => {
        const raw = String(getValue("llmConcurrencyMode") || "").trim().toLowerCase();
        return raw === "manual" ? "manual" : "auto";
      };

      const escapeText = (value) => String(value || "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;");

      const persistSchemaDesignerState = () => {
        persistState({ schemaDesigner: schemaDesignerState });
      };

      const ensureSchemaDesignerDraft = () => {
        if (!Array.isArray(schemaDesignerState.draftColumns) || schemaDesignerState.draftColumns.length === 0) {
          schemaDesignerState.draftColumns = [createDraftColumn("Id", "INT", false, true)];
        }
      };

      const renderSchemaTables = () => {
        if (!schemaTablesList || !schemaTablesEmpty) {
          return;
        }

        schemaTablesList.innerHTML = schemaDesignerState.tables
          .map((table) => (
            '<div class="table-item">' +
              '<div class="table-item-head">' +
                '<strong>' + escapeText(table.name) + '</strong>' +
                '<button type="button" class="ghost-button" data-delete-table="' + escapeText(table.name) + '">Sil</button>' +
              '</div>' +
              '<div class="table-item-columns">' +
                table.columns.map((column) => (
                  '<span class="table-chip">' +
                    escapeText(column.name) + ' ' + escapeText(column.sqlType) + (column.isPrimaryKey ? ' PK' : '') + (column.isNullable ? ' NULL' : ' NOT NULL') +
                  '</span>'
                )).join('') +
              '</div>' +
            '</div>'
          ))
          .join("");

        schemaTablesEmpty.hidden = schemaDesignerState.tables.length > 0;
      };

      const renderDraftColumns = () => {
        if (!draftColumnsContainer) {
          return;
        }

        ensureSchemaDesignerDraft();
        draftColumnsContainer.innerHTML = schemaDesignerState.draftColumns
          .map((column, index) => (
            '<div class="designer-row" data-column-index="' + index + '">' +
              '<input data-column-field="name" type="text" placeholder="Sütun" value="' + escapeText(column.name) + '" />' +
              '<input data-column-field="sqlType" type="text" placeholder="NVARCHAR(100)" value="' + escapeText(column.sqlType) + '" />' +
              '<label class="designer-checkbox"><input data-column-field="isNullable" type="checkbox" ' + (column.isNullable ? 'checked' : '') + ' />Boş Geçilebilir</label>' +
              '<label class="designer-checkbox"><input data-column-field="isPrimaryKey" type="checkbox" ' + (column.isPrimaryKey ? 'checked' : '') + ' />PK</label>' +
              '<button type="button" class="ghost-button" data-column-action="remove">Sil</button>' +
            '</div>'
          ))
          .join("");
      };

      const renderSchemaDesigner = () => {
        ensureSchemaDesignerDraft();

        if (schemaDesignerStatus) {
          schemaDesignerStatus.textContent = schemaDesignerState.status || "Tabloları yönetmek için bir şema dosyası seçip yükleyin.";
        }

        if (schemaDesignerResolvedPath) {
          schemaDesignerResolvedPath.textContent = schemaDesignerState.resolvedPath
            ? "Çözümlenen SQL dosyası: " + schemaDesignerState.resolvedPath
            : "";
        }

        if (draftTableNameField) {
          draftTableNameField.value = schemaDesignerState.draftTableName || "";
        }

        renderSchemaTables();
        renderDraftColumns();
        persistSchemaDesignerState();
      };

      const readSchemaPathOrBlock = () => {
        const schemaValue = String(getValue("schema") || "").trim();
        if (schemaValue.length > 0) {
          return schemaValue;
        }

        schemaDesignerState.status = "Önce bir şema dosyası seçin, ardından Veritabanı sekmesinden SQL dosyasını yükleyin.";
        renderSchemaDesigner();
        setActiveTab("database");
        return "";
      };

      const requestSchemaLoad = () => {
        const schemaValue = readSchemaPathOrBlock();
        if (schemaValue.length === 0) {
          return;
        }

        schemaDesignerState.status = "Şema yükleniyor...";
        renderSchemaDesigner();
        vscode.postMessage({
          type: "load-schema-designer",
          payload: {
            schema: schemaValue
          }
        });
      };

      const requestAppendSchemaTable = () => {
        const schemaValue = readSchemaPathOrBlock();
        if (schemaValue.length === 0) {
          return;
        }

        ensureSchemaDesignerDraft();
        schemaDesignerState.draftTableName = draftTableNameField ? String(draftTableNameField.value || "") : String(schemaDesignerState.draftTableName || "");
        const table = {
          name: String(schemaDesignerState.draftTableName || "").trim(),
          columns: schemaDesignerState.draftColumns
        };

        if (table.name.length === 0) {
          schemaDesignerState.status = "CREATE TABLE betiğini eklemeden önce bir tablo adı girin.";
          renderSchemaDesigner();
          setActiveTab("database");
          return;
        }

        schemaDesignerState.status = "'" + table.name + "' tablosu seçilen SQL dosyasına ekleniyor...";
        renderSchemaDesigner();
        vscode.postMessage({
          type: "append-schema-table",
          payload: {
            schema: schemaValue,
            table
          }
        });
      };

      const requestDeleteSchemaTable = (tableName) => {
        const schemaValue = readSchemaPathOrBlock();
        if (schemaValue.length === 0) {
          return;
        }

        if (!window.confirm("Seçilen SQL dosyasından CREATE TABLE " + tableName + " kaydını silmek istiyor musunuz?")) {
          return;
        }

        schemaDesignerState.status = "'" + tableName + "' tablosu SQL dosyasından kaldırılıyor...";
        renderSchemaDesigner();
        vscode.postMessage({
          type: "delete-schema-table",
          payload: {
            schema: schemaValue,
            tableName
          }
        });
      };
      const getSelectedFrameworkBehavior = () => {
        const frameworkValue = String(getValue("framework") || "").trim();
        return frameworkBehavior[frameworkValue] || {};
      };

      const getFrameworkDefaults = () => {
        const frameworkValue = String(getValue("framework") || "").trim();
        const defaults = frameworkFeatureDefaults[frameworkValue] || {};
        return {
          windowsAuthentication: typeof defaults.windowsAuthentication === "boolean" ? defaults.windowsAuthentication : false,
          unitTests: typeof defaults.unitTests === "boolean" ? defaults.unitTests : true,
          postmanCollection: typeof defaults.postmanCollection === "boolean" ? defaults.postmanCollection : false
        };
      };

      const applyFrameworkFeatureDefaults = () => {
        const defaults = getFrameworkDefaults();

        if (featureWindowsAuthenticationField) {
          featureWindowsAuthenticationField.checked = defaults.windowsAuthentication;
        }

        if (featureUnitTestsField) {
          featureUnitTestsField.checked = defaults.unitTests;
        }

        if (featurePostmanCollectionField) {
          featurePostmanCollectionField.checked = defaults.postmanCollection;
        }
      };

      const syncLlmSection = () => {
        const usesLlm = Boolean(llmEnabledField && llmEnabledField.checked);
        const usesManualConcurrency = getLlmConcurrencyMode() === "manual";

        if (llmUrlField) {
          llmUrlField.required = usesLlm;
        }

        if (llmModelField) {
          llmModelField.required = usesLlm;
        }

        if (llmMaxConcurrencyField) {
          llmMaxConcurrencyField.required = usesManualConcurrency;
          llmMaxConcurrencyField.disabled = !usesManualConcurrency;
        }

        if (llmTokenField) {
          llmTokenField.required = usesLlm && !hasSavedLlmToken;
        }

        if (llmRequiredNote) {
          llmRequiredNote.textContent = usesLlm
            ? "LLM desteği etkin. URL, model ve token zorunludur."
            : "LLM desteği kapalı. URL, model ve token isteğe bağlıdır.";
        }
      };

      const syncFrameworkSummary = () => {
        const selectedFramework = frameworkField ? String(frameworkField.value || "") : "";
        const usesDefaultFramework = selectedFramework.trim().length === 0;
        const behavior = selectedFramework ? (frameworkBehavior[selectedFramework] || {}) : {};
        const projectName = deriveProjectName(projectField ? projectField.value : "");

        let title = "Default Framework";
        let copy = projectName
          ? "'" + projectName + "' projesini referans alıp yeni bir proje üretin."
          : "Seçilen projeyi referans alıp yeni bir proje üretin.";
        let items = [
          "Bu modda Referans Proje zorunludur.",
          "Seçilen proje kuralları ve mevcut tablolar örnek alınır.",
          "Yeni projenin adı ve konumu Çıktı Klasörü alanından belirlenir."
        ];

        if (!usesDefaultFramework && behavior.id === "minimal-api-swagger") {
          title = "Minimal Project";
          copy = "Controller, repository, Swagger ve isteğe bağlı eklerle sıfırdan küçük bir API oluşturun.";
          items = [
            "Bu modda Referans Proje ve ek profil gerekmez.",
            "Bağlantı dizesi ve kontrol listesi seçenekleri için Runtime sekmesini kullanın.",
            "Seçili .sql dosyasına doğrudan tablo eklemek istediğinizde SQL Schema sekmesini kullanın."
          ];
        } else if (!usesDefaultFramework && behavior.id === "enterprise-controller-loghelper-swagger") {
          title = "Enterprise Project";
          copy = "Kurumsal preset ile sıfırdan daha kapsamlı ve opinionated bir iskelet oluşturun.";
          items = [
            "Bu modda Referans Proje ve ek profil gerekmez.",
            "Auth, test ve Postman çıktısı istediğinizde Runtime kontrol listesini kullanın.",
            "Kurumsal konvansiyonları doğrudan başlatmak istediğinizde bu modu seçin."
          ];
        }

        if (frameworkSummaryTitle) {
          frameworkSummaryTitle.textContent = title;
        }

        if (frameworkSummaryCopy) {
          frameworkSummaryCopy.textContent = copy;
        }

        if (frameworkSummaryList) {
          frameworkSummaryList.innerHTML = items
            .map((item) => "<li>" + escapeText(item) + "</li>")
            .join("");
        }
      };

      const syncFrameworkProjectSection = () => {
        if (!frameworkField || !defaultFrameworkProjectSection) {
          return;
        }

        const usesDefaultFramework = String(frameworkField.value || "").trim().length === 0;
        
        if (outputField) {
          outputField.readOnly = false;
          outputField.disabled = false;
          outputField.placeholder = "GeneratedApi";
        }

        if (projectField) {
          projectField.disabled = !usesDefaultFramework;
        }


        if (pickProjectFolderButton) {
          pickProjectFolderButton.disabled = !usesDefaultFramework;
        }

        if (pickProjectFileButton) {
          pickProjectFileButton.disabled = !usesDefaultFramework;
        }

        if (defaultFrameworkProjectSection) {
          defaultFrameworkProjectSection.hidden = !usesDefaultFramework;
        }

        if (outputCaption) {
          outputCaption.textContent = usesDefaultFramework
            ? "Yeni proje bu klasörde oluşturulur. Referans proje sadece örnek alınır."
            : "Hazır paket yeni bir proje oluşturduğunda kullanılır.";
        }

        syncFrameworkSummary();
      };

      const syncFeatureChecklist = (applyDefaults = false) => {
        const usesDefaultFramework = !frameworkField || String(frameworkField.value || "").trim().length === 0;
        const featureFields = [featureWindowsAuthenticationField, featureUnitTestsField, featurePostmanCollectionField];

        if (defaultFrameworkFeatureNote) {
          defaultFrameworkFeatureNote.hidden = !usesDefaultFramework;
        }

        if (frameworkFeatureChecklist) {
          frameworkFeatureChecklist.hidden = usesDefaultFramework;
        }

        for (const field of featureFields) {
          if (field) {
            field.disabled = usesDefaultFramework;
          }
        }

        if (!usesDefaultFramework && applyDefaults) {
          applyFrameworkFeatureDefaults();
        }
      };

      const syncStandardProfileSelection = () => {
        const usesDefaultFramework = !frameworkField || String(frameworkField.value || "").trim().length === 0;

        if (profileSection) {
          profileSection.hidden = !usesDefaultFramework;
        }

        if (profileField) {
          if (!usesDefaultFramework) {
            profileField.value = "";
          }

          profileField.disabled = !usesDefaultFramework;
        }

        if (profileLabel) {
          profileLabel.textContent = usesDefaultFramework ? "Ek Profil" : "Standart Profil";
        }

        if (profileCaption) {
          profileCaption.textContent = usesDefaultFramework
            ? "Referans proje ana standardı sağlar. İsterseniz bunun üstüne kayıtlı bir profil uygulayın."
            : "Çalışma alanı standartları ve öğrenilen profiller.";
        }
      };

      const setIdleSummary = (message) => {
        setMetric("metricFiles", 0);
        setMetric("metricCreated", 0);
        setMetric("metricUpdated", 0);
        setMetric("metricUnchanged", 0);
        setMetric("metricConflicts", 0);
        statusPill.textContent = "Boşta";
        summaryMeta.innerHTML = "<span>" + message + "</span>";
        fileList.innerHTML = "";
        emptyState.hidden = false;
      };

      const errorCard = document.getElementById("errorCard");
      const errorText = document.getElementById("errorText");

      const showError = (message) => {
        if (errorCard && errorText) {
          errorText.textContent = message;
          errorCard.hidden = false;
        }
      };

      const hideError = () => {
        if (errorCard) {
          errorCard.hidden = true;
        }
      };

      const fileStatusLabels = {
        created: "Oluşturuldu",
        updated: "Güncellendi",
        unchanged: "Aynı",
        conflict: "Çakışma",
        "would-create": "Oluşturulacak",
        "would-update": "Güncellenecek"
      };

      const warningCard = document.getElementById("warningCard");
      const warningList = document.getElementById("warningList");
      const warningTitle = document.getElementById("warningTitle");

      const renderWarnings = (warnings) => {
        const items = Array.isArray(warnings) ? warnings.filter((warning) => String(warning || "").length > 0) : [];
        if (!warningCard || !warningList) {
          return;
        }

        warningList.innerHTML = items.map((warning) => "<li>" + escapeText(warning) + "</li>").join("");
        if (warningTitle) {
          warningTitle.textContent = "Uyarılar (" + items.length + ")";
        }
        warningCard.hidden = items.length === 0;
      };

      const renderManifest = (manifest) => {
        setMetric("metricFiles", manifest.Summary.TotalFiles);
        setMetric("metricCreated", manifest.Summary.Created);
        setMetric("metricUpdated", manifest.Summary.Updated);
        setMetric("metricUnchanged", manifest.Summary.Unchanged);
        setMetric("metricConflicts", manifest.Summary.Conflicts);
        const llmMeta = manifest.Llm && manifest.Llm.Enabled
          ? "<span><strong>LLM:</strong> " + (manifest.Llm.Applied
            ? manifest.Llm.Model + " / refined " + manifest.Llm.RefinedFiles + " of " + manifest.Llm.TargetFiles + " / concurrency " + (typeof manifest.Llm.EffectiveMaxConcurrency === "number" ? manifest.Llm.EffectiveMaxConcurrency : 0)
            : "etkin fakat uygulanmadı") + "</span>"
          : "";

        statusPill.textContent = manifest.DryRun ? "Önizleme" : String(manifest.OverwriteMode || "").toUpperCase();
        renderWarnings(manifest.Warnings);
        summaryMeta.innerHTML =
          "<span><strong>Çözüm:</strong> " + escapeText(manifest.SolutionName) + "</span>" +
          "<span><strong>Varlıklar:</strong> " + escapeText(manifest.EntityCount) + "</span>" +
          "<span><strong>Çıktı:</strong> " + escapeText(manifest.OutputPath) + "</span>" +
          llmMeta;

        const files = manifest.GeneratedFiles || [];
        const outputRoot = String(manifest.OutputPath || "").replace(/[\\\\/]+$/, "");
        fileList.innerHTML = files
          .map((file) => (
            '<li class="file-item">' +
              '<span class="file-status status-' + escapeText(file.Status) + '">' + escapeText(fileStatusLabels[file.Status] || file.Status) + '</span>' +
              (manifest.DryRun
                ? '<span class="mono">' + escapeText(file.RelativePath) + '</span>'
                : '<button type="button" class="file-link mono" data-open-file="' + escapeText(outputRoot + "/" + file.RelativePath) + '" title="Dosyayı aç">' + escapeText(file.RelativePath) + '</button>') +
            '</li>'
          ))
          .join("");

        emptyState.hidden = files.length > 0;
      };

      const hasStoredFeatureSelections = featureFieldIds.some((fieldId) => typeof (viewState.form || {})[fieldId] === "boolean");

      restoreForm();
      const savedGeneratedProjectOutput = typeof viewState.generatedProjectOutput === "string"
        ? viewState.generatedProjectOutput
        : "";
      const startsInDefaultFramework = !frameworkField || String(frameworkField.value || "").trim().length === 0;
      generatedProjectOutput = savedGeneratedProjectOutput || (startsInDefaultFramework ? "GeneratedApi" : (outputField ? String(outputField.value || "GeneratedApi") : "GeneratedApi"));
      setActiveTab(viewState.activeTab || "config", false);
      setTokenStatus(hasSavedLlmToken);
      syncFrameworkProjectSection();
      syncFeatureChecklist(!hasStoredFeatureSelections);
      syncStandardProfileSelection();
      syncLlmSection();

      for (const fieldId of formFieldIds) {
        const element = document.getElementById(fieldId);
        if (element) {
          element.addEventListener("input", () => {
            persistState({ form: snapshotForm(), generatedProjectOutput });
          });
          element.addEventListener("change", () => {
            persistState({ form: snapshotForm(), generatedProjectOutput });
          });
        }
      }

      for (const button of tabButtons) {
        button.addEventListener("click", () => {
          setActiveTab(button.dataset.tabButton);
        });
      }

      if (outputField) {
        outputField.addEventListener("input", () => {
          if (!outputField.disabled) {
            generatedProjectOutput = String(outputField.value || "GeneratedApi");
            persistState({ form: snapshotForm(), generatedProjectOutput });
          }
        });
      }

      if (projectField) {
        const syncProjectSelection = () => {
          syncFrameworkProjectSection();
          persistState({ form: snapshotForm(), generatedProjectOutput });
        };

        projectField.addEventListener("input", syncProjectSelection);
        projectField.addEventListener("change", syncProjectSelection);
      }


      if (loadSchemaDesignerButton) {
        loadSchemaDesignerButton.addEventListener("click", () => {
          requestSchemaLoad();
        });
      }

      if (addDraftColumnButton) {
        addDraftColumnButton.addEventListener("click", () => {
          schemaDesignerState.draftColumns.push(createDraftColumn("", "NVARCHAR(100)", true, false));
          renderSchemaDesigner();
        });
      }

      if (appendSchemaTableButton) {
        appendSchemaTableButton.addEventListener("click", () => {
          requestAppendSchemaTable();
        });
      }

      if (draftTableNameField) {
        draftTableNameField.addEventListener("input", () => {
          schemaDesignerState.draftTableName = String(draftTableNameField.value || "");
          persistSchemaDesignerState();
        });
      }

      if (draftColumnsContainer) {
        draftColumnsContainer.addEventListener("input", (event) => {
          const target = event.target;
          if (!target || !target.closest) {
            return;
          }

          const row = target.closest("[data-column-index]");
          if (!row) {
            return;
          }

          const index = Number(row.getAttribute("data-column-index"));
          const field = target.getAttribute("data-column-field");
          if (Number.isNaN(index) || !field || !schemaDesignerState.draftColumns[index]) {
            return;
          }

          if (target.type === "checkbox") {
            schemaDesignerState.draftColumns[index][field] = Boolean(target.checked);
          } else {
            schemaDesignerState.draftColumns[index][field] = String(target.value || "");
          }

          persistSchemaDesignerState();
        });

        draftColumnsContainer.addEventListener("click", (event) => {
          const target = event.target;
          if (!target || !target.closest) {
            return;
          }

          const button = target.closest("[data-column-action]");
          if (!button || button.getAttribute("data-column-action") !== "remove") {
            return;
          }

          const row = button.closest("[data-column-index]");
          const index = row ? Number(row.getAttribute("data-column-index")) : NaN;
          if (Number.isNaN(index)) {
            return;
          }

          schemaDesignerState.draftColumns.splice(index, 1);
          if (schemaDesignerState.draftColumns.length === 0) {
            schemaDesignerState.draftColumns.push(createDraftColumn("Id", "INT", false, true));
          }
          renderSchemaDesigner();
        });
      }

      if (schemaTablesList) {
        schemaTablesList.addEventListener("click", (event) => {
          const target = event.target;
          if (!target || !target.closest) {
            return;
          }

          const button = target.closest("[data-delete-table]");
          if (!button) {
            return;
          }

          const tableName = String(button.getAttribute("data-delete-table") || "").trim();
          if (tableName.length > 0) {
            requestDeleteSchemaTable(tableName);
          }
        });
      }

      const entityField = document.getElementById("entity");
      const recipeField = document.getElementById("recipe");
      const fieldField = document.getElementById("field");
      const entityCaption = document.getElementById("entityCaption");
      const fieldCaption = document.getElementById("fieldCaption");
      const loadEntitiesButton = document.getElementById("loadEntities");
      const previewButton = document.getElementById("preview");
      const currentMode = ${JSON.stringify(this.currentMode)};
      const usesGenerationForm = currentMode === "create" || currentMode === "generate";
      let entityCatalog = viewState.entityCatalog || { project: "", entities: [] };

      const fieldTypeMatches = (recipe, type) => {
        const normalized = String(type || "").replace(/\\?$/, "");
        if (recipe === "GetActiveList") {
          return normalized === "bool";
        }
        if (recipe === "Search") {
          return normalized === "string";
        }
        if (recipe === "GetByDateRange") {
          return normalized === "DateTime" || normalized === "DateOnly" || normalized === "DateTimeOffset";
        }
        if (recipe === "GetByCode") {
          return normalized !== "bool" && normalized !== "byte[]";
        }
        return false;
      };

      const selectedEntity = () => (entityCatalog.entities || []).find((entity) => entity.name === String(getValue("entity") || ""));

      const renderFieldOptions = (preferred) => {
        if (!fieldField || !recipeField) {
          return;
        }

        const recipe = String(recipeField.value || "");
        const entity = selectedEntity();
        const candidates = entity ? entity.properties.filter((property) => fieldTypeMatches(recipe, property.type)) : [];
        const wanted = preferred !== undefined ? preferred : String(fieldField.value || "");
        fieldField.innerHTML = recipe === "BulkInsert"
          ? '<option value="">Alan gerekmez</option>'
          : candidates.map((property) => '<option value="' + escapeText(property.name) + '">' + escapeText(property.name) + " (" + escapeText(property.type) + ")</option>").join("");
        if (candidates.some((property) => property.name === wanted)) {
          fieldField.value = wanted;
        } else if (recipe === "GetActiveList" && candidates.some((property) => property.name === "IsActive")) {
          fieldField.value = "IsActive";
        }
        fieldField.disabled = recipe === "BulkInsert";
        if (fieldCaption) {
          fieldCaption.textContent = recipe === "BulkInsert"
            ? "BulkInsert bir alan kullanmaz."
            : entity && candidates.length === 0
              ? "Bu varlıkta " + recipe + " reçetesine uygun alan yok."
              : "Reçeteye uygun alanlar listelenir.";
        }
      };

      const renderEntityOptions = (preferredEntity, preferredField) => {
        if (!entityField) {
          return;
        }

        const entities = entityCatalog.entities || [];
        entityField.innerHTML = entities.length === 0
          ? '<option value="">Önce varlıkları yükleyin</option>'
          : entities.map((entity) => '<option value="' + escapeText(entity.name) + '">' + escapeText(entity.name) + "</option>").join("");
        if (entities.some((entity) => entity.name === preferredEntity)) {
          entityField.value = preferredEntity;
        }
        renderFieldOptions(preferredField);
      };

      const requestEntities = () => {
        const project = String(getValue("project") || "").trim();
        if (entityCaption) {
          entityCaption.textContent = project.length === 0 ? "Önce çözüm klasörünü seçin." : "Varlıklar okunuyor...";
        }
        if (project.length > 0) {
          vscode.postMessage({ type: "load-entities", payload: { project } });
        }
      };

      const clearFieldErrors = () => {
        for (const error of Array.from(document.querySelectorAll("[data-error-for]"))) {
          error.remove();
        }
        for (const invalid of Array.from(document.querySelectorAll('[aria-invalid="true"]'))) {
          invalid.removeAttribute("aria-invalid");
        }
      };

      const showFieldError = (fieldId, message) => {
        const element = document.getElementById(fieldId);
        if (!element) {
          return;
        }
        element.setAttribute("aria-invalid", "true");
        const container = element.closest(".field") || element.parentElement;
        const error = document.createElement("span");
        error.className = "field-error";
        error.setAttribute("data-error-for", fieldId);
        error.textContent = message;
        container.appendChild(error);
      };

      // Required inputs per mode; returns field id -> message.
      const validateForm = () => {
        const errors = {};
        const isBlank = (id) => String(getValue(id) || "").trim().length === 0;
        if (usesGenerationForm) {
          if (isBlank("schema")) {
            errors.schema = "SQL dosyası zorunludur.";
          }
          if (isBlank("output")) {
            errors.output = "Çıktı klasörü zorunludur.";
          }
          if ((!frameworkField || String(frameworkField.value || "").trim().length === 0) && isBlank("project")) {
            errors.project = "Default Framework modunda bir Referans Proje seçmelisiniz.";
          }
        } else if (currentMode === "learn") {
          if (isBlank("project")) {
            errors.project = "İncelenecek proje yolu zorunludur.";
          }
          if (isBlank("output")) {
            errors.output = "Profil çıktı klasörü zorunludur.";
          }
        } else if (currentMode === "document") {
          if (isBlank("output")) {
            errors.output = "Çıktı klasörü zorunludur.";
          }
        } else if (currentMode === "endpoint") {
          const recipe = String(getValue("recipe") || "");
          if (isBlank("project")) {
            errors.project = "Çözüm klasörü zorunludur.";
          }
          if (isBlank("entity")) {
            errors.entity = "Bir varlık seçin (önce Varlıkları Yükle).";
          }
          if (recipe !== "BulkInsert" && isBlank("field")) {
            errors.field = "Bu reçete için uygun bir alan seçin.";
          }
        }
        return errors;
      };

      const blockRun = (message, tab) => {
        statusPill.textContent = "Engellendi";
        outputBox.textContent = message;
        if (tab) {
          setActiveTab(tab);
        }
        persistState({ form: snapshotForm(), output: outputBox.textContent });
      };

      const submit = (preview) => {
        const usesLlm = usesGenerationForm && Boolean(getValue("llmEnabled"));
        const llmUrlValue = String(getValue("llmUrl") || "").trim();
        const llmModelValue = String(getValue("llmModel") || "").trim();
        const llmConcurrencyMode = getLlmConcurrencyMode();
        const llmMaxConcurrencyValue = llmConcurrencyMode === "manual"
          ? String(getValue("llmMaxConcurrency") || "").trim()
          : "";
        const llmTokenValue = String(getValue("llmToken") || "").trim();
        const parsedLlmMaxConcurrency = parseOptionalPositiveInteger(llmMaxConcurrencyValue);

        clearFieldErrors();
        const errors = validateForm();
        const invalidFields = Object.keys(errors);
        if (invalidFields.length > 0) {
          for (const fieldId of invalidFields) {
            showFieldError(fieldId, errors[fieldId]);
          }
          blockRun(invalidFields.map((fieldId) => errors[fieldId]).join("\\n"), invalidFields.includes("project") && usesGenerationForm ? "config" : undefined);
          const first = document.getElementById(invalidFields[0]);
          if (first && first.focus) {
            first.focus();
          }
          return;
        }

        if (usesLlm && (llmUrlValue.length === 0 || llmModelValue.length === 0 || (llmTokenValue.length === 0 && !hasSavedLlmToken))) {
          blockRun("LLM desteği etkinse URL, model ve token zorunludur.", "llm");
          return;
        }

        if (usesLlm && llmConcurrencyMode === "manual" && parsedLlmMaxConcurrency === undefined) {
          blockRun("Manual LLM eşzamanlılık seçildiğinde 1 veya daha büyük bir tam sayı girmelisiniz.", "llm");
          return;
        }

        if (usesLlm && llmMaxConcurrencyValue.length > 0 && parsedLlmMaxConcurrency === null) {
          blockRun("LLM eşzamanlılık değeri boş bırakılmalı ya da 1 veya daha büyük bir tam sayı olmalıdır.", "llm");
          return;
        }

        statusPill.textContent = preview ? "Önizleniyor" : "Çalışıyor";
        outputBox.textContent = "CLI çalışıyor...";
        hideError();
        setActiveTab("output");
        persistState({
          form: snapshotForm(),
          output: "CLI çalışıyor..."
        });

        vscode.postMessage({
          type: "run",
          payload: {
            preview,
            schema: getValue("schema"),
            output: getValue("output"),
            project: getValue("project"),
            entity: getValue("entity"),
            recipe: getValue("recipe"),
            field: recipeField && recipeField.value === "BulkInsert" ? "" : getValue("field"),
            profile: getValue("profile"),
            framework: getValue("framework"),
            connectionString: getValue("connectionString"),
            overwriteMode: getValue("overwriteMode"),
            featureWindowsAuthentication: Boolean(getValue("featureWindowsAuthentication")),
            featureUnitTests: Boolean(getValue("featureUnitTests")),
            featurePostmanCollection: Boolean(getValue("featurePostmanCollection")),
            llmEnabled: usesLlm,
            llmUrl: String(getValue("llmUrl") || ""),
            llmModel: String(getValue("llmModel") || ""),
            llmMaxConcurrency: llmConcurrencyMode === "manual" && parsedLlmMaxConcurrency !== null
              ? parsedLlmMaxConcurrency
              : undefined,
            llmToken: String(getValue("llmToken") || "")
          }
        });
      };

      document.getElementById("run").addEventListener("click", () => submit(false));
      if (previewButton) {
        previewButton.addEventListener("click", () => submit(true));
      }

      if (loadEntitiesButton) {
        loadEntitiesButton.addEventListener("click", requestEntities);
      }

      if (entityField) {
        entityField.addEventListener("change", () => {
          renderFieldOptions();
          persistState({ form: snapshotForm() });
        });
      }

      if (recipeField) {
        recipeField.addEventListener("change", () => {
          renderFieldOptions();
          persistState({ form: snapshotForm() });
        });
      }

      renderEntityOptions(String((viewState.form || {}).entity || ""), String((viewState.form || {}).field || ""));

      const retryRunButton = document.getElementById("retryRun");
      if (retryRunButton) {
        retryRunButton.addEventListener("click", () => {
          hideError();
          document.getElementById("run").click();
        });
      }

      if (fileList) {
        fileList.addEventListener("click", (event) => {
          const target = event.target;
          const button = target && target.closest ? target.closest("[data-open-file]") : null;
          if (button) {
            vscode.postMessage({ type: "open-file", payload: { path: button.getAttribute("data-open-file") } });
          }
        });
      }

      const refreshProfilesButton = document.getElementById("refreshProfiles");
      if (refreshProfilesButton) {
        refreshProfilesButton.addEventListener("click", () => {
          vscode.postMessage({ type: "refresh-profiles" });
        });
      }

      if (pickProjectFolderButton) {
        pickProjectFolderButton.addEventListener("click", () => {
          vscode.postMessage({ type: "pick-project-folder", payload: { currentProject: String(getValue("project") || "") } });
        });
      }

      if (pickProjectFileButton) {
        pickProjectFileButton.addEventListener("click", () => {
          vscode.postMessage({ type: "pick-project-file", payload: { currentProject: String(getValue("project") || "") } });
        });
      }

      if (pickSchemaFileButton) {
        pickSchemaFileButton.addEventListener("click", () => {
          vscode.postMessage({ type: "pick-schema-file", payload: { currentSchema: String(getValue("schema") || "") } });
        });
      }

      if (frameworkField) {
        frameworkField.addEventListener("change", () => {
          syncFrameworkProjectSection();
          syncFeatureChecklist(true);
          syncStandardProfileSelection();
          persistState({ form: snapshotForm(), generatedProjectOutput });
        });
      }

      const saveLlmSettingsButton = document.getElementById("saveLlmSettings");
      if (saveLlmSettingsButton) {
        saveLlmSettingsButton.addEventListener("click", () => {
          const usesLlm = Boolean(getValue("llmEnabled"));
          const llmUrlValue = String(getValue("llmUrl") || "").trim();
          const llmModelValue = String(getValue("llmModel") || "").trim();
          const llmConcurrencyMode = getLlmConcurrencyMode();
          const llmMaxConcurrencyValue = llmConcurrencyMode === "manual"
            ? String(getValue("llmMaxConcurrency") || "").trim()
            : "";
          const llmTokenValue = String(getValue("llmToken") || "").trim();
          const parsedLlmMaxConcurrency = parseOptionalPositiveInteger(llmMaxConcurrencyValue);

          if (usesLlm && (llmUrlValue.length === 0 || llmModelValue.length === 0 || (llmTokenValue.length === 0 && !hasSavedLlmToken))) {
            statusPill.textContent = "Engellendi";
            outputBox.textContent = "LLM desteği etkinse LLM ayarlarını kaydetmek için URL, model ve token zorunludur.";
            setActiveTab("llm");
            persistState({
              form: snapshotForm(),
              output: outputBox.textContent
            });
            return;
          }

          if (llmConcurrencyMode === "manual" && parsedLlmMaxConcurrency === undefined) {
            statusPill.textContent = "Engellendi";
            outputBox.textContent = "Manual LLM eşzamanlılık seçildiğinde 1 veya daha büyük bir tam sayı girmelisiniz.";
            setActiveTab("llm");
            persistState({
              form: snapshotForm(),
              output: outputBox.textContent
            });
            return;
          }

          if (llmMaxConcurrencyValue.length > 0 && parsedLlmMaxConcurrency === null) {
            statusPill.textContent = "Engellendi";
            outputBox.textContent = "LLM eşzamanlılık değeri boş bırakılmalı ya da 1 veya daha büyük bir tam sayı olmalıdır.";
            setActiveTab("llm");
            persistState({
              form: snapshotForm(),
              output: outputBox.textContent
            });
            return;
          }

          vscode.postMessage({
            type: "save-llm-settings",
            payload: {
              llmEnabled: Boolean(getValue("llmEnabled")),
              llmUrl: String(getValue("llmUrl") || ""),
              llmModel: String(getValue("llmModel") || ""),
              llmMaxConcurrency: llmConcurrencyMode === "manual" && parsedLlmMaxConcurrency !== null
                ? parsedLlmMaxConcurrency
                : undefined,
              llmToken: String(getValue("llmToken") || "")
            }
          });
        });
      }

      window.addEventListener("message", (event) => {
        if (event.data.type === "project-picked") {
          const payload = event.data.payload || {};
          const projectField = document.getElementById("project");
          if (projectField && typeof payload.project === "string") {
            projectField.value = payload.project;
          }
          if (currentMode === "endpoint") {
            requestEntities();
          }
          syncFrameworkProjectSection();
          persistState({ form: snapshotForm(), generatedProjectOutput });
          return;
        }

        if (event.data.type === "schema-picked") {
          const payload = event.data.payload || {};
          const schemaField = document.getElementById("schema");
          if (schemaField && typeof payload.schema === "string") {
            schemaField.value = payload.schema;
          }
          persistState({ form: snapshotForm(), generatedProjectOutput });
          return;
        }

        if (event.data.type === "project-pick-error" || event.data.type === "schema-pick-error") {
          const payload = event.data.payload || {};
          statusPill.textContent = "Seçici Hatası";
          outputBox.textContent = String(payload.message || "Dosya seçici açılamadı.");
          setActiveTab("output");
          persistState({
            form: snapshotForm(),
            output: outputBox.textContent,
            generatedProjectOutput
          });
          return;
        }

        if (event.data.type === "llm-settings") {
          const payload = event.data.payload || {};
          hasSavedLlmToken = Boolean(payload.hasToken);
          if (llmUrlField && typeof payload.url === "string") {
            llmUrlField.value = payload.url;
          }
          if (llmModelField && typeof payload.model === "string") {
            llmModelField.value = payload.model;
          }
          if (llmConcurrencyModeField) {
            llmConcurrencyModeField.value = typeof payload.maxConcurrency === "number" && payload.maxConcurrency > 0
              ? "manual"
              : "auto";
          }
          if (llmMaxConcurrencyField) {
            llmMaxConcurrencyField.value = typeof payload.maxConcurrency === "number" && payload.maxConcurrency > 0
              ? String(payload.maxConcurrency)
              : "";
          }
          setTokenStatus(hasSavedLlmToken);
          syncLlmSection();
          persistState({ form: snapshotForm(), generatedProjectOutput });
          return;
        }

        if (event.data.type === "schema-designer-data") {
          const payload = event.data.payload || {};
          schemaDesignerState = {
            ...schemaDesignerState,
            loadedSchemaPath: String(payload.schemaPath || getValue("schema") || ""),
            resolvedPath: String(payload.resolvedPath || ""),
            tables: Array.isArray(payload.tables) ? payload.tables : [],
            status: String(payload.message || "Şema yüklendi."),
            draftTableName: "",
            draftColumns: [createDraftColumn("Id", "INT", false, true)]
          };
          renderSchemaDesigner();
          setActiveTab("database");
          return;
        }

        if (event.data.type === "schema-designer-error") {
          const payload = event.data.payload || {};
          schemaDesignerState = {
            ...schemaDesignerState,
            status: String(payload.message || "Şema tasarımcısı işlemi başarısız oldu.")
          };
          renderSchemaDesigner();
          setActiveTab("database");
          return;
        }

        if (event.data.type === "entities") {
          const payload = event.data.payload || {};
          entityCatalog = { project: String(payload.project || ""), entities: Array.isArray(payload.entities) ? payload.entities : [] };
          persistState({ entityCatalog });
          renderEntityOptions(String(getValue("entity") || ""), String(getValue("field") || ""));
          if (entityCaption) {
            entityCaption.textContent = payload.error
              ? String(payload.error)
              : entityCatalog.entities.length + " varlık yüklendi.";
          }
          return;
        }

        if (event.data.type === "recent") {
          const payload = event.data.payload || {};
          const fill = (id, values) => {
            const list = document.getElementById(id);
            if (list && Array.isArray(values)) {
              list.innerHTML = values.map((value) => '<option value="' + escapeText(value) + '"></option>').join("");
            }
          };
          fill("recentSchemas", payload.schemas);
          fill("recentOutputs", payload.outputs);
          fill("recentProjects", payload.projects);
          return;
        }

        if (event.data.type !== "result") {
          return;
        }

        const payload = event.data.payload;
        const exitCode = typeof payload.exitCode === "number" ? payload.exitCode : 1;
        hideError();
        const preferredOutput = exitCode !== 0
          ? payload.stderr || payload.stdout
          : payload.stdout || payload.stderr;
        outputBox.textContent = preferredOutput || "CLI çıktısı yok.";
        persistState({
          form: snapshotForm(),
          output: outputBox.textContent
        });

        if (exitCode !== 0) {
          setIdleSummary("Komut başarısız oldu. Ayrıntılar için Loglar sekmesini inceleyin.");
          statusPill.textContent = "Başarısız";
          renderWarnings([]);
          showError(preferredOutput || "CLI çıktısı yok.");
          emptyState.hidden = true;
          setActiveTab("results");
          persistState({
            form: snapshotForm(),
            manifest: null,
            output: outputBox.textContent
          });
          return;
        }

        if (payload.manifest) {
          renderManifest(payload.manifest);
          setActiveTab("results");
          persistState({
            form: snapshotForm(),
            manifest: payload.manifest,
            output: outputBox.textContent
          });
          return;
        }

        const events = Array.isArray(payload.events) ? payload.events : [];
        const endpointEvent = events.find((item) => item.event === "endpoint-added" || item.event === "endpoint-exists");
        if (endpointEvent) {
          // add-endpoint has no manifest; show the files it created or changed like a generation summary.
          const project = String(payload.project || "").replace(/[\\\\/]+$/, "");
          const preview = Boolean(endpointEvent.dryRun);
          const files = []
            .concat((endpointEvent.createdFiles || []).map((path) => ({ RelativePath: path, Category: "source", Status: preview ? "would-create" : "created" })))
            .concat((endpointEvent.updatedFiles || []).map((path) => ({ RelativePath: path, Category: "source", Status: preview ? "would-update" : "updated" })));
          const pseudoManifest = {
            SolutionName: endpointEvent.entity + " · " + endpointEvent.method,
            OutputPath: project,
            EntityCount: 1,
            DryRun: preview,
            OverwriteMode: endpointEvent.event === "endpoint-exists" ? "mevcut" : "eklendi",
            Warnings: events.filter((item) => item.level === "warning").map((item) => item.message),
            Summary: {
              TotalFiles: files.length,
              Created: (endpointEvent.createdFiles || []).length,
              Updated: (endpointEvent.updatedFiles || []).length,
              Unchanged: 0,
              Conflicts: 0
            },
            GeneratedFiles: files
          };
          renderManifest(pseudoManifest);
          summaryMeta.innerHTML += "<span><strong>Endpoint:</strong> " + escapeText(endpointEvent.httpMethod + " " + endpointEvent.route) + "</span>";
          if (endpointEvent.event === "endpoint-exists") {
            statusPill.textContent = "Zaten Var";
          }
          setActiveTab("results");
          persistState({ form: snapshotForm(), manifest: pseudoManifest, output: outputBox.textContent });
          return;
        }

        // learn / document / analyze: no file manifest, show the CLI messages as the summary.
        setIdleSummary(escapeText(preferredOutput || "Komut tamamlandı."));
        emptyState.hidden = true;
        renderWarnings(events.filter((item) => item.level === "warning").map((item) => item.message));
        statusPill.textContent = "Tamamlandı";
        setActiveTab("results");
        persistState({
          form: snapshotForm(),
          manifest: null,
          output: outputBox.textContent
        });
      });

      if (viewState.output) {
        outputBox.textContent = viewState.output;
      }

      if (viewState.manifest) {
        renderManifest(viewState.manifest);
      } else {
        setIdleSummary("Üretilen dosyaları ve manifest detaylarını görmek için bir komut çalıştırın.");
      }

      if (llmEnabledField) {
        llmEnabledField.addEventListener("change", () => {
          syncLlmSection();
          persistState({ form: snapshotForm(), generatedProjectOutput });
        });
      }

      if (llmConcurrencyModeField) {
        llmConcurrencyModeField.addEventListener("change", () => {
          syncLlmSection();
          persistState({ form: snapshotForm(), generatedProjectOutput });
        });
      }

      if (llmTokenField) {
        llmTokenField.addEventListener("input", () => {
          syncLlmSection();
        });
      }
    </script>
  </body>
</html>`;
  }

  private getPrimaryHint(mode: string): string {
    if (mode === "endpoint") {
      return "Önizle ile değişecek dosyaları görün; Endpoint Ekle dosyaları yazar ve testleri ekler.";
    }

    if (mode === "learn") {
      return "Proje incelenir ve profil seçilen klasöre kaydedilir; sonuç Özet sekmesinde görünür.";
    }

    if (mode === "document") {
      return "Dokümantasyon seçilen çözüm klasörüne yazılır; sonuç Özet sekmesinde görünür.";
    }

    return "Önce ana alanları doldurun, sonra kurulum, SQL şeması, çalışma zamanı ve loglar için aşağıdaki sekmeleri kullanın.";
  }

  private getRecentDatalistsHtml(): string {
    const recent = this.dependencies.recentValuesStore.get();
    const list = (id: string, values: string[]): string =>
      `<datalist id="${id}">${values.map((value) => `<option value="${escapeHtml(value)}"></option>`).join("")}</datalist>`;
    return list("recentSchemas", recent.schemas) + list("recentOutputs", recent.outputs) + list("recentProjects", recent.projects);
  }

  private getWorkflowStepsHtml(mode: string): string {
    if (mode === "learn") {
      return `
      <div class="workflow-strip">
        <div class="workflow-step">
          <span class="workflow-step-number">Adım 1</span>
          <span class="workflow-step-title">Proje Seç</span>
          <span class="workflow-step-note">İncelenecek kod tabanını seçin.</span>
        </div>
        <div class="workflow-step">
          <span class="workflow-step-number">Adım 2</span>
          <span class="workflow-step-title">Kuralları Oku</span>
          <span class="workflow-step-note">Klasörleri, şablonları ve isimlendirmeyi analiz edin.</span>
        </div>
        <div class="workflow-step">
          <span class="workflow-step-number">Adım 3</span>
          <span class="workflow-step-title">Profili Kaydet</span>
          <span class="workflow-step-note">Yeniden kullanılabilir bir standart profil oluşturun.</span>
        </div>
      </div>`;
    }

    if (mode === "endpoint") {
      return `
      <div class="workflow-strip">
        <div class="workflow-step">
          <span class="workflow-step-number">Adım 1</span>
          <span class="workflow-step-title">Çözümü Seç</span>
          <span class="workflow-step-note">Üretilmiş çözüm klasörünü seçip varlıkları yükleyin.</span>
        </div>
        <div class="workflow-step">
          <span class="workflow-step-number">Adım 2</span>
          <span class="workflow-step-title">Reçeteyi Seç</span>
          <span class="workflow-step-note">Varlık, reçete ve uygun alanı belirleyin.</span>
        </div>
        <div class="workflow-step">
          <span class="workflow-step-number">Adım 3</span>
          <span class="workflow-step-title">Önizle ve Ekle</span>
          <span class="workflow-step-note">Değişecek dosyaları önizleyin, sonra endpoint'i ekleyin.</span>
        </div>
      </div>`;
    }

    if (mode === "document") {
      return `
      <div class="workflow-strip">
        <div class="workflow-step">
          <span class="workflow-step-number">Adım 1</span>
          <span class="workflow-step-title">Çıktıyı Seç</span>
          <span class="workflow-step-note">Üretilmiş çözüm klasörünü gösterin.</span>
        </div>
        <div class="workflow-step">
          <span class="workflow-step-number">Adım 2</span>
          <span class="workflow-step-title">Manifesti Oku</span>
          <span class="workflow-step-note">Üretilen dosyaları ve manifesti girdi olarak kullanın.</span>
        </div>
        <div class="workflow-step">
          <span class="workflow-step-number">Adım 3</span>
          <span class="workflow-step-title">Doküman Yaz</span>
          <span class="workflow-step-note">API odaklı dokümantasyon çıktısı oluşturun.</span>
        </div>
      </div>`;
    }

    return `
    <div class="workflow-strip">
      <div class="workflow-step">
        <span class="workflow-step-number">Adım 1</span>
        <span class="workflow-step-title">Şema Seç</span>
        <span class="workflow-step-note">Tabloları tanımlayan SQL dosyasını seçin.</span>
      </div>
      <div class="workflow-step">
        <span class="workflow-step-number">Adım 2</span>
        <span class="workflow-step-title">Modu Seç</span>
        <span class="workflow-step-note">Varsayılan mod referans projeyi örnek alıp yeni proje üretir. Hazır paketler sıfırdan proje oluşturur.</span>
      </div>
      <div class="workflow-step">
        <span class="workflow-step-number">Adım 3</span>
        <span class="workflow-step-title">Ekleri Belirle</span>
        <span class="workflow-step-note">SQL, çalışma zamanı ve LLM sekmelerini yalnızca gerektiğinde kullanın.</span>
      </div>
      <div class="workflow-step">
        <span class="workflow-step-number">Adım 4</span>
        <span class="workflow-step-title">Çalıştır ve İncele</span>
        <span class="workflow-step-note">Üretimi çalıştırın, sonra Özet ve Loglar sekmelerini kontrol edin.</span>
      </div>
    </div>`;
  }

  private getPrimaryFieldsHtml(mode: string): string {
    if (mode === "learn") {
      return `
      <label class="field">
        <span class="field-label">Proje Yolu</span>
        <input id="project" type="text" list="recentProjects" placeholder="c:\\work\\ExistingApi" />
        <span class="field-caption">Analiz edilecek mevcut kod tabanını seçin.</span>
      </label>
      <label class="field">
        <span class="field-label">Profil Çıktı Klasörü</span>
        <input id="output" type="text" value="profiles" />
        <span class="field-caption">Öğrenilen profil JSON dosyasının yazılacağı klasör.</span>
      </label>`;
    }

    if (mode === "endpoint") {
      return `
      <label class="field">
        <span class="field-label">Çözüm Klasörü</span>
        <div class="field-input-row">
          <input id="project" type="text" list="recentProjects" placeholder="GeneratedApi" />
          <button id="pickProjectFolder" type="button" class="ghost-button">Klasör Seç</button>
        </div>
        <span class="field-caption">Endpoint eklenecek, daha önce üretilmiş çözüm klasörü.</span>
      </label>
      <label class="field">
        <span class="field-label">Varlık</span>
        <div class="field-input-row">
          <select id="entity"><option value="">Önce varlıkları yükleyin</option></select>
          <button id="loadEntities" type="button" class="ghost-button">Varlıkları Yükle</button>
        </div>
        <span id="entityCaption" class="field-caption">Çözümdeki varlıklar okunur.</span>
      </label>
      <label class="field">
        <span class="field-label">Reçete</span>
        <select id="recipe">
          <option value="GetByCode">GetByCode – alana göre tek kayıt</option>
          <option value="GetActiveList">GetActiveList – aktif kayıtlar</option>
          <option value="Search">Search – metin araması</option>
          <option value="GetByDateRange">GetByDateRange – tarih aralığı</option>
          <option value="BulkInsert">BulkInsert – toplu ekleme</option>
        </select>
        <span class="field-caption">Eklenecek endpoint tipi.</span>
      </label>
      <label class="field">
        <span class="field-label">Alan</span>
        <select id="field"></select>
        <span id="fieldCaption" class="field-caption">Reçeteye uygun alanlar listelenir.</span>
      </label>`;
    }

    if (mode === "document") {
      return `
      <label class="field">
        <span class="field-label">Çıktı Klasörü</span>
        <input id="output" type="text" value="GeneratedApi" />
        <span class="field-caption">Dokümantasyon üretmek için kullanılacak çözüm kök klasörü.</span>
      </label>`;
    }

    return `
    <label class="field">
      <span class="field-label">SQL Dosyası</span>
      <div class="field-input-row">
        <input id="schema" type="text" list="recentSchemas" placeholder="examples\\users.sql" />
        <button id="pickSchemaFile" type="button" class="ghost-button">SQL Dosyası Seç</button>
      </div>
      <span class="field-caption">Tablolar için kaynak alınacak SQL dosyası.</span>
    </label>
    <label class="field">
      <span class="field-label">Çıktı Klasörü</span>
      <input id="output" type="text" list="recentOutputs" value="GeneratedApi" />
      <span id="outputCaption" class="field-caption">Yeni projenin adı ve klasörü burada belirlenir.</span>
    </label>`;
  }

  private getConfigurationTabHtml(mode: string): string {
    const frameworkOptions = this.buildFrameworkOptions();
    const profileOptions = this.buildProfileOptions();

    if (mode === "endpoint") {
      return `
    <div class="tab-stack">
      <div class="status-card">
        <span class="status-card-title">Endpoint Reçeteleri</span>
        <span class="status-card-copy">Reçeteler mevcut koda dokunmadan yanına partial dosyalar ekler; testler ve API dokümanı da güncellenir.</span>
        <ul class="status-card-list">
          <li><strong>GetByCode</strong>: seçilen alana göre tek kayıt döner, yoksa 404.</li>
          <li><strong>GetActiveList</strong>: bool alanı true olan kayıtları listeler (varsayılan IsActive).</li>
          <li><strong>Search</strong>: metin alanında arama yapar, terim zorunludur.</li>
          <li><strong>GetByDateRange</strong>: tarih alanında from/to aralığı uygular.</li>
          <li><strong>BulkInsert</strong>: kayıtları toplu ekler; anahtar varsa hiçbirini eklemez (409).</li>
        </ul>
      </div>
    </div>`;
    }

    if (isGenerationMode(mode)) {
      return `
      <div class="tab-stack">
        <div class="status-card">
          <span id="frameworkSummaryTitle" class="status-card-title">Default Framework</span>
          <span id="frameworkSummaryCopy" class="status-card-copy">Seçilen projeyi referans alıp yeni bir proje üretin.</span>
          <ul id="frameworkSummaryList" class="status-card-list">
            <li>Bu modda Referans Proje zorunludur.</li>
            <li>Seçilen proje ana standart kaynağı olarak kullanılır.</li>
            <li>Yeni projenin adı ve konumu Çıktı Klasörü alanından belirlenir.</li>
          </ul>
        </div>



        <div class="section-block">
          <span class="section-title">Proje Tipi</span>
          <span class="section-note">Default Framework seçili projeyi örnek alır ve yeni proje üretir. Minimal ve Kurumsal seçenekleri sıfırdan yeni proje oluşturur.</span>
          <label class="field">
            <span class="field-label">Hazır Paket</span>
            <select id="framework">
              ${frameworkOptions}
            </select>
            <span class="field-caption">Üretimin nasıl başlayacağını seçin.</span>
          </label>
        </div>

        <div id="defaultFrameworkProjectSection" class="section-block">
          <span class="section-title">Referans Proje</span>
          <span class="section-note">Yalnızca Default Framework modunda kullanılır. Seçilen proje örnek alınır, yerinde güncellenmez.</span>
          <label class="field">
            <span class="field-label">Referans Proje</span>
            <input id="project" type="text" list="recentProjects" placeholder="c:\\work\\ExistingApi or c:\\work\\ExistingApi\\ExistingApi.csproj" />
            <span class="field-caption">Bir proje klasörü, .csproj veya .sln seçebilirsiniz. Bu seçim yalnızca standart ve mevcut tablo yapısını örnek almak için kullanılır.</span>
          </label>
          <div class="tab-actions">
            <button id="pickProjectFolder" type="button" class="ghost-button">Klasör Seç</button>
            <button id="pickProjectFile" type="button" class="ghost-button">Dosya Seç</button>
          </div>
        </div>

        <div id="profileSection" class="section-block">
          <span id="profileLabel" class="section-title">Ek Profil</span>
          <span id="profileCaption" class="section-note">Referans proje ana standardı sağlar. İsterseniz bunun üstüne kayıtlı bir profil uygulayın.</span>
          <label class="field">
            <span class="field-label">Profil</span>
            <select id="profile">
              ${profileOptions}
            </select>
            <span class="field-caption">İsteğe bağlı olarak referans projenin üstüne ek kurallar bindirebilirsiniz.</span>
          </label>
        </div>

        <div class="section-block">
          <span class="section-title">Çakışma Yönetimi</span>
          <span class="section-note">Üretilen dosyalar zaten varsa ne olacağını seçin.</span>
          <label class="field">
            <span class="field-label">Üzerine Yazma Modu</span>
            <select id="overwriteMode">
              <option value="Skip">Mevcut dosyaları atla</option>
              <option value="Overwrite">Mevcut dosyaların üzerine yaz</option>
              <option value="Fail">Çakışmada hata ver</option>
            </select>
            <span class="field-caption">Bu ayar üretim sırasında yazma davranışını belirler.</span>
          </label>
          <div class="tab-actions">
            <button id="refreshProfiles" type="button" class="ghost-button">Profilleri Yenile</button>
          </div>
        </div>
      </div>`;
    }

    return `
    <div class="tab-stack">
      <div class="status-card">
        <span class="status-card-title">Profil Çalışma Alanı</span>
        <span class="status-card-copy">Mevcut profilleri görmek ve profil dosyası oluşturup düzenledikten sonra listeyi yenilemek için bu alanı kullanın.</span>
        <ul class="status-card-list">
          <li>Standart profiller yerleşik ve öğrenilmiş profilleri içerir.</li>
          <li>Hazır paketler, yeniden kullanılabilir standart profillerden ayrıdır.</li>
        </ul>
      </div>

      <div class="tab-actions">
        <button id="refreshProfiles" type="button" class="ghost-button">Profilleri Yenile</button>
      </div>
    </div>`;
  }

  private getDatabaseTabHtml(mode: string): string {
    if (!isGenerationMode(mode)) {
      return `
      <div class="info-card">
        <span class="info-card-title">Veritabanı Tasarımcısı</span>
        <ul class="info-list">
          <li>SQL tasarımcısı, API projesi oluştururken veya üretirken kullanılabilir.</li>
          <li>Önce bir şema dosyası seçin, sonra tabloları incelemek, eklemek veya kaldırmak için bu sekmeyi kullanın.</li>
        </ul>
      </div>`;
    }

    return `
    <div class="tab-stack">
      <div class="status-card">
        <span class="status-card-title">SQL Şeması Akışı</span>
        <span class="status-card-copy">Üretimden önce seçilen SQL dosyasını kaynak olarak kullanın.</span>
        <ul class="status-card-list">
          <li>Mevcut tabloları görmek için geçerli SQL dosyasını yükleyin.</li>
          <li>Tablo eklemek, seçilen SQL dosyasına CREATE TABLE betiği ekler.</li>
          <li>Varsayılan Framework modunda SQL dosyasında olmayan tablolar, mevcut proje tablolarını silmez.</li>
        </ul>
      </div>

      <div class="designer-toolbar">
        <div>
          <div id="schemaDesignerStatus" class="token-note">Tabloları yönetmek için bir şema dosyası seçip yükleyin.</div>
          <div id="schemaDesignerResolvedPath" class="field-caption"></div>
        </div>
        <button id="loadSchemaDesigner" type="button" class="ghost-button">Seçilen SQL Dosyasını Yükle</button>
      </div>

      <div class="database-layout">
        <div class="designer-card">
          <span class="info-card-title">Mevcut Tablolar</span>
          <div id="schemaTablesList" class="designer-grid"></div>
          <div id="schemaTablesEmpty" class="empty-state">Henüz tablo yüklenmedi.</div>
        </div>

        <div class="designer-card">
          <span class="info-card-title">Tablo Ekle</span>
          <label class="field">
            <span class="field-label">Tablo Adı</span>
            <input id="draftTableName" type="text" placeholder="Orders" />
            <span class="field-caption">Kaydetmek, seçilen SQL dosyasına yeni bir CREATE TABLE betiği ekler.</span>
          </label>
          <div id="draftColumns" class="designer-grid"></div>
          <div class="tab-actions">
            <button id="addDraftColumn" type="button" class="ghost-button">Sütun Ekle</button>
            <button id="appendSchemaTable" type="button">Tabloyu SQL Dosyasına Ekle</button>
          </div>
        </div>
      </div>
    </div>`;
  }
  private getRuntimeTabHtml(mode: string): string {
    if (!isGenerationMode(mode)) {
      return `
      <div class="info-card">
        <span class="info-card-title">Çalışma Zamanı Ayarları</span>
        <ul class="info-list">
          <li>Çalışma zamanı yapılandırması yalnızca API projesi üretirken kullanılır.</li>
          <li>Bağlantı dizesi ve kimlik doğrulama ayarlarını uygulamak için oluşturma veya üretim akışından bir hazır paket seçin.</li>
        </ul>
      </div>`;
    }

    return `
    <div class="tab-stack">
      <div class="status-card">
        <span class="status-card-title">Çalışma Zamanı ve Ekler</span>
        <span class="status-card-copy">Bağlantı dizesi, kimlik doğrulama, testler ve örnek istekler burada yönetilir.</span>
        <ul class="status-card-list">
          <li>Default Framework bu kararları seçilen referans projeden öğrenir.</li>
          <li>Minimal ve Kurumsal paketler, sıfırdan proje oluştururken aşağıdaki kontrol listesini kullanır.</li>
        </ul>
      </div>

      <div class="section-block">
        <span class="section-title">Bağlantı Dizesi</span>
        <span class="section-note">Seçilen hazır paket çalışma zamanı yapılandırmasını destekliyorsa appsettings içine yazılır.</span>
        <label class="field">
          <span class="field-label">Bağlantı Dizesi</span>
          <textarea id="connectionString" placeholder="Server=.;Database=MyApiDb;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"></textarea>
          <span class="field-caption">Hazır varsayılan bağlantı dizesi şablonunu korumak için boş bırakın.</span>
        </label>
      </div>

      <div id="defaultFrameworkFeatureNote" class="section-block">
        <span class="section-title">Varsayılan Framework Davranışı</span>
        <span class="section-note">Referans proje yalnızca öğrenme kaynağıdır; yeni proje üretimi bu kararları örnek alır.</span>
        <ul class="status-card-list">
          <li>Kimlik doğrulama, testler ve Postman koleksiyonu davranışı seçilen referans projeden öğrenilir.</li>
          <li>Aşağıdaki kontrol listesi bu modda bilinçli olarak pasiftir.</li>
        </ul>
      </div>

      <div id="frameworkFeatureChecklist" class="section-block">
        <span class="section-title">İsteğe Bağlı Kontrol Listesi</span>
        <span class="section-note">Bu seçenekler yalnızca hazır paketten yeni proje üretirken sorulur.</span>

        <label class="toggle-row">
          <span class="toggle-copy">
            <span class="toggle-title">Windows Kimlik Doğrulaması</span>
            <span class="toggle-note">Negotiate kimlik doğrulaması, controller yetkilendirme öznitelikleri ve launch profile ayarlarını ekler.</span>
          </span>
          <input id="featureWindowsAuthentication" class="toggle-input" type="checkbox" />
        </label>

        <label class="toggle-row">
          <span class="toggle-copy">
            <span class="toggle-title">Birim Testleri</span>
            <span class="toggle-note">Seçilen yapı testleri destekliyorsa birim test projesi ve CRUD odaklı test iskeletleri oluşturur.</span>
          </span>
          <input id="featureUnitTests" class="toggle-input" type="checkbox" />
        </label>

        <label class="toggle-row">
          <span class="toggle-copy">
            <span class="toggle-title">Örnek Postman Koleksiyonu</span>
            <span class="toggle-note">API docs klasörü altında çalıştırmaya hazır CRUD istekleri içeren Postman v2.1 koleksiyonu oluşturur.</span>
          </span>
          <input id="featurePostmanCollection" class="toggle-input" type="checkbox" />
        </label>
      </div>
    </div>`;
  }

  private getLlmTabHtml(): string {
    return `
    <div class="tab-stack">
      <div class="status-card">
        <span class="status-card-title">İsteğe Bağlı LLM Düzeltme Adımı</span>
        <span class="status-card-copy">LLM desteği temel şablonlar üretildikten sonra çalışır. İsteğe bağlıdır ve stateless yapıdadır.</span>
        <ul class="status-card-list">
          <li>Yalnızca deterministik şablon çıktısı istiyorsanız kapalı bırakın.</li>
          <li>Yalnızca URL, model ve token hazır olduğunda açın.</li>
        </ul>
      </div>

      <label class="toggle-row">
        <span class="toggle-copy">
          <span class="toggle-title">LLM Desteğini Kullan</span>
          <span class="toggle-note">Etkinleştirildiğinde üretici, şablon üretiminden sonra ek bir düzeltme adımı çalıştırır.</span>
        </span>
        <input id="llmEnabled" class="toggle-input" type="checkbox" ${this.llmSettings.enabled ? "checked" : ""} />
      </label>
      <div id="llmRequiredNote" class="token-note">${this.llmSettings.enabled
        ? "LLM desteği etkin. URL, model ve token zorunludur."
        : "LLM desteği kapalı. URL, model ve token isteğe bağlıdır."}</div>

      <div class="section-block">
        <span class="section-title">Sağlayıcı Ayarları</span>
        <span class="section-note">Bu değerler yalnızca LLM desteği etkin olduğunda kullanılır.</span>

        <label class="field">
          <span class="field-label">LLM URL</span>
          <input id="llmUrl" type="text" value="${escapeHtml(this.llmSettings.url)}" placeholder="https://api.openai.com/v1" />
          <span class="field-caption">OpenAI uyumlu temel URL veya tam chat/completions endpoint adresi.</span>
        </label>

        <label class="field">
          <span class="field-label">Model</span>
          <input id="llmModel" type="text" value="${escapeHtml(this.llmSettings.model)}" placeholder="gpt-4.1-mini" />
          <span class="field-caption">Düzeltme için kullanılacak model veya deployment adı.</span>
        </label>

        <label class="field">
          <span class="field-label">Eşzamanlılık Modu</span>
          <select id="llmConcurrencyMode">
            <option value="auto" ${typeof this.llmSettings.maxConcurrency === "number" ? "" : "selected"}>Auto</option>
            <option value="manual" ${typeof this.llmSettings.maxConcurrency === "number" ? "selected" : ""}>Manual</option>
          </select>
          <span class="field-caption">Auto modu prompt boyutuna göre güvenli bir eşzamanlılık değeri seçer. Manual modunda sabit bir değer verirsiniz.</span>
        </label>

        <label class="field">
          <span class="field-label">Maksimum Eşzamanlılık</span>
          <input id="llmMaxConcurrency" type="number" min="1" step="1" value="${typeof this.llmSettings.maxConcurrency === "number" ? String(this.llmSettings.maxConcurrency) : ""}" placeholder="4" ${typeof this.llmSettings.maxConcurrency === "number" ? "" : "disabled"} />
          <span class="field-caption">Manual modunda 1 veya daha büyük bir tam sayı girin. Daha yüksek değerler daha hızlı ama daha yoğun LLM isteği gönderir.</span>
        </label>

        <label class="field">
          <span class="field-label">Token</span>
          <input id="llmToken" type="password" value="" placeholder="VS Code gizli depolamasında güvenle saklanır" />
          <span id="llmTokenStatus" class="token-note">${this.llmSettings.hasToken
            ? "Kayıtlı token var. Korumak için boş bırakın veya yenisini girin."
            : "Henüz kayıtlı token yok. LLM isteklerini etkinleştirmek için bir token girin."}</span>
        </label>
      </div>

      <div class="section-grid">
        <div class="info-card">
          <span class="info-card-title">Nasıl Çalışır</span>
          <ul class="info-list">
            <li>1. Önce şema, hazır paket ve profil seçimlerinden şablonlar üretilir.</li>
            <li>2. Üretici, her hedef dosyayı STANDARD-PROFILE.json ve prompt bağlamı ile stateless olarak gönderir.</li>
            <li>3. LLM tam revize edilmiş dosyayı döndürür ve üretici düzeltilmiş sürümü çıktıya yazar.</li>
          </ul>
        </div>

        <div class="info-card">
          <span class="info-card-title">Önerilen Kullanım</span>
          <ul class="info-list">
            <li>Tekrarlanabilir iskeletler için deterministik model ayarı kullanın.</li>
            <li>Doğru proje tipi ve çalışma zamanı ayarlarını seçtikten sonra LLM desteğini etkinleştirin.</li>
            <li>Süreci stateless tutun: mevcut dosya, profil ve kimlik bilgileri yeterlidir.</li>
          </ul>
        </div>
      </div>

      <div class="tab-actions">
        <button id="saveLlmSettings" type="button" class="ghost-button">LLM Ayarlarını Kaydet</button>
      </div>
    </div>`;
  }

  private getConfigurationTabLabel(mode: string): string {
    if (mode === "learn") {
      return "Çalışma Alanı";
    }

    if (mode === "document") {
      return "Detaylar";
    }

    return "Kurulum";
  }

  private buildProfileOptions(): string {
    const options = [`<option value="">Built-in Default</option>`];

    for (const profile of this.getStandardProfiles()) {
      const profileName = profile.displayName || profile.name;
      const label = profileName;
      options.push(`<option value="${escapeHtml(profile.path)}">${escapeHtml(label)}</option>`);
    }

    return options.join("");
  }

  private buildFrameworkOptions(): string {
    const options = [`<option value="">Varsayılan Framework</option>`];

    for (const profile of this.getFrameworkProfiles()) {
      const label = this.getFrameworkOptionLabel(profile);
      options.push(`<option value="${escapeHtml(profile.path)}">${escapeHtml(label)}</option>`);
    }

    return options.join("");
  }

  private getFrameworkFeatureDefaults(): Record<string, { windowsAuthentication: boolean; unitTests: boolean; postmanCollection: boolean }> {
    return Object.fromEntries(this.getFrameworkProfiles().map((profile) => [
      profile.path,
      {
        windowsAuthentication: profile.frameworkUseWindowsAuthentication ?? false,
        unitTests: profile.frameworkIncludeUnitTests ?? true,
        postmanCollection: profile.frameworkGeneratePostmanCollection ?? false
      }
    ]));
  }

  private getFrameworkBehavior(): Record<string, { id?: string; locksStandardProfile: boolean }> {
    return Object.fromEntries(this.getFrameworkProfiles().map((profile) => [
      profile.path,
      {
        id: profile.frameworkId,
        locksStandardProfile: true
      }
    ]));
  }

  private getFrameworkOptionLabel(profile: StoredProfileSummary): string {
    return profile.frameworkId === "minimal-api-swagger"
      ? "Minimal Project"
      : profile.frameworkId === "enterprise-controller-loghelper-swagger"
        ? "Enterprise Project"
        : profile.frameworkDisplayName || profile.name;
  }

  private getFrameworkProfiles(): StoredProfileSummary[] {
    return this.profiles.filter((profile) =>
      profile.kind === "FrameworkPreset" &&
      profile.frameworkId !== "aspnet-controller-swagger");
  }

  private getStandardProfiles(): StoredProfileSummary[] {
    return this.profiles.filter((profile) => profile.kind !== "FrameworkPreset");
  }

  private getModeDefinition(): ModeDefinition {
    return MODE_DEFINITIONS[this.currentMode] ?? MODE_DEFINITIONS.create;
  }

  private render(): void {
    if (this.view) {
      this.view.title = "API Generator";
      this.view.description = this.getModeDefinition().title;
      this.view.webview.html = this.getHtml(this.view.webview);
    }

    if (this.panel) {
      this.panel.webview.html = this.getHtml(this.panel.webview);
    }
  }
}

function isGenerationMode(mode: string): boolean {
  return mode === "create" || mode === "generate";
}

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;");
}

function getNonce(): string {
  const charset = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  let value = "";

  for (let index = 0; index < 32; index += 1) {
    value += charset.charAt(Math.floor(Math.random() * charset.length));
  }

  return value;
}
