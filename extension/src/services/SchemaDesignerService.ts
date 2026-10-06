import * as fs from "node:fs/promises";
import * as path from "node:path";
import {
  parseSchemaTableBlocks,
  parseSchemaTables,
  SchemaDesignerTable
} from "./SchemaDesignerSqlParser";
import { WorkspaceService } from "./WorkspaceService";

export type { SchemaDesignerColumn, SchemaDesignerTable } from "./SchemaDesignerSqlParser";

export interface SchemaDesignerDocument {
  schemaPath: string;
  resolvedPath: string;
  tables: SchemaDesignerTable[];
}

export class SchemaDesignerService {
  public constructor(private readonly workspaceService: WorkspaceService) {}

  public async loadSchema(schemaPath: string): Promise<SchemaDesignerDocument> {
    const resolvedPath = this.resolveSchemaPath(schemaPath);
    const sql = await this.tryReadSchemaFile(resolvedPath);

    return {
      schemaPath,
      resolvedPath,
      tables: this.parseSchema(sql)
    };
  }

  public async appendTable(schemaPath: string, table: SchemaDesignerTable): Promise<SchemaDesignerDocument> {
    const resolvedPath = this.resolveSchemaPath(schemaPath);
    const normalizedTable = this.normalizeTable(table);
    const sql = await this.tryReadSchemaFile(resolvedPath);
    const tables = this.parseSchema(sql);

    if (tables.some((entry) => entry.name.toLowerCase() === normalizedTable.name.toLowerCase())) {
      throw new Error(`'${normalizedTable.name}' tablosu seçilen SQL dosyasında zaten var.`);
    }

    await fs.mkdir(path.dirname(resolvedPath), { recursive: true });
    const nextSql = `${sql.trimEnd()}${sql.trim().length > 0 ? "\n\n" : ""}${this.buildCreateTableScript(normalizedTable)}\n`;
    await fs.writeFile(resolvedPath, nextSql, "utf8");
    return this.loadSchema(schemaPath);
  }

  public async deleteTable(schemaPath: string, tableName: string): Promise<SchemaDesignerDocument> {
    const resolvedPath = this.resolveSchemaPath(schemaPath);
    const sql = await this.tryReadSchemaFile(resolvedPath);
    const blocks = parseSchemaTableBlocks(sql);
    const target = blocks.find((entry) => entry.name.toLowerCase() === tableName.trim().toLowerCase());

    if (!target) {
      throw new Error(`'${tableName}' tablosu seçilen SQL dosyasında bulunamadı.`);
    }

    const before = sql.slice(0, target.start).trimEnd();
    const after = sql.slice(target.end).trimStart();
    const updatedSql = [before, after].filter((value) => value.length > 0).join("\n\n");
    await fs.writeFile(resolvedPath, `${updatedSql}${updatedSql.length > 0 ? "\n" : ""}`, "utf8");
    return this.loadSchema(schemaPath);
  }

  private resolveSchemaPath(schemaPath: string): string {
    const trimmed = schemaPath.trim();
    if (trimmed.length === 0) {
      throw new Error("Önce bir şema dosyası seçin.");
    }

    if (path.isAbsolute(trimmed)) {
      return path.normalize(trimmed);
    }

    return path.resolve(this.workspaceService.getWorkspaceFolder().uri.fsPath, trimmed);
  }

  private async tryReadSchemaFile(resolvedPath: string): Promise<string> {
    try {
      return await fs.readFile(resolvedPath, "utf8");
    } catch {
      return "";
    }
  }

  private parseSchema(sql: string): SchemaDesignerTable[] {
    return parseSchemaTables(sql);
  }

  private normalizeTable(table: SchemaDesignerTable): SchemaDesignerTable {
    const name = table.name.trim();
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)) {
      throw new Error("Tablo adı harfle başlamalı ve yalnızca harf, rakam veya alt çizgi içermelidir.");
    }

    const columns = table.columns
      .map((column) => ({
        name: column.name.trim(),
        sqlType: column.sqlType.trim(),
        isNullable: Boolean(column.isNullable),
        isPrimaryKey: Boolean(column.isPrimaryKey)
      }))
      .filter((column) => column.name.length > 0 && column.sqlType.length > 0);

    if (columns.length === 0) {
      throw new Error("Tabloyu kaydetmeden önce en az bir geçerli sütun ekleyin.");
    }

    const seenColumns = new Set<string>();
    for (const column of columns) {
      if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(column.name)) {
        throw new Error(`'${column.name}' geçerli bir sütun adı değil.`);
      }

      const key = column.name.toLowerCase();
      if (seenColumns.has(key)) {
        throw new Error(`'${column.name}' sütunu birden fazla kez tanımlanmış.`);
      }

      seenColumns.add(key);
    }

    return {
      name,
      columns
    };
  }

  private buildCreateTableScript(table: SchemaDesignerTable): string {
    const lines = table.columns.map((column) => {
      const nullableKeyword = column.isPrimaryKey || !column.isNullable ? " NOT NULL" : " NULL";
      const primaryKeyKeyword = column.isPrimaryKey ? " PRIMARY KEY" : "";
      return `    ${column.name} ${column.sqlType}${nullableKeyword}${primaryKeyKeyword}`;
    });

    return `CREATE TABLE ${table.name} (\n${lines.join(",\n")}\n);`;
  }
}
