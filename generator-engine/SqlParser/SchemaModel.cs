namespace ApiGenerator.Cli.SqlParser;

public sealed class DatabaseSchema
{
    public required IReadOnlyList<TableDefinition> Tables { get; init; }
    public IReadOnlyList<SchemaDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record SchemaDiagnostic(int Line, string Message)
{
    public override string ToString() => $"Line {Line}: {Message}";
}

public sealed class TableDefinition
{
    public required string Name { get; init; }
    public string? Schema { get; init; }
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }
}

public sealed class ColumnDefinition
{
    public required string Name { get; init; }
    public required string SqlType { get; init; }
    public bool IsNullable { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsForeignKey { get; init; }
    public int? Length { get; init; }
    public string? DefaultValue { get; init; }
    public bool IsIdentity { get; init; }
    public string? TypeArguments { get; init; }
    public string? ReferencedTable { get; init; }
    public string? ReferencedColumn { get; init; }

    public ColumnDefinition AsPrimaryKey() => new()
    {
        Name = Name,
        SqlType = SqlType,
        IsNullable = false,
        IsPrimaryKey = true,
        IsForeignKey = IsForeignKey,
        Length = Length,
        DefaultValue = DefaultValue,
        IsIdentity = IsIdentity,
        TypeArguments = TypeArguments,
        ReferencedTable = ReferencedTable,
        ReferencedColumn = ReferencedColumn
    };

    public string StoreType => string.IsNullOrWhiteSpace(TypeArguments)
        ? SqlType
        : $"{SqlType}({TypeArguments.Replace(" ", string.Empty)})";
}

public static class DatabaseSchemaMerger
{
    public static DatabaseSchema Merge(DatabaseSchema existingSchema, DatabaseSchema incomingSchema)
    {
        var existingTables = existingSchema.Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
        var incomingTables = incomingSchema.Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
        var mergedTables = new List<TableDefinition>();

        foreach (var existingTable in existingSchema.Tables)
        {
            if (incomingTables.TryGetValue(existingTable.Name, out var incomingTable))
            {
                mergedTables.Add(MergeTable(existingTable, incomingTable));
                incomingTables.Remove(existingTable.Name);
                continue;
            }

            mergedTables.Add(existingTable);
        }

        foreach (var incomingTable in incomingSchema.Tables)
        {
            if (!existingTables.ContainsKey(incomingTable.Name))
            {
                mergedTables.Add(incomingTable);
            }
        }

        return new DatabaseSchema
        {
            Tables = mergedTables
        };
    }

    private static TableDefinition MergeTable(TableDefinition existingTable, TableDefinition incomingTable)
    {
        var existingColumns = existingTable.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var incomingColumns = incomingTable.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var mergedColumns = new List<ColumnDefinition>();

        foreach (var existingColumn in existingTable.Columns)
        {
            if (incomingColumns.TryGetValue(existingColumn.Name, out var incomingColumn))
            {
                mergedColumns.Add(incomingColumn);
                incomingColumns.Remove(existingColumn.Name);
                continue;
            }

            mergedColumns.Add(existingColumn);
        }

        foreach (var incomingColumn in incomingTable.Columns)
        {
            if (!existingColumns.ContainsKey(incomingColumn.Name))
            {
                mergedColumns.Add(incomingColumn);
            }
        }

        return new TableDefinition
        {
            Name = incomingTable.Name,
            Schema = incomingTable.Schema ?? existingTable.Schema,
            Columns = mergedColumns
        };
    }
}
