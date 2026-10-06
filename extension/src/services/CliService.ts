import * as cp from "node:child_process";
import * as fs from "node:fs/promises";
import * as path from "node:path";
import * as vscode from "vscode";
import { WorkspaceService } from "./WorkspaceService";

export interface CliExecutionRequest {
  command: "generate" | "learn" | "analyze" | "document";
  args: string[];
  llmToken?: string;
}

export interface CliExecutionResult {
  exitCode: number;
  stdout: string;
  stderr: string;
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
  private readonly bundledCliExecutablePath: string;

  public constructor(
    private readonly workspaceService: WorkspaceService,
    extensionUri: vscode.Uri
  ) {
    this.bundledCliExecutablePath = resolveCliPath(extensionUri.fsPath);
  }

  public async execute(request: CliExecutionRequest): Promise<CliExecutionResult> {
    await this.ensureBundledCliExists();

    const workspaceFolder = this.workspaceService.getWorkspaceFolder();
    const workspacePath = workspaceFolder.uri.fsPath;
    const args = [request.command, ...request.args];
    const outputDirectory = this.getOutputDirectory(workspacePath, request.args);
    const commandStartedAt = Date.now();

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
        this.outputChannel.append(chunk.toString());
      });

      child.stderr.on("data", (chunk) => {
        stderr += chunk.toString();
        this.outputChannel.append(chunk.toString());
      });

      child.on("error", (error) => {
        resolve({
          exitCode: 1,
          stdout,
          stderr: `${stderr}${stderr.length > 0 ? "\n" : ""}${error.message}`
        });
      });

      child.on("close", (exitCode) => {
        const finalExitCode = exitCode ?? 1;
        this.outputChannel.appendLine(`< exit code ${finalExitCode}`);
        void this.tryReadManifest(outputDirectory, finalExitCode, commandStartedAt).then((manifest) => {
          resolve({
            exitCode: finalExitCode,
            stdout,
            stderr,
            manifest
          });
        });
      });
    });
  }

  public async ensureBuilt(): Promise<void> {
    await this.ensureBundledCliExists();
    void vscode.window.showInformationMessage("Bundled API Generator CLI is ready.");
  }

  private async ensureBundledCliExists(): Promise<void> {
    try {
      await fs.access(this.bundledCliExecutablePath);
    } catch {
      throw new Error(`Paketlenmiş CLI bulunamadı: '${this.bundledCliExecutablePath}'. Önce paket hazırlama adımını (npm run prepare:assets) çalıştırın.`);
    }
  }

  private getOutputDirectory(workspacePath: string, args: string[]): string | undefined {
    const outputIndex = args.findIndex((arg) => arg === "--output");
    if (outputIndex < 0 || outputIndex === args.length - 1) {
      return undefined;
    }

    return path.resolve(workspacePath, args[outputIndex + 1]);
  }

  private async tryReadManifest(
    outputDirectory: string | undefined,
    exitCode: number,
    commandStartedAt: number
  ): Promise<GenerationManifestSummary | undefined> {
    if (!outputDirectory || exitCode !== 0) {
      return undefined;
    }

    const manifestPath = path.join(outputDirectory, "generation-manifest.json");

    try {
      const stats = await fs.stat(manifestPath);
      if (stats.mtimeMs < commandStartedAt) {
        return undefined;
      }

      const json = await fs.readFile(manifestPath, "utf8");
      return JSON.parse(json) as GenerationManifestSummary;
    } catch {
      return undefined;
    }
  }
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
