using ApiGenerator.Cli.Recipes;

namespace ApiGenerator.Cli.Commands;

public static class AddEndpointCommandHandler
{
    public static Task HandleAsync(string? projectPath, string? entity, string? recipe, string? field, bool dryRun, EndpointRecipeGenerator generator)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new CliInputException("add-endpoint requires --project <generated solution folder>.");
        }

        if (string.IsNullOrWhiteSpace(entity))
        {
            throw new CliInputException("add-endpoint requires --entity <EntityName>.");
        }

        var result = generator.Add(new AddEndpointRequest
        {
            ProjectPath = projectPath,
            Entity = entity.Trim(),
            Recipe = recipe ?? string.Empty,
            Field = field,
            DryRun = dryRun
        });

        foreach (var warning in result.Warnings)
        {
            CliLog.Warning("endpoint-warning", warning);
        }

        var endpoint = result.Endpoint;
        var data = new
        {
            endpoint.Entity,
            endpoint.Recipe,
            endpoint.Method,
            endpoint.HttpMethod,
            endpoint.Route,
            result.AlreadyExisted,
            dryRun,
            result.CreatedFiles,
            result.UpdatedFiles
        };

        if (result.AlreadyExisted)
        {
            CliLog.Info("endpoint-exists", $"Endpoint '{endpoint.Method}' already exists on {endpoint.Entity} ({endpoint.HttpMethod} {endpoint.Route}); nothing changed.", data);
            return Task.CompletedTask;
        }

        CliLog.Info(
            "endpoint-added",
            $"{(dryRun ? "Planned" : "Added")} endpoint '{endpoint.Method}' on {endpoint.Entity}: {endpoint.HttpMethod} {endpoint.Route}",
            data);
        foreach (var file in result.CreatedFiles)
        {
            CliLog.Info("file", $"  created: {file}", new { path = file, status = "created" });
        }

        foreach (var file in result.UpdatedFiles)
        {
            CliLog.Info("file", $"  updated: {file}", new { path = file, status = "updated" });
        }

        return Task.CompletedTask;
    }
}
