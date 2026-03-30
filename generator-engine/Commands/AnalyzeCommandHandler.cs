using ApiGenerator.Cli.Analyzers;

namespace ApiGenerator.Cli.Commands;

public static class AnalyzeCommandHandler
{
    public static async Task HandleAsync(string? projectPath, RoslynProjectAnalyzer analyzer)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new DirectoryNotFoundException("Project path was not found. Select a project folder, .csproj, or .sln.");
        }

        var resolvedProjectPath = ProjectPathResolver.ResolveProjectRoot(projectPath);
        var profile = await analyzer.LearnAsync(resolvedProjectPath);
        Console.WriteLine(StandardProfileSerializer.Serialize(profile));
    }
}
