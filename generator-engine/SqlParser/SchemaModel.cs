namespace ApiGenerator.Cli.SqlParser;

public sealed class DatabaseSchema
{
    public required IReadOnlyList<TableDefinition> Tables { get; init; }
}

public sealed class TableDefinition
{
    public required string Name { get; init; }
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
            Columns = mergedColumns
        };
    }
}
