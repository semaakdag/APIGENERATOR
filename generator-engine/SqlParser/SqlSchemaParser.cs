using System.Text.RegularExpressions;

namespace ApiGenerator.Cli.SqlParser;

public sealed class SqlSchemaParser
{
    public DatabaseSchema Parse(string sql)
    {
        var tables = new List<TableDefinition>();
        var normalizedSql = NormalizeSql(sql);
        var tableMatches = Regex.Matches(
            normalizedSql,
            @"CREATE\s+TABLE\s+(?<name>(?:\[[^\]]+\]|\w+)(?:\s*\.\s*(?:\[[^\]]+\]|\w+))?)\s*\(",
            RegexOptions.IgnoreCase);

        foreach (Match tableMatch in tableMatches)
        {
            var tableName = NormalizeIdentifier(tableMatch.Groups["name"].Value);
            var body = ExtractTableBody(normalizedSql, tableMatch.Index + tableMatch.Length - 1);
            var columnLines = SplitColumns(body);
            var columns = new List<ColumnDefinition>();
            var primaryKeyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var foreignKeyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in columnLines)
            {
                if (TryCollectTableConstraint(line, primaryKeyColumns, foreignKeyColumns))
                {
                    continue;
                }

                if (!TryParseColumn(line, out var column))
                {
                    continue;
                }

                columns.Add(column);
            }

            if (columns.Count == 0)
            {
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
                    DefaultValue = column.DefaultValue
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
                            ? new ColumnDefinition
                            {
                                Name = column.Name,
                                SqlType = column.SqlType,
                                IsNullable = false,
                                IsPrimaryKey = true,
                                IsForeignKey = column.IsForeignKey,
                                Length = column.Length,
                                DefaultValue = column.DefaultValue
                            }
                            : column)
                        .ToList();
                }
            }

            tables.Add(new TableDefinition
            {
                Name = tableName,
                Columns = finalizedColumns
            });
        }

        return new DatabaseSchema
        {
            Tables = tables
        };
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
            DefaultValue = ExtractDefaultValue(remainder)
        };

        return true;
    }

    private static bool TryCollectTableConstraint(string line, ISet<string> primaryKeyColumns, ISet<string> foreignKeyColumns)
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
                foreach (var columnName in ExtractConstraintColumns(trimmedLine))
                {
                    foreignKeyColumns.Add(columnName);
                }
            }

            return true;
        }

        if (trimmedLine.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var columnName in ExtractConstraintColumns(trimmedLine))
            {
                foreignKeyColumns.Add(columnName);
            }

            return true;
        }

        return false;
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

    private static IReadOnlyList<string> SplitColumns(string body)
    {
        var parts = new List<string>();
        var buffer = new List<char>();
        var depth = 0;
        var inSingleQuote = false;

        foreach (var character in body)
        {
            if (character == '\'')
            {
                inSingleQuote = !inSingleQuote;
            }

            if (!inSingleQuote)
            {
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }
            }

            if (character == ',' && depth == 0 && !inSingleQuote)
            {
                var part = new string(buffer.ToArray()).Trim();
                if (part.Length > 0)
                {
                    parts.Add(part);
                }

                buffer.Clear();
                continue;
            }

            buffer.Add(character);
        }

        if (buffer.Count > 0)
        {
            var lastPart = new string(buffer.ToArray()).Trim();
            if (lastPart.Length > 0)
            {
                parts.Add(lastPart);
            }
        }

        return parts;
    }

    private static string NormalizeSql(string sql)
    {
        return Regex.Replace(sql, @"--.*?$", string.Empty, RegexOptions.Multiline)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
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
