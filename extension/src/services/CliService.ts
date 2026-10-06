import * as cp from "node:child_process";
import * as fs from "node:fs/promises";
import * as path from "node:path";
import * as vscode from "vscode";
import { WorkspaceService } from "./WorkspaceService";

export type CliCommand = "generate" | "learn" | "analyze" | "document" | "add-endpoint" | "entities";

export interface CliExecutionRequest {
  command: CliCommand;
  args: string[];
  llmToken?: string;
}

/** One line of the CLI's `--log-format Json` output. */
export interface CliEvent {
  level: "info" | "warning" | "error";
  event: string;
  message: string;
  [key: string]: unknown;
}

export interface CliExecutionResult {
  exitCode: number;
  /** Human readable output (the messages of info and warning events). */
  stdout: string;
  /** Human readable errors. */
  stderr: string;
  events: CliEvent[];
  manifest?: GenerationManifestSummary;
}

export interface GenerationManifestSummary {
  SolutionName: string;
  OutputPath: string;
  GeneratedAtUtc: string;
  ProfilePath?: string | null;
  FrameworkPath?: string | null;
  EntityCount: number;
  DryRun: boolean;
  OverwriteMode: string;
  Warnings?: string[];
  Llm?: {
    Enabled: boolean;
    Applied: boolean;
    Url?: string | null;
    Model?: string | null;
    ConfiguredMaxConcurrency?: number | null;
    EffectiveMaxConcurrency?: number;
    TargetFiles: number;
    RefinedFiles: number;
    SkippedFiles: number;
    Errors: string[];
  };
  Summary: {
    TotalFiles: number;
    Created: number;
    Updated: number;
    Unchanged: number;
    Conflicts: number;
  };
  GeneratedFiles: Array<{
    RelativePath: string;
    Category: string;
    Status: string;
  }>;
}

export class CliService {
  private readonly outputChannel = vscode.window.createOutputChannel("API Generator");
  public constructor(
    private readonly workspaceService: WorkspaceService,
    private readonly extensionUri: vscode.Uri
  ) {}

  // Resolved per call so a changed API_GENERATOR_CLI_PATH takes effect without reloading the extension.
  private get bundledCliExecutablePath(): string {
    return resolveCliPath(this.extensionUri.fsPath);
  }

  public async execute(request: CliExecutionRequest): Promise<CliExecutionResult> {
    let workspacePath: string;
    try {
      await this.ensureBundledCliExists();
      workspacePath = this.workspaceService.getWorkspaceFolder().uri.fsPath;
    } catch (error) {
      // Always answer with a result so the webview never stays in its running state.
      return failedResult(error instanceof Error ? error.message : String(error));
    }

    // In this mode the CLI reports progress, warnings, the manifest and errors as JSON lines.
    const args = [request.command, ...request.args, "--log-format", "Json"];

    return new Promise<CliExecutionResult>((resolve) => {
      const [executable, executableArgs] = buildCliInvocation(this.bundledCliExecutablePath, args);
      const child = cp.spawn(executable, executableArgs, {
        cwd: workspacePath,
        shell: false,
        env: {
          ...process.env,
          API_GENERATOR_LLM_TOKEN: request.llmToken ?? process.env.API_GENERATOR_LLM_TOKEN ?? ""
        }
      });

      let stdout = "";
      let stderr = "";
      this.outputChannel.appendLine(`> ${executable} ${executableArgs.join(" ")}`);

      child.stdout.on("data", (chunk) => {
        stdout += chunk.toString();
      });

      child.stderr.on("data", (chunk) => {
        stderr += chunk.toString();
      });

      child.on("error", (error) => {
        resolve(failedResult(`${stderr}${stderr.length > 0 ? "\n" : ""}${error.message}`));
      });

      child.on("close", (exitCode) => {
        const result = parseCliOutput(exitCode ?? 1, stdout, stderr);
        for (const text of [result.stdout, result.stderr].filter((value) => value.length > 0)) {
          this.outputChannel.appendLine(text);
        }
        this.outputChannel.appendLine(`< exit code ${result.exitCode}`);
        resolve(result);
      });
    });
  }

  public async ensureBuilt(): Promise<void> {
    await this.ensureBundledCliExists();
    void vscode.window.showInformationMessage("Paketlenmiş API Generator CLI hazır.");
  }

  private async ensureBundledCliExists(): Promise<void> {
    try {
      await fs.access(this.bundledCliExecutablePath);
    } catch {
      throw new Error(`Paketlenmiş CLI bulunamadı: '${this.bundledCliExecutablePath}'. Önce paket hazırlama adımını (npm run prepare:assets) çalıştırın.`);
    }
  }
}

function failedResult(message: string): CliExecutionResult {
  return { exitCode: 1, stdout: "", stderr: message, events: [] };
}

/** Separates JSON event lines from other output and extracts the manifest event. */
export function parseCliOutput(exitCode: number, stdout: string, stderr: string): CliExecutionResult {
  const events: CliEvent[] = [];
  const plainOut: string[] = [];
  const plainErr: string[] = [];

  const collect = (text: string, plain: string[]): void => {
    for (const line of text.split(/\r?\n/)) {
      if (line.trim().length === 0) {
        continue;
      }

      try {
        const parsed = JSON.parse(line) as CliEvent;
        if (parsed && typeof parsed.event === "string") {
          events.push(parsed);
          continue;
        }
      } catch {
        // Not an event line (for example a runtime failure before logging started).
      }

      plain.push(line);
    }
  };

  collect(stdout, plainOut);
  collect(stderr, plainErr);

  const manifestEvent = events.find((event) => event.event === "manifest");
  const messages = events
    .filter((event) => event.event !== "manifest" && event.level !== "error")
    .map((event) => (event.level === "warning" ? `Uyarı: ${event.message}` : event.message));
  const errors = events.filter((event) => event.level === "error").map((event) => event.message);

  return {
    exitCode,
    stdout: [...messages, ...plainOut].join("\n"),
    stderr: [...errors, ...plainErr].join("\n"),
    events,
    manifest: manifestEvent ? (toPascalCase(manifestEvent.manifest) as GenerationManifestSummary) : undefined
  };
}

function toPascalCase(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(toPascalCase);
  }

  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value as Record<string, unknown>).map(([key, entry]) => [
      key.length > 0 ? key[0].toUpperCase() + key.slice(1) : key,
      toPascalCase(entry)
    ]));
  }

  return value;
}

// API_GENERATOR_CLI_PATH lets development and test runs point at a locally built CLI.
// The bundled framework-dependent ApiGenerator.Cli.dll runs through `dotnet` outside Windows.
export function resolveCliPath(extensionPath: string, platform: NodeJS.Platform = process.platform): string {
  const overridePath = process.env.API_GENERATOR_CLI_PATH?.trim();
  if (overridePath) {
    return overridePath;
  }

  const fileName = platform === "win32" ? "ApiGenerator.Cli.exe" : "ApiGenerator.Cli.dll";
  return path.join(extensionPath, "bundled", "cli", fileName);
}

export function buildCliInvocation(cliPath: string, args: string[]): [string, string[]] {
  return cliPath.toLowerCase().endsWith(".dll")
    ? ["dotnet", [cliPath, ...args]]
    : [cliPath, args];
}
