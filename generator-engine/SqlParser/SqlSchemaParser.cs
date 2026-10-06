using System.Text.RegularExpressions;

namespace ApiGenerator.Cli.SqlParser;

public sealed class SqlSchemaParser
{
    public DatabaseSchema Parse(string sql)
    {
        var tables = new List<TableDefinition>();
        var diagnostics = new List<SchemaDiagnostic>();
        var normalizedSql = NormalizeSql(sql);
        var tableMatches = Regex.Matches(
            normalizedSql,
            @"CREATE\s+TABLE\s+(?<name>(?:\[[^\]]+\]|""[^""]+""|\w+)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|\w+))?)\s*\(",
            RegexOptions.IgnoreCase);

        foreach (Match tableMatch in tableMatches)
        {
            var tableName = NormalizeIdentifier(tableMatch.Groups["name"].Value);
            var tableSchema = ExtractSchemaName(tableMatch.Groups["name"].Value);
            var tableLine = LineOf(normalizedSql, tableMatch.Index);
            var bodyStart = tableMatch.Index + tableMatch.Length;
            var body = ExtractTableBody(normalizedSql, bodyStart - 1);
            var columns = new List<ColumnDefinition>();
            var primaryKeyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var foreignKeyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var foreignKeyTargets = new Dictionary<string, (string Table, string? Column)>(StringComparer.OrdinalIgnoreCase);

            foreach (var (line, offset) in SplitColumns(body))
            {
                var lineNumber = LineOf(normalizedSql, bodyStart + offset);
                if (TryCollectTableConstraint(line, primaryKeyColumns, foreignKeyColumns, foreignKeyTargets))
                {
                    continue;
                }

                if (!TryParseColumn(line, out var column))
                {
                    if (!IsIgnorableTableElement(line))
                    {
                        diagnostics.Add(new SchemaDiagnostic(lineNumber, $"Could not parse the definition '{Shorten(line)}' in table '{tableName}'; it was skipped."));
                    }

                    continue;
                }

                if (!KnownSqlTypes.Contains(column.SqlType))
                {
                    diagnostics.Add(new SchemaDiagnostic(lineNumber, $"Type '{column.SqlType}' of column '{tableName}.{column.Name}' is not recognized; it is mapped to string."));
                }

                var inlineReference = ExtractReference(line);
                if (inlineReference is not null)
                {
                    foreignKeyTargets[column.Name] = inlineReference.Value;
                }

                columns.Add(column);
            }

            if (columns.Count == 0)
            {
                diagnostics.Add(new SchemaDiagnostic(tableLine, $"Table '{tableName}' has no parsable columns and was skipped."));
                continue;
            }

            var finalizedColumns = columns
                .Select(column => new ColumnDefinition
                {
                    Name = column.Name,
                    SqlType = column.SqlType,
                    IsNullable = column.IsNullable && !primaryKeyColumns.Contains(column.Name),
                    IsPrimaryKey = column.IsPrimaryKey || primaryKeyColumns.Contains(column.Name),
                    IsForeignKey = column.IsForeignKey || foreignKeyColumns.Contains(column.Name),
                    Length = column.Length,
                    DefaultValue = column.DefaultValue,
                    IsIdentity = column.IsIdentity,
                    TypeArguments = column.TypeArguments,
                    ReferencedTable = foreignKeyTargets.TryGetValue(column.Name, out var target) ? target.Table : null,
                    ReferencedColumn = foreignKeyTargets.TryGetValue(column.Name, out target) ? target.Column : null
                })
                .ToList();

            if (finalizedColumns.All(column => !column.IsPrimaryKey))
            {
                var conventionalKey = finalizedColumns.FirstOrDefault(column =>
                    column.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                    column.Name.Equals($"{tableName}Id", StringComparison.OrdinalIgnoreCase));

                if (conventionalKey is not null)
                {
                    finalizedColumns = finalizedColumns
                        .Select(column => column.Name.Equals(conventionalKey.Name, StringComparison.OrdinalIgnoreCase)
                            ? column.AsPrimaryKey()
                            : column)
                        .ToList();
                }
                else
                {
                    diagnostics.Add(new SchemaDiagnostic(
                        tableLine,
                        $"Table '{tableName}' has no primary key; column '{finalizedColumns[0].Name}' is used as the key for the generated CRUD endpoints."));
                }
            }

            tables.Add(new TableDefinition
            {
                Name = tableName,
                Schema = tableSchema,
                Columns = finalizedColumns
            });
        }

        return new DatabaseSchema
        {
            Tables = tables,
            Diagnostics = diagnostics
        };
    }

    private static readonly HashSet<string> KnownSqlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "integer", "bigint", "smallint", "tinyint", "bit", "float", "real",
        "datetime", "datetime2", "smalldatetime", "datetimeoffset", "date", "time",
        "decimal", "numeric", "money", "smallmoney",
        "nvarchar", "varchar", "nchar", "char", "text", "ntext", "xml", "sysname",
        "uniqueidentifier", "binary", "varbinary", "image", "rowversion", "timestamp"
    };

    private static bool IsIgnorableTableElement(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0 ||
               Regex.IsMatch(trimmed, @"^(CONSTRAINT|PRIMARY\s+KEY|FOREIGN\s+KEY|UNIQUE|INDEX|KEY|CHECK|PERIOD\s+FOR)\b", RegexOptions.IgnoreCase);
    }

    private static (string Table, string? Column)? ExtractReference(string line)
    {
        var match = Regex.Match(
            line,
            @"REFERENCES\s+(?<table>(?:\[[^\]]+\]|""[^""]+""|\w+)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|\w+))?)\s*(?:\(\s*(?<column>[^)]+?)\s*\))?",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var column = match.Groups["column"].Success ? NormalizeIdentifier(match.Groups["column"].Value.Split(',')[0]) : null;
        return (NormalizeIdentifier(match.Groups["table"].Value), string.IsNullOrWhiteSpace(column) ? null : column);
    }

    private static int LineOf(string text, int index) =>
        1 + text.AsSpan(0, Math.Min(index, text.Length)).Count('\n');

    private static string Shorten(string value)
    {
        var singleLine = Regex.Replace(value.Trim(), @"\s+", " ");
        return singleLine.Length <= 60 ? singleLine : singleLine[..57] + "...";
    }

    private static bool TryParseColumn(string line, out ColumnDefinition column)
    {
        column = default!;

        var trimmedLine = line.Trim().TrimEnd(',');
        if (string.IsNullOrWhiteSpace(trimmedLine))
        {
            return false;
        }

        if (trimmedLine.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("INDEX", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("KEY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = Regex.Match(
            trimmedLine,
            @"^(?<name>\[[^\]]+\]|""[^""]+""|`[^`]+`|\w+)\s+(?<type>\[[^\]]+\]|\w+)(?<args>\s*\((?<argsContent>[^)]*)\))?(?<rest>.*)$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return false;
        }

        var columnName = NormalizeIdentifier(match.Groups["name"].Value);
        var sqlType = NormalizeType(match.Groups["type"].Value);
        var typeArguments = match.Groups["argsContent"].Value;
        var remainder = match.Groups["rest"].Value;

        if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(sqlType))
        {
            return false;
        }

        column = new ColumnDefinition
        {
            Name = columnName,
            SqlType = sqlType,
            IsNullable = !remainder.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase)
                && !remainder.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase),
            IsPrimaryKey = remainder.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase),
            IsForeignKey = remainder.Contains("REFERENCES", StringComparison.OrdinalIgnoreCase),
            Length = ExtractLength(typeArguments),
            DefaultValue = ExtractDefaultValue(remainder),
            IsIdentity = Regex.IsMatch(remainder, @"\bIDENTITY\b", RegexOptions.IgnoreCase),
            TypeArguments = string.IsNullOrWhiteSpace(typeArguments) ? null : typeArguments.Trim()
        };

        return true;
    }

    private static bool TryCollectTableConstraint(
        string line,
        ISet<string> primaryKeyColumns,
        ISet<string> foreignKeyColumns,
        IDictionary<string, (string Table, string? Column)> foreignKeyTargets)
    {
        var trimmedLine = line.Trim().TrimEnd(',');
        if (string.IsNullOrWhiteSpace(trimmedLine))
        {
            return false;
        }

        if (trimmedLine.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmedLine.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var columnName in ExtractConstraintColumns(trimmedLine))
                {
                    primaryKeyColumns.Add(columnName);
                }
            }

            if (trimmedLine.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
            {
                CollectForeignKey(trimmedLine, foreignKeyColumns, foreignKeyTargets);
            }

            return true;
        }

        if (trimmedLine.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
        {
            CollectForeignKey(trimmedLine, foreignKeyColumns, foreignKeyTargets);
            return true;
        }

        return false;
    }

    private static void CollectForeignKey(
        string line,
        ISet<string> foreignKeyColumns,
        IDictionary<string, (string Table, string? Column)> foreignKeyTargets)
    {
        var reference = ExtractReference(line);
        foreach (var columnName in ExtractConstraintColumns(line))
        {
            foreignKeyColumns.Add(columnName);
            if (reference is not null)
            {
                foreignKeyTargets[columnName] = reference.Value;
            }
        }
    }

    private static IReadOnlyList<string> ExtractConstraintColumns(string line)
    {
        var columns = new List<string>();
        var matches = Regex.Matches(line, @"\((?<columns>[^)]*)\)");
        if (matches.Count == 0)
        {
            return columns;
        }

        var targetMatch = matches[0];
        if (line.Contains("REFERENCES", StringComparison.OrdinalIgnoreCase) && matches.Count > 1)
        {
            targetMatch = matches[0];
        }

        foreach (var rawColumn in targetMatch.Groups["columns"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeIdentifier(rawColumn);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                columns.Add(normalized);
            }
        }

        return columns;
    }

    private static int? ExtractLength(string typeArguments)
    {
        if (string.IsNullOrWhiteSpace(typeArguments))
        {
            return null;
        }

        var firstToken = typeArguments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return int.TryParse(firstToken, out var length) ? length : null;
    }

    private static string NormalizeType(string sqlType)
    {
        return NormalizeIdentifier(sqlType).ToLowerInvariant();
    }

    private static string? ExtractDefaultValue(string line)
    {
        var match = Regex.Match(line, @"DEFAULT\s+(?<value>\([^)]+\)|[^\s,]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string ExtractTableBody(string sql, int openingParenthesisIndex)
    {
        var depth = 0;
        var start = openingParenthesisIndex + 1;

        for (var index = openingParenthesisIndex; index < sql.Length; index++)
        {
            if (sql[index] == '(')
            {
                depth++;
            }
            else if (sql[index] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return sql[start..index];
                }
            }
        }

        throw new InvalidOperationException("Could not parse CREATE TABLE body.");
    }

    private static IReadOnlyList<(string Text, int Offset)> SplitColumns(string body)
    {
        var parts = new List<(string Text, int Offset)>();
        var depth = 0;
        var inSingleQuote = false;
        var partStart = 0;

        void AddPart(int endExclusive)
        {
            var raw = body[partStart..endExclusive];
            var leading = raw.Length - raw.TrimStart().Length;
            var text = raw.Trim();
            if (text.Length > 0)
            {
                parts.Add((text, partStart + leading));
            }
        }

        for (var index = 0; index < body.Length; index++)
        {
            var character = body[index];
            if (character == '\'')
            {
                inSingleQuote = !inSingleQuote;
            }

            if (inSingleQuote)
            {
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                AddPart(index);
                partStart = index + 1;
            }
        }

        AddPart(body.Length);
        return parts;
    }

    private static string NormalizeSql(string sql)
    {
        // Comments are blanked out but their line breaks are kept so diagnostics report original line numbers.
        var withoutBlockComments = Regex.Replace(
            sql,
            @"/\*.*?\*/",
            match => new string(match.Value.Where(character => character is '\n' or '\r').ToArray()),
            RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"--.*?$", string.Empty, RegexOptions.Multiline)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string? ExtractSchemaName(string qualifiedName)
    {
        var match = Regex.Match(qualifiedName.Trim(), @"^(?<schema>\[[^\]]+\]|""[^""]+""|\w+)\s*\.");
        return match.Success ? match.Groups["schema"].Value.Trim('[', ']', '"') : null;
    }

    private static string NormalizeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return string.Empty;
        }

        var normalized = identifier.Trim();
        if (normalized.Contains('.', StringComparison.Ordinal))
        {
            normalized = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
        }

        return normalized.Trim('[', ']', '"', '`');
    }
}
