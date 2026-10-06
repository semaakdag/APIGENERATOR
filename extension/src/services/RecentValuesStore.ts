import * as vscode from "vscode";

export interface RecentValues {
  schemas: string[];
  outputs: string[];
  projects: string[];
}

/** Remembers the last few schema, output and project paths used in successful runs. */
export class RecentValuesStore {
  private static readonly Key = "apiGenerator.recentValues";
  private static readonly Limit = 5;

  public constructor(private readonly context: vscode.ExtensionContext) {}

  public get(): RecentValues {
    const stored = this.context.globalState.get<Partial<RecentValues>>(RecentValuesStore.Key, {});
    return {
      schemas: Array.isArray(stored.schemas) ? stored.schemas : [],
      outputs: Array.isArray(stored.outputs) ? stored.outputs : [],
      projects: Array.isArray(stored.projects) ? stored.projects : []
    };
  }

  public async remember(values: { schema?: string; output?: string; project?: string }): Promise<RecentValues> {
    const current = this.get();
    const push = (list: string[], value: string | undefined): string[] => {
      const trimmed = (value ?? "").trim();
      return trimmed.length === 0
        ? list
        : [trimmed, ...list.filter((item) => item !== trimmed)].slice(0, RecentValuesStore.Limit);
    };
    const next: RecentValues = {
      schemas: push(current.schemas, values.schema),
      outputs: push(current.outputs, values.output),
      projects: push(current.projects, values.project)
    };
    await this.context.globalState.update(RecentValuesStore.Key, next);
    return next;
  }
}
