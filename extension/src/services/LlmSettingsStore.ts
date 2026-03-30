import * as vscode from "vscode";

export interface StoredLlmSettings {
  enabled: boolean;
  url: string;
  model: string;
  maxConcurrency?: number;
  hasToken: boolean;
}

export interface LlmSettingsInput {
  enabled?: boolean;
  url?: string;
  model?: string;
  maxConcurrency?: number;
  token?: string;
}

export interface ResolvedLlmSettings extends StoredLlmSettings {
  token: string;
}

export class LlmSettingsStore {
  private static readonly SettingsKey = "apiGenerator.llm.settings";
  private static readonly TokenKey = "apiGenerator.llm.token";

  public constructor(private readonly context: vscode.ExtensionContext) {}

  public async getSettings(): Promise<StoredLlmSettings> {
    const stored = this.context.globalState.get<Partial<StoredLlmSettings>>(LlmSettingsStore.SettingsKey, {});
    const token = await this.context.secrets.get(LlmSettingsStore.TokenKey);

    return {
      enabled: stored.enabled ?? false,
      url: stored.url?.trim() ?? "",
      model: stored.model?.trim() ?? "",
      maxConcurrency: typeof stored.maxConcurrency === "number" && stored.maxConcurrency > 0
        ? Math.trunc(stored.maxConcurrency)
        : undefined,
      hasToken: Boolean(token && token.trim().length > 0)
    };
  }

  public async saveSettings(input: LlmSettingsInput): Promise<StoredLlmSettings> {
    const current = await this.getSettings();
    const nextSettings: StoredLlmSettings = {
      enabled: input.enabled ?? current.enabled,
      url: input.url?.trim() ?? current.url,
      model: input.model?.trim() ?? current.model,
      maxConcurrency: typeof input.maxConcurrency === "number" && input.maxConcurrency > 0
        ? Math.trunc(input.maxConcurrency)
        : undefined,
      hasToken: current.hasToken
    };

    await this.context.globalState.update(LlmSettingsStore.SettingsKey, {
      enabled: nextSettings.enabled,
      url: nextSettings.url,
      model: nextSettings.model,
      maxConcurrency: nextSettings.maxConcurrency
    });

    const token = input.token?.trim();
    if (typeof token === "string" && token.length > 0) {
      await this.context.secrets.store(LlmSettingsStore.TokenKey, token);
      nextSettings.hasToken = true;
    }

    return nextSettings;
  }

  public async resolveForExecution(input: LlmSettingsInput): Promise<ResolvedLlmSettings> {
    const saved = await this.saveSettings(input);
    const storedToken = await this.context.secrets.get(LlmSettingsStore.TokenKey);
    const token = input.token?.trim() || storedToken?.trim() || "";

    return {
      ...saved,
      token
    };
  }
}
