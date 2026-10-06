using System.Text.Json;
using System.Text.RegularExpressions;
using ApiGenerator.Cli.Commands;

namespace ApiGenerator.Cli.Recipes;

public enum RecipeKind
{
    GetByCode,
    GetActiveList,
    Search,
    BulkInsert,
    GetByDateRange
}

/// <summary>An endpoint added to a generated solution; persisted in api-generator.endpoints.json.</summary>
public sealed class EndpointRecord
{
    public required string Entity { get; init; }
    public required string Recipe { get; init; }
    public string? Field { get; init; }
    public required string Method { get; init; }
    public required string HttpMethod { get; init; }
    public required string Route { get; init; }
    public required string RequestBody { get; init; }
    public required string Response { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];

    public string DocumentationRow =>
        $"| {Method} | {HttpMethod} | `{Route}` | `{RequestBody}` | `{Response}` |";
}

public static class EndpointStore
{
    public const string FileName = "api-generator.endpoints.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<EndpointRecord> Load(string solutionRoot)
    {
        var path = Path.Combine(solutionRoot, FileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<EndpointRecord>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new CliInputException($"'{path}' is not valid: {exception.Message}");
        }
    }

    public static void Save(string solutionRoot, IReadOnlyList<EndpointRecord> records) =>
        File.WriteAllText(Path.Combine(solutionRoot, FileName), JsonSerializer.Serialize(records, JsonOptions) + Environment.NewLine);
}

public static class RecipeRules
{
    private static readonly string[] DateTypes = ["DateTime", "DateOnly", "DateTimeOffset"];

    public static RecipeKind Parse(string? recipe)
    {
        if (Enum.TryParse<RecipeKind>(recipe, ignoreCase: true, out var kind))
        {
            return kind;
        }

        throw new CliInputException($"Unknown recipe '{recipe}'. Use one of: {string.Join(", ", Enum.GetNames<RecipeKind>())}.");
    }

    /// <summary>Picks and validates the field a recipe works on.</summary>
    public static (string Name, string Type)? ResolveField(RecipeKind kind, string? field, IReadOnlyDictionary<string, string> properties, string entity)
    {
        if (kind == RecipeKind.BulkInsert)
        {
            return null;
        }

        var requested = string.IsNullOrWhiteSpace(field) && kind == RecipeKind.GetActiveList ? "IsActive" : field;
        if (string.IsNullOrWhiteSpace(requested))
        {
            throw new CliInputException($"Recipe '{kind}' needs --field <Property> (properties of {entity}: {string.Join(", ", properties.Keys)}).");
        }

        var match = properties.Keys.FirstOrDefault(name => name.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliInputException($"'{entity}' has no property '{requested}'. Available: {string.Join(", ", properties.Keys)}.");
        var type = properties[match].TrimEnd('?');

        var valid = kind switch
        {
            RecipeKind.GetActiveList => type == "bool",
            RecipeKind.Search => type == "string",
            RecipeKind.GetByDateRange => DateTypes.Contains(type),
            RecipeKind.GetByCode => type is not ("byte[]" or "bool"),
            _ => true
        };

        if (!valid)
        {
            var expected = kind switch
            {
                RecipeKind.GetActiveList => "bool",
                RecipeKind.Search => "string",
                RecipeKind.GetByDateRange => "DateTime, DateOnly or DateTimeOffset",
                _ => "a scalar value (not bool or byte[])"
            };
            throw new CliInputException($"Recipe '{kind}' needs a {expected} property, but '{entity}.{match}' is '{properties[match]}'.");
        }

        return (match, properties[match]);
    }

    public static string MethodName(RecipeKind kind, string? field) => kind switch
    {
        RecipeKind.GetByCode => $"GetBy{field}",
        RecipeKind.GetActiveList => "GetActiveList",
        RecipeKind.Search => $"SearchBy{field}",
        RecipeKind.GetByDateRange => $"GetBy{field}Range",
        _ => "BulkInsert"
    };

    public static string Kebab(string value) =>
        Regex.Replace(value, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", "-").ToLowerInvariant();

    public static string Camel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    /// <summary>Route below the entity's base route, using ASP.NET route syntax.</summary>
    public static string RelativeRoute(RecipeKind kind, string? field, string parameter) => kind switch
    {
        RecipeKind.GetByCode => $"by-{Kebab(field!)}/{{{parameter}}}",
        RecipeKind.GetActiveList => "active",
        RecipeKind.Search => $"search-by-{Kebab(field!)}",
        RecipeKind.GetByDateRange => $"by-{Kebab(field!)}-range",
        _ => "bulk"
    };

    public static string QuerySuffix(RecipeKind kind) => kind switch
    {
        RecipeKind.Search => "?term={term}",
        RecipeKind.GetByDateRange => "?from={from}&to={to}",
        _ => string.Empty
    };

    /// <summary>C# literal used as sample input in generated tests.</summary>
    public static string SampleLiteral(string type, int variant = 1) => type.TrimEnd('?') switch
    {
        "int" => $"{variant}",
        "long" => $"{variant}L",
        "short" => $"(short){variant}",
        "byte" => $"(byte){variant}",
        "decimal" => $"{variant}.25m",
        "double" => $"{variant}.5d",
        "float" => $"{variant}.5f",
        "bool" => "true",
        "Guid" => $"Guid.Parse(\"00000000-0000-0000-0000-00000000000{variant}\")",
        "DateTime" => $"new DateTime(2026, 1, {variant})",
        "DateOnly" => $"new DateOnly(2026, 1, {variant})",
        "DateTimeOffset" => $"new DateTimeOffset(2026, 1, {variant}, 0, 0, 0, TimeSpan.Zero)",
        "TimeOnly" => $"new TimeOnly(9, {variant})",
        _ => $"\"sample-{variant}\""
    };
}
