namespace ApiGenerator.Cli.Analyzers;

public static class ProjectPathResolver
{
    public static string? TryResolveProjectRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);

        if (Directory.Exists(fullPath))
        {
            return TryResolveSolutionRoot(fullPath) ?? fullPath;
        }

        if (File.Exists(fullPath))
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            if (Path.GetExtension(fullPath).Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }

            return TryResolveSolutionRoot(directory) ?? directory;
        }

        return null;
    }

    public static string ResolveProjectRoot(string path)
    {
        var resolved = TryResolveProjectRoot(path);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new DirectoryNotFoundException($"Project path was not found: '{path}'. Select a project folder, .csproj, or .sln.");
        }

        return resolved;
    }

    private static string? TryResolveSolutionRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);

        while (current is not null)
        {
            if (current.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly).Any())
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
