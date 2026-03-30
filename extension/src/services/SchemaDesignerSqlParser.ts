export interface SchemaDesignerColumn {
  name: string;
  sqlType: string;
  isNullable: boolean;
  isPrimaryKey: boolean;
}

export interface SchemaDesignerTable {
  name: string;
  columns: SchemaDesignerColumn[];
}

export interface ParsedSchemaDesignerTableBlock extends SchemaDesignerTable {
  start: number;
  end: number;
}

export function parseSchemaTableBlocks(sql: string): ParsedSchemaDesignerTableBlock[] {
  const blocks: ParsedSchemaDesignerTableBlock[] = [];
  const normalizedSql = normalizeSql(sql);
  const regex = /CREATE\s+TABLE\s+(?<name>(?:\[[^\]]+\]|"[^"]+"|`[^`]+`|\w+)(?:\s*\.\s*(?:\[[^\]]+\]|"[^"]+"|`[^`]+`|\w+))?)\s*\(/gi;
  let match: RegExpExecArray | null;

  while ((match = regex.exec(normalizedSql)) !== null) {
    const tableName = normalizeIdentifier(match.groups?.name || "");
    if (!tableName) {
      continue;
    }

    const openingParenthesisIndex = normalizedSql.indexOf("(", match.index);
    if (openingParenthesisIndex < 0) {
      continue;
    }

    const closingParenthesisIndex = findMatchingParenthesis(normalizedSql, openingParenthesisIndex);
    const blockEnd = findStatementEnd(normalizedSql, closingParenthesisIndex + 1);
    const body = normalizedSql.slice(openingParenthesisIndex + 1, closingParenthesisIndex);
    const columns = parseColumns(tableName, body);

    if (columns.length === 0) {
      continue;
    }

    blocks.push({
      name: tableName,
      columns,
      start: match.index,
      end: blockEnd
    });
  }

  return blocks;
}

export function parseSchemaTables(sql: string): SchemaDesignerTable[] {
  return parseSchemaTableBlocks(sql).map(({ name, columns }) => ({ name, columns }));
}

function parseColumns(tableName: string, body: string): SchemaDesignerColumn[] {
  const columns: SchemaDesignerColumn[] = [];
  const primaryKeyColumns = new Set<string>();

  for (const line of splitColumns(body)) {
    if (tryCollectPrimaryKeyConstraint(line, primaryKeyColumns)) {
      continue;
    }

    const column = tryParseColumn(line);
    if (!column) {
      continue;
    }

    columns.push(column);
  }

  let finalizedColumns = columns.map((column) => (
    primaryKeyColumns.has(column.name.toLowerCase())
      ? { ...column, isPrimaryKey: true, isNullable: false }
      : column
  ));

  if (finalizedColumns.every((column) => !column.isPrimaryKey)) {
    const conventionalKeyName = `${tableName}Id`.toLowerCase();
    const conventionalKey = finalizedColumns.find((column) => {
      const normalizedName = column.name.toLowerCase();
      return normalizedName === "id" || normalizedName === conventionalKeyName;
    });

    if (conventionalKey) {
      finalizedColumns = finalizedColumns.map((column) => (
        column.name.toLowerCase() === conventionalKey.name.toLowerCase()
          ? { ...column, isPrimaryKey: true, isNullable: false }
          : column
      ));
    }
  }

  return finalizedColumns;
}

function tryParseColumn(line: string): SchemaDesignerColumn | undefined {
  const trimmedLine = line.trim().replace(/,+$/, "");
  if (trimmedLine.length === 0) {
    return undefined;
  }

  if (/^(CONSTRAINT|PRIMARY\s+KEY|FOREIGN\s+KEY|UNIQUE|INDEX|KEY)\b/i.test(trimmedLine)) {
    return undefined;
  }

  const match = trimmedLine.match(/^(?<name>\[[^\]]+\]|"[^"]+"|`[^`]+`|\w+)\s+(?<type>\[[^\]]+\]|\w+)(?:\s*\((?<args>[^)]*)\))?(?<rest>.*)$/i);
  if (!match?.groups) {
    return undefined;
  }

  const name = normalizeIdentifier(match.groups.name);
  const sqlType = normalizeSqlType(match.groups.type, match.groups.args);
  if (!name || !sqlType) {
    return undefined;
  }

  const remainder = match.groups.rest || "";
  return {
    name,
    sqlType,
    isNullable: !/NOT\s+NULL/i.test(remainder) && !/PRIMARY\s+KEY/i.test(remainder),
    isPrimaryKey: /PRIMARY\s+KEY/i.test(remainder)
  };
}

function tryCollectPrimaryKeyConstraint(line: string, primaryKeyColumns: Set<string>): boolean {
  const trimmedLine = line.trim().replace(/,+$/, "");
  if (trimmedLine.length === 0) {
    return false;
  }

  if (!/^(CONSTRAINT|PRIMARY\s+KEY)\b/i.test(trimmedLine)) {
    return false;
  }

  if (!/PRIMARY\s+KEY/i.test(trimmedLine)) {
    return true;
  }

  for (const columnName of extractConstraintColumns(trimmedLine)) {
    primaryKeyColumns.add(columnName.toLowerCase());
  }

  return true;
}

function extractConstraintColumns(line: string): string[] {
  const matches = Array.from(line.matchAll(/\((?<columns>[^)]*)\)/g));
  if (matches.length === 0) {
    return [];
  }

  const rawColumns = matches[0].groups?.columns || "";
  return rawColumns
    .split(",")
    .map((value) => normalizeIdentifier(value))
    .filter((value) => value.length > 0);
}

function splitColumns(body: string): string[] {
  const parts: string[] = [];
  let buffer = "";
  let depth = 0;

  for (const character of body) {
    if (character === "(") {
      depth += 1;
    } else if (character === ")") {
      depth -= 1;
    }

    if (character === "," && depth === 0) {
      const trimmed = buffer.trim();
      if (trimmed.length > 0) {
        parts.push(trimmed);
      }
      buffer = "";
      continue;
    }

    buffer += character;
  }

  if (buffer.trim().length > 0) {
    parts.push(buffer.trim());
  }

  return parts;
}

function findMatchingParenthesis(sql: string, openingParenthesisIndex: number): number {
  let depth = 0;

  for (let index = openingParenthesisIndex; index < sql.length; index += 1) {
    if (sql[index] === "(") {
      depth += 1;
    } else if (sql[index] === ")") {
      depth -= 1;
      if (depth === 0) {
        return index;
      }
    }
  }

  throw new Error("Could not parse CREATE TABLE block.");
}

function findStatementEnd(sql: string, startIndex: number): number {
  let index = startIndex;

  while (index < sql.length && /\s/.test(sql[index])) {
    index += 1;
  }

  if (sql[index] === ";") {
    index += 1;
  }

  while (index < sql.length && /[\s;]/.test(sql[index])) {
    index += 1;
  }

  return index;
}

function normalizeSql(sql: string): string {
  return sql
    .replace(/--.*?$/gm, "")
    .replace(/\r\n/g, "\n")
    .replace(/\r/g, "\n");
}

function normalizeIdentifier(identifier: string): string {
  const trimmed = String(identifier || "").trim();
  if (!trimmed) {
    return "";
  }

  const lastSegment = trimmed.includes(".")
    ? trimmed.split(".").map((value) => value.trim()).filter(Boolean).slice(-1)[0] || trimmed
    : trimmed;

  return lastSegment.replace(/^[\["`]/, "").replace(/[\]"`]$/, "");
}

function normalizeSqlType(typeName: string, typeArguments?: string): string {
  const normalizedType = normalizeIdentifier(typeName);
  const normalizedArguments = String(typeArguments || "").trim();
  return normalizedArguments.length > 0
    ? `${normalizedType}(${normalizedArguments})`
    : normalizedType;
}
