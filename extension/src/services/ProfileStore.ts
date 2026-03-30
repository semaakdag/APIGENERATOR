import * as fs from "node:fs/promises";
import * as path from "node:path";
import * as vscode from "vscode";

export interface StoredProfileSummary {
  name: string;
  displayName?: string;
  path: string;
  description?: string;
  kind?: string;
  frameworkId?: string;
  frameworkDisplayName?: string;
  frameworkIncludeUnitTests?: boolean;
  frameworkUseWindowsAuthentication?: boolean;
  frameworkGeneratePostmanCollection?: boolean;
  hasTemplateOverrides?: boolean;
}

interface ProfileDocument {
  ProfileName?: string;
  Description?: string;
  ProfileKind?: string;
  TemplateOverrides?: Record<string, unknown>;
  Framework?: {
    Id?: string;
    DisplayName?: string;
    IncludeUnitTests?: boolean;
    UseWindowsAuthentication?: boolean;
    GeneratePostmanCollection?: boolean;
  };
}

interface ProfileDirectory {
  rootPath: string;
  scope: "bundled" | "workspace";
}

export class ProfileStore {
  private static readonly RecentProfilesKey = "apiGenerator.recentProfiles";
  private readonly bundledProfilesPath: string;

  public constructor(
    private readonly context: vscode.ExtensionContext,
    extensionUri: vscode.Uri
  ) {
    this.bundledProfilesPath = path.join(extensionUri.fsPath, "bundled", "profiles");
  }

  public async listProfiles(workspacePath?: string): Promise<StoredProfileSummary[]> {
    const recentProfiles = this.context.globalState.get<string[]>(ProfileStore.RecentProfilesKey, []);
    const profileDirectories: ProfileDirectory[] = [
      {
        rootPath: this.bundledProfilesPath,
        scope: "bundled"
      }
    ];

    if (workspacePath && workspacePath.trim().length > 0) {
      profileDirectories.push({
        rootPath: path.join(workspacePath, "profiles"),
        scope: "workspace"
      });
    }

    const discoveredFiles = new Map<string, StoredProfileSummary>();

    for (const directory of profileDirectories) {
      const summaries = await this.scanDirectory(directory.rootPath);
      for (const summary of summaries) {
        const canonicalKey = this.getCanonicalProfileKey(summary.path, profileDirectories);
        discoveredFiles.set(canonicalKey, summary);
      }
    }

    for (const recentPath of recentProfiles) {
      const canonicalKey = this.getCanonicalProfileKey(recentPath, profileDirectories);
      if (discoveredFiles.has(canonicalKey)) {
        continue;
      }

      const summary = await this.readProfileSummary(recentPath);
      if (summary) {
        discoveredFiles.set(canonicalKey, summary);
      }
    }

    return this.withUniqueLabels(this.withDisambiguatedLabels(Array.from(discoveredFiles.values())))
      .sort((left, right) => (left.displayName || left.name).localeCompare(right.displayName || right.name));
  }

  public async rememberProfile(profilePath: string): Promise<void> {
    const recentProfiles = this.context.globalState.get<string[]>(ProfileStore.RecentProfilesKey, []);
    const updatedProfiles = [profilePath, ...recentProfiles.filter((entry) => entry !== profilePath)].slice(0, 10);
    await this.context.globalState.update(ProfileStore.RecentProfilesKey, updatedProfiles);
  }

  private async scanDirectory(directoryPath: string): Promise<StoredProfileSummary[]> {
    const roots = [directoryPath, path.join(directoryPath, "frameworks")];
    const summaries: StoredProfileSummary[] = [];

    for (const root of roots) {
      summaries.push(...await this.scanDirectoryFiles(root));
    }

    return summaries;
  }

  private async scanDirectoryFiles(directoryPath: string): Promise<StoredProfileSummary[]> {
    try {
      const entries = await fs.readdir(directoryPath, { withFileTypes: true });
      const summaries: StoredProfileSummary[] = [];

      for (const entry of entries) {
        if (!entry.isFile() || !entry.name.endsWith(".profile.json")) {
          continue;
        }

        const summary = await this.readProfileSummary(path.join(directoryPath, entry.name));
        if (summary) {
          summaries.push(summary);
        }
      }

      return summaries;
    } catch {
      return [];
    }
  }

  private async readProfileSummary(profilePath: string): Promise<StoredProfileSummary | undefined> {
    try {
      const json = await fs.readFile(profilePath, "utf8");
      const document = JSON.parse(json) as ProfileDocument;
      const normalizedPath = profilePath.replaceAll("\\", "/").toLowerCase();
      const inferredKind = normalizedPath.includes("/profiles/frameworks/") ? "FrameworkPreset" : undefined;
      return {
        name: document.ProfileName?.trim() || path.basename(profilePath, ".profile.json"),
        displayName: undefined,
        path: profilePath,
        description: document.Description?.trim(),
        kind: document.ProfileKind?.trim() || inferredKind,
        frameworkId: document.Framework?.Id?.trim(),
        frameworkDisplayName: document.Framework?.DisplayName?.trim(),
        frameworkIncludeUnitTests: document.Framework?.IncludeUnitTests,
        frameworkUseWindowsAuthentication: document.Framework?.UseWindowsAuthentication,
        frameworkGeneratePostmanCollection: document.Framework?.GeneratePostmanCollection,
        hasTemplateOverrides: !!document.TemplateOverrides && Object.keys(document.TemplateOverrides).length > 0
      };
    } catch {
      return undefined;
    }
  }

  private getCanonicalProfileKey(profilePath: string, directories: ProfileDirectory[]): string {
    const normalizedProfilePath = path.resolve(profilePath).replaceAll("\\", "/").toLowerCase();

    for (const directory of directories) {
      const normalizedRoot = path.resolve(directory.rootPath).replaceAll("\\", "/").toLowerCase();
      if (normalizedProfilePath.startsWith(`${normalizedRoot}/`)) {
        const relativePath = normalizedProfilePath.slice(normalizedRoot.length + 1);
        return relativePath;
      }
    }

    return `recent:${normalizedProfilePath}`;
  }

  private withDisambiguatedLabels(profiles: StoredProfileSummary[]): StoredProfileSummary[] {
    const duplicateCounts = new Map<string, number>();

    for (const profile of profiles) {
      const key = `${profile.kind || "Standard"}::${profile.name}`;
      duplicateCounts.set(key, (duplicateCounts.get(key) || 0) + 1);
    }

    return profiles.map((profile) => {
      const key = `${profile.kind || "Standard"}::${profile.name}`;
      if ((duplicateCounts.get(key) || 0) < 2) {
        return profile;
      }

      const fileName = path.basename(profile.path, ".profile.json");
      const suffix = this.getDuplicateLabelSuffix(profile, fileName);
      return {
        ...profile,
        displayName: `${profile.name} - ${suffix}`
      };
    });
  }

  private getDuplicateLabelSuffix(profile: StoredProfileSummary, fileName: string): string {
    const normalizedFileName = fileName.toLowerCase();
    if (normalizedFileName.includes("no-overrides")) {
      return "Rules Only";
    }

    if (profile.hasTemplateOverrides) {
      return "Exact Templates";
    }

    return fileName;
  }

  private withUniqueLabels(profiles: StoredProfileSummary[]): StoredProfileSummary[] {
    const uniqueProfiles = new Map<string, StoredProfileSummary>();

    for (const profile of profiles) {
      const label = (profile.displayName || profile.name).trim().toLowerCase();
      const key = `${profile.kind || "Standard"}::${label}`;
      if (!uniqueProfiles.has(key)) {
        uniqueProfiles.set(key, profile);
      }
    }

    return Array.from(uniqueProfiles.values());
  }
}
