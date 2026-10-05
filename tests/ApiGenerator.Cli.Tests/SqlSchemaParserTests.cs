using ApiGenerator.Cli.SqlParser;
using Xunit;

namespace ApiGenerator.Cli.Tests;

public sealed class SqlSchemaParserTests
{
    private static DatabaseSchema Parse(string sql) => new SqlSchemaParser().Parse(sql);

    [Fact]
    public void Ignores_tables_inside_block_and_line_comments()
    {
        var schema = Parse("""
            /* CREATE TABLE Hidden (Id INT) */
            -- CREATE TABLE AlsoHidden (Id INT)
            CREATE TABLE Visible (Id INT PRIMARY KEY);
            """);

        Assert.Equal(["Visible"], schema.Tables.Select(table => table.Name));
    }

    [Fact]
    public void Keeps_schema_and_unquotes_bracketed_and_quoted_names()
    {
        var schema = Parse("""
            CREATE TABLE [dbo].[Customer Orders] ([OrderId] INT NOT NULL PRIMARY KEY);
            CREATE TABLE "sales"."order_line" ("order_line_id" INT NOT NULL PRIMARY KEY);
            CREATE TABLE Plain (Id INT PRIMARY KEY);
            """);

        Assert.Collection(
            schema.Tables,
            table => { Assert.Equal("Customer Orders", table.Name); Assert.Equal("dbo", table.Schema); },
            table => { Assert.Equal("order_line", table.Name); Assert.Equal("sales", table.Schema); },
            table => { Assert.Equal("Plain", table.Name); Assert.Null(table.Schema); });
    }

    [Fact]
    public void Reads_composite_primary_key_from_table_constraint()
    {
        var table = Parse("""
            CREATE TABLE CompositeKey (
                TenantId INT NOT NULL,
                ItemId INT NOT NULL,
                Name NVARCHAR(50) NULL,
                CONSTRAINT PK_CompositeKey PRIMARY KEY (TenantId, ItemId)
            );
            """).Tables.Single();

        Assert.Equal(["TenantId", "ItemId"], table.Columns.Where(column => column.IsPrimaryKey).Select(column => column.Name));
        Assert.True(table.Columns.Single(column => column.Name == "Name").IsNullable);
    }

    [Fact]
    public void Captures_identity_and_store_type()
    {
        var columns = Parse("""
            CREATE TABLE Orders (
                OrderId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Amount DECIMAL(18, 4) NULL,
                Code VARCHAR(20) NOT NULL,
                Notes NVARCHAR(MAX) NULL,
                CreatedAt DATETIME NOT NULL
            );
            """).Tables.Single().Columns.ToDictionary(column => column.Name);

        Assert.True(columns["OrderId"].IsIdentity);
        Assert.False(columns["Code"].IsIdentity);
        Assert.Equal("decimal(18,4)", columns["Amount"].StoreType);
        Assert.Equal("varchar(20)", columns["Code"].StoreType);
        Assert.Equal("nvarchar(MAX)", columns["Notes"].StoreType);
        Assert.Equal("datetime", columns["CreatedAt"].StoreType);
    }

    [Fact]
    public void Marks_inline_and_constraint_foreign_keys()
    {
        var columns = Parse("""
            CREATE TABLE Line (
                Id INT PRIMARY KEY,
                OrderId INT NOT NULL REFERENCES Orders(Id),
                ProductId INT NOT NULL,
                CONSTRAINT FK_Line_Product FOREIGN KEY (ProductId) REFERENCES Products(Id)
            );
            """).Tables.Single().Columns.ToDictionary(column => column.Name);

        Assert.True(columns["OrderId"].IsForeignKey);
        Assert.True(columns["ProductId"].IsForeignKey);
        Assert.False(columns["Id"].IsForeignKey);
    }

    [Fact]
    public void Falls_back_to_conventional_id_key()
    {
        var table = Parse("CREATE TABLE Users (Id INT NOT NULL, Name NVARCHAR(10) NOT NULL);").Tables.Single();

        Assert.True(table.Columns.Single(column => column.Name == "Id").IsPrimaryKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("/* CREATE TABLE Ghost (Id INT) */")]
    public void Returns_no_tables_for_input_without_create_table(string sql)
    {
        Assert.Empty(Parse(sql).Tables);
    }
}
