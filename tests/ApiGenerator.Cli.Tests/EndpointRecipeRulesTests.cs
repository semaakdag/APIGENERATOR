using ApiGenerator.Cli.Commands;
using ApiGenerator.Cli.Recipes;
using Xunit;

namespace ApiGenerator.Cli.Tests;

public sealed class EndpointRecipeRulesTests
{
    private static readonly Dictionary<string, string> Properties = new()
    {
        ["Id"] = "int",
        ["Code"] = "string",
        ["IsActive"] = "bool",
        ["IsOpen"] = "bool?",
        ["CreatedAt"] = "DateTime",
        ["Payload"] = "byte[]?"
    };

    [Theory]
    [InlineData("getbycode", RecipeKind.GetByCode)]
    [InlineData("BulkInsert", RecipeKind.BulkInsert)]
    public void Parses_recipe_names_case_insensitively(string name, RecipeKind expected) =>
        Assert.Equal(expected, RecipeRules.Parse(name));

    [Fact]
    public void Rejects_unknown_recipes() =>
        Assert.Contains("Unknown recipe 'Nope'", Assert.Throws<CliInputException>(() => RecipeRules.Parse("Nope")).Message);

    [Fact]
    public void GetActiveList_defaults_to_IsActive_and_accepts_nullable_bool()
    {
        Assert.Equal(("IsActive", "bool"), RecipeRules.ResolveField(RecipeKind.GetActiveList, null, Properties, "Item"));
        Assert.Equal(("IsOpen", "bool?"), RecipeRules.ResolveField(RecipeKind.GetActiveList, "isopen", Properties, "Item"));
    }

    [Theory]
    [InlineData(RecipeKind.Search, "CreatedAt", "needs a string property")]
    [InlineData(RecipeKind.GetByDateRange, "Code", "needs a DateTime, DateOnly or DateTimeOffset property")]
    [InlineData(RecipeKind.GetByCode, "Payload", "needs a scalar value")]
    [InlineData(RecipeKind.GetByCode, "Missing", "has no property 'Missing'")]
    [InlineData(RecipeKind.GetByCode, null, "needs --field")]
    public void Rejects_fields_that_do_not_fit_the_recipe(RecipeKind kind, string? field, string expected) =>
        Assert.Contains(expected, Assert.Throws<CliInputException>(() => RecipeRules.ResolveField(kind, field, Properties, "Item")).Message);

    [Fact]
    public void BulkInsert_needs_no_field() =>
        Assert.Null(RecipeRules.ResolveField(RecipeKind.BulkInsert, null, Properties, "Item"));

    [Theory]
    [InlineData(RecipeKind.GetByCode, "Code", "GetByCode", "by-code/{code}")]
    [InlineData(RecipeKind.GetActiveList, "IsOpen", "GetActiveList", "active")]
    [InlineData(RecipeKind.Search, "ProductName", "SearchByProductName", "search-by-product-name")]
    [InlineData(RecipeKind.GetByDateRange, "CreatedAt", "GetByCreatedAtRange", "by-created-at-range")]
    [InlineData(RecipeKind.BulkInsert, null, "BulkInsert", "bulk")]
    public void Names_methods_and_routes(RecipeKind kind, string? field, string method, string route)
    {
        Assert.Equal(method, RecipeRules.MethodName(kind, field));
        Assert.Equal(route, RecipeRules.RelativeRoute(kind, field, field is null ? string.Empty : RecipeRules.Camel(field)));
    }

    [Fact]
    public void Documentation_row_lists_route_and_types()
    {
        var record = new EndpointRecord
        {
            Entity = "Users",
            Recipe = "Search",
            Method = "SearchByName",
            HttpMethod = "GET",
            Route = "/api/Users/search-by-name?term={term}",
            RequestBody = "None",
            Response = "IReadOnlyList<UsersResponse>"
        };

        Assert.Equal("| SearchByName | GET | `/api/Users/search-by-name?term={term}` | `None` | `IReadOnlyList<UsersResponse>` |", record.DocumentationRow);
    }
}
