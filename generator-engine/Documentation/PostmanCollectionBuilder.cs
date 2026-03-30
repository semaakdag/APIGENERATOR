using ApiGenerator.Cli.Analyzers;
using ApiGenerator.Cli.Generators;
using System.Text.Json;

namespace ApiGenerator.Cli.Documentation;

internal static class PostmanCollectionBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string Build(SolutionTemplateModel model)
    {
        var collection = new
        {
            info = new
            {
                _postman_id = Guid.NewGuid().ToString(),
                name = $"{model.SolutionName} API",
                schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            },
            variable = new[]
            {
                new { key = "baseUrl", value = $"http://localhost:{model.HttpPort}" }
            },
            item = model.Entities.Select(entity => BuildEntityFolder(entity, model.Profile)).ToArray()
        };

        return JsonSerializer.Serialize(collection, JsonOptions);
    }

    private static object BuildEntityFolder(EntityTemplateModel entity, StandardProfile profile)
    {
        var baseRoute = ResolveBaseRoute(entity, profile);
        var idValue = BuildSampleRouteValue(entity.PrimaryKeyType);

        return new
        {
            name = entity.EntityTypeName,
            item = new object[]
            {
                BuildRequest("GetAll", "GET", baseRoute),
                BuildRequest("GetById", "GET", $"{baseRoute}/{{{{id}}}}", routeVariables: new[] { new { key = "id", value = idValue } }),
                BuildRequest("Create", "POST", baseRoute, BuildBody(entity, includePrimaryKey: false)),
                BuildRequest("Update", "PUT", $"{baseRoute}/{{{{id}}}}", BuildBody(entity, includePrimaryKey: false), new[] { new { key = "id", value = idValue } }),
                BuildRequest("Delete", "DELETE", $"{baseRoute}/{{{{id}}}}", routeVariables: new[] { new { key = "id", value = idValue } })
            }
        };
    }

    private static object BuildRequest(string name, string method, string route, string? rawBody = null, object[]? routeVariables = null)
    {
        var headers = rawBody is null
            ? new object[]
            {
                new { key = "Accept", value = "application/json" }
            }
            : new object[]
            {
                new { key = "Accept", value = "application/json" },
                new { key = "Content-Type", value = "application/json" }
            };

        return new
        {
            name,
            request = new
            {
                method,
                header = headers,
                body = rawBody is null
                    ? null
                    : new
                    {
                        mode = "raw",
                        raw = rawBody,
                        options = new
                        {
                            raw = new
                            {
                                language = "json"
                            }
                        }
                    },
                url = new
                {
                    raw = $"{{{{baseUrl}}}}{route}",
                    host = new[] { "{{baseUrl}}" },
                    path = SplitRoute(route),
                    variable = routeVariables ?? Array.Empty<object>()
                }
            },
            response = Array.Empty<object>()
        };
    }

    private static string[] SplitRoute(string route) =>
        route.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string BuildBody(EntityTemplateModel entity, bool includePrimaryKey)
    {
        var payload = new Dictionary<string, object?>();

        foreach (var property in entity.Properties)
        {
            if (!includePrimaryKey && property.IsPrimaryKey)
            {
                continue;
            }

            payload[property.Name] = BuildSampleValue(property);
        }

        if (payload.Count == 0)
        {
            payload[entity.PrimaryKeyName] = BuildSampleRouteValue(entity.PrimaryKeyType);
        }

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static object BuildSampleValue(EntityPropertyModel property)
    {
        var type = property.Type.TrimEnd('?');
        return type switch
        {
            "int" => 1,
            "long" => 1,
            "decimal" => 19.99m,
            "bool" => true,
            "DateTime" => "2026-03-12T09:00:00Z",
            "DateOnly" => "2026-03-12",
            _ => property.Name.Equals("Email", StringComparison.OrdinalIgnoreCase)
                ? "sample@example.com"
                : property.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)
                    ? $"Sample {property.Name}"
                    : $"sample-{property.Name.ToLowerInvariant()}"
        };
    }

    private static string BuildSampleRouteValue(string primaryKeyType)
    {
        var normalized = primaryKeyType.TrimEnd('?');
        return normalized switch
        {
            "int" => "1",
            "long" => "1",
            _ => "sample-id"
        };
    }

    private static string ResolveBaseRoute(EntityTemplateModel entity, StandardProfile profile)
    {
        if (!UsesControllerArtifacts(profile))
        {
            return $"/api/{entity.EntityName}";
        }

        var controllerSegment = entity.ControllerName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            ? entity.ControllerName[..^"Controller".Length]
            : entity.ControllerName;
        var route = profile.Controller.RouteTemplate.Replace("[controller]", controllerSegment, StringComparison.OrdinalIgnoreCase);
        return route.StartsWith("/", StringComparison.Ordinal) ? route : $"/{route}";
    }

    private static bool UsesControllerArtifacts(StandardProfile profile) =>
        profile.Framework.UseControllers || profile.Framework.ApiStyle.Equals("controller", StringComparison.OrdinalIgnoreCase);
}
