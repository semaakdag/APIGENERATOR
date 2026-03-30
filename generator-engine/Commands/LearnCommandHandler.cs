using ApiGenerator.Cli.Analyzers;

namespace ApiGenerator.Cli.Commands;

public static class LearnCommandHandler
{
    public static async Task HandleAsync(
        string? projectPath,
        string outputPath,
        RoslynProjectAnalyzer analyzer,
        StandardProfileSerializer serializer)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new DirectoryNotFoundException("Project path was not found. Select a project folder, .csproj, or .sln.");
        }

        var resolvedProjectPath = ProjectPathResolver.ResolveProjectRoot(projectPath);
        var profile = await analyzer.LearnAsync(resolvedProjectPath);
        Directory.CreateDirectory(outputPath);
        var profilePath = Path.Combine(outputPath, "company-standard.profile.json");
        await serializer.SaveAsync(profile, profilePath);
        Console.WriteLine($"Profile saved to '{profilePath}'.");
    }
}
