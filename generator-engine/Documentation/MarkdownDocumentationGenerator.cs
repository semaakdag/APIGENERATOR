using ApiGenerator.Cli.Analyzers;
using ApiGenerator.Cli.Generators;
using ApiGenerator.Cli.SqlParser;
using System.Text;

namespace ApiGenerator.Cli.Documentation;

public sealed class MarkdownDocumentationGenerator
{
    public Task<IReadOnlyList<PlannedFileEntry>> BuildPlanAsync(SolutionTemplateModel model, DatabaseSchema schema)
    {
        var files = new List<PlannedFileEntry>
        {
            new()
            {
                RelativePath = Path.Combine(model.Layout.ApiBasePath, "docs", "API-DOCUMENTATION.md"),
                Category = "documentation",
                ArtifactKey = "apiProjectDocumentation",
                Content = BuildApiDocumentation(model)
            }
        };

        if (model.Profile.Framework.GeneratePostmanCollection)
        {
            files.Add(new PlannedFileEntry
            {
                RelativePath = Path.Combine(model.Layout.ApiBasePath, "docs", $"{model.SolutionName}.postman_collection.json"),
                Category = "documentation",
                ArtifactKey = "postmanCollection",
                Content = PostmanCollectionBuilder.Build(model)
            });
        }

        if (!model.Layout.ApplyInPlace)
        {
            files.Add(new PlannedFileEntry
            {
                RelativePath = "README.md",
                Category = "documentation",
                ArtifactKey = "readmeDocumentation",
                Content = BuildRootReadme(model.SolutionName, schema, model.Profile)
            });
            files.Add(new PlannedFileEntry
            {
                RelativePath = Path.Combine("docs", "API-DOCUMENTATION.md"),
                Category = "documentation",
                ArtifactKey = "apiDocumentation",
                Content = BuildApiDocumentation(model)
            });
            files.Add(new PlannedFileEntry
            {
                RelativePath = Path.Combine("docs", "ARCHITECTURE.md"),
                Category = "documentation",
                ArtifactKey = "architectureDocumentation",
                Content = BuildArchitectureDocumentation(model.SolutionName, model.Profile)
            });
        }

        return Task.FromResult<IReadOnlyList<PlannedFileEntry>>(files);
    }

    public PlannedFileEntry BuildApiProjectDocxPlan(SolutionTemplateModel model) =>
        new()
        {
            RelativePath = Path.Combine(model.Layout.ApiBasePath, "docs", "API-DOCUMENTATION.docx"),
            Category = "documentation",
            ArtifactKey = DocumentationArtifacts.ApiProjectDocxArtifactKey,
            Content = string.Empty,
            BinaryContent = ApiDocumentationDocxBuilder.Build(model)
        };

    public async Task GenerateStandaloneAsync(string outputPath)
    {
        Directory.CreateDirectory(outputPath);
        await File.WriteAllTextAsync(Path.Combine(outputPath, "README.md"), "# Generated API\n");
        await File.WriteAllTextAsync(Path.Combine(outputPath, "API-DOCUMENTATION.md"), "# API Documentation\n");
        await File.WriteAllTextAsync(Path.Combine(outputPath, "ARCHITECTURE.md"), "# Architecture\n");
    }

    private static string BuildRootReadme(string solutionName, DatabaseSchema schema, StandardProfile profile)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {solutionName}");
        builder.AppendLine();
        builder.AppendLine("## Project Overview");
        builder.AppendLine("Generated with API Generator Platform.");
        builder.AppendLine();
        builder.AppendLine("## Quick Start");
        builder.AppendLine("1. Run `dotnet restore`");
        builder.AppendLine("2. Run `dotnet build`");
        builder.AppendLine($"3. Run `dotnet run --project src/{solutionName}.Api/{solutionName}.Api.csproj`");
        builder.AppendLine();
        builder.AppendLine("## Solution Structure");
        if (profile.Framework.ProjectLayout.Equals("single-api", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine("- `src/` contains a single API project");
            builder.AppendLine("- `src/{Solution}.Api/Controllers` contains CRUD endpoints");
            builder.AppendLine("- `src/{Solution}.Api/Models` contains database-backed entity classes");
            builder.AppendLine("- `src/{Solution}.Api/Repositories` contains repository interfaces and implementations");
            builder.AppendLine("- `src/{Solution}.Api/Data` contains the DbContext");
            if (profile.Framework.UseAppSettings)
            {
                builder.AppendLine("- `src/{Solution}.Api/appsettings.json` contains the connection string and runtime settings");
            }
        }
        else
        {
            builder.AppendLine("- `src/` contains runtime projects");
            builder.AppendLine("- `tests/` contains unit tests");
        }
        builder.AppendLine("- `docs/` contains generated documentation");
        builder.AppendLine("- `src/{Solution}.Api/docs/API-DOCUMENTATION.md` contains API endpoint inventory");
        if (profile.Framework.GeneratePostmanCollection)
        {
            builder.AppendLine("- `src/{Solution}.Api/docs/{Solution}.postman_collection.json` contains ready-to-run example requests");
        }
        builder.AppendLine();
        builder.AppendLine($"Entities: {schema.Tables.Count}");
        return builder.ToString();
    }

    private static string BuildApiDocumentation(SolutionTemplateModel model)
    {
        // Endpoints added later with add-endpoint stay documented when the solution is regenerated.
        var recipeEndpoints = ApiGenerator.Cli.Recipes.EndpointStore.Load(model.Layout.OutputRootPath);
        var builder = new StringBuilder();
        builder.AppendLine("# API Documentation");
        builder.AppendLine();
        builder.AppendLine($"Project: `{model.SolutionName}.Api`");
        builder.AppendLine($"API Style: `{(UsesControllerArtifacts(model.Profile) ? "Controller" : "EndpointModule")}`");
        builder.AppendLine();

        foreach (var entity in model.Entities)
        {
            var baseRoute = ResolveBaseRoute(entity, model.Profile);
            var responseType = UsesContractModels(model.Profile) ? entity.ResponseName : entity.EntityTypeName;
            var createRequestType = UsesContractModels(model.Profile) ? entity.CreateRequestName : entity.EntityTypeName;
            var updateRequestType = UsesContractModels(model.Profile) ? entity.UpdateRequestName : entity.EntityTypeName;

            builder.AppendLine($"## {entity.EntityTypeName}");
            if (UsesControllerArtifacts(model.Profile))
            {
                builder.AppendLine($"- Artifact: `{entity.ControllerName}`");
            }
            else
            {
                builder.AppendLine($"- Artifact: `{entity.EndpointModuleName}`");
                builder.AppendLine($"- Registration Method: `Map{entity.EntityName}Endpoints`");
            }
            builder.AppendLine($"- Base Route: `{baseRoute}`");
            builder.AppendLine();
            builder.AppendLine("| Method | HTTP | Route | Request Body | Response |");
            builder.AppendLine("| --- | --- | --- | --- | --- |");
            builder.AppendLine($"| GetAll | GET | `{baseRoute}` | `None` | `IReadOnlyList<{responseType}>` |");
            builder.AppendLine($"| GetById | GET | `{baseRoute}/{entity.KeyRouteTemplate}` | `None` | `{responseType}` |");
            builder.AppendLine($"| Create | POST | `{baseRoute}` | `{createRequestType}` | `{responseType}` |");
            builder.AppendLine($"| Update | PUT | `{baseRoute}/{entity.KeyRouteTemplate}` | `{updateRequestType}` | `{responseType}` |");
            builder.AppendLine($"| Delete | DELETE | `{baseRoute}/{entity.KeyRouteTemplate}` | `None` | `204 No Content` |");
            foreach (var endpoint in recipeEndpoints.Where(endpoint => endpoint.Entity == entity.EntityName))
            {
                builder.AppendLine(endpoint.DocumentationRow);
            }

            builder.AppendLine();

            builder.AppendLine("### Fields");
            builder.AppendLine();
            builder.AppendLine("| Field | Type | Required | Key | Column | Store Type |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var property in entity.Properties)
            {
                var key = property.IsPrimaryKey ? (property.IsGenerated ? "PK (generated)" : "PK") : string.Empty;
                builder.AppendLine($"| `{property.Name}` | `{property.Type}` | {(property.Required ? "Yes" : "No")} | {key} | `{property.ColumnName}` | `{property.StoreType}` |");
            }

            builder.AppendLine();
            var relationships = entity.Properties.Where(property => property.References.Length > 0).ToList();
            if (relationships.Count > 0)
            {
                builder.AppendLine("### Relationships");
                builder.AppendLine();
                foreach (var property in relationships)
                {
                    builder.AppendLine($"- `{property.Name}` references `{property.References}`");
                }

                builder.AppendLine();
            }

            builder.AppendLine("### Code Methods");
            builder.AppendLine();
            builder.AppendLine("- `GetAll`");
            builder.AppendLine("- `GetById`");
            builder.AppendLine("- `Create`");
            builder.AppendLine("- `Update`");
            builder.AppendLine("- `Delete`");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildArchitectureDocumentation(string solutionName, StandardProfile profile)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Architecture");
        builder.AppendLine();
        if (profile.Framework.ProjectLayout.Equals("single-api", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine("## Project");
            builder.AppendLine($"- `{solutionName}.Api`");
            builder.AppendLine();
            builder.AppendLine("## Folder Structure");
            builder.AppendLine("- `Controllers/`");
            builder.AppendLine("- `Models/`");
            builder.AppendLine("- `Repositories/`");
            builder.AppendLine("- `Data/`");
            builder.AppendLine("- `Properties/`");
            if (profile.Framework.UseAppSettings)
            {
                builder.AppendLine("- `appsettings.json`");
            }
            builder.AppendLine();
            builder.AppendLine("## Runtime");
            builder.AppendLine($"- Database provider: `{profile.Framework.DatabaseProvider}`");
            builder.AppendLine($"- Windows authentication: `{profile.Framework.UseWindowsAuthentication}`");
            builder.AppendLine($"- Postman collection: `{profile.Framework.GeneratePostmanCollection}`");
        }
        else
        {
            builder.AppendLine("## Projects");
            builder.AppendLine($"- `{solutionName}.Domain`");
            builder.AppendLine($"- `{solutionName}.Application`");
            builder.AppendLine($"- `{solutionName}.Infrastructure`");
            builder.AppendLine($"- `{solutionName}.Api`");
            builder.AppendLine($"- `{solutionName}.UnitTests`");
            builder.AppendLine();
            builder.AppendLine("## Dependency Graph");
            builder.AppendLine("- Api -> Application");
            builder.AppendLine("- Api -> Infrastructure");
            builder.AppendLine("- Application -> Domain");
            builder.AppendLine("- Infrastructure -> Application");
            builder.AppendLine("- Infrastructure -> Domain");
            builder.AppendLine("- UnitTests -> Application");
        }
        return builder.ToString();
    }

    private static bool UsesControllerArtifacts(StandardProfile profile) =>
        profile.Framework.UseControllers;

    private static bool UsesContractModels(StandardProfile profile) =>
        profile.Framework.UseServiceLayer || profile.Framework.UseContractModels;

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
}
