namespace ApiGenerator.Cli.Analyzers;

public sealed class FrameworkPresetResolver
{
    public string? Resolve(string? frameworkOrPath)
    {
        if (string.IsNullOrWhiteSpace(frameworkOrPath))
        {
            return null;
        }

        if (File.Exists(frameworkOrPath))
        {
            return Path.GetFullPath(frameworkOrPath);
        }

        var relativeCandidate = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, frameworkOrPath));
        if (File.Exists(relativeCandidate))
        {
            return relativeCandidate;
        }

        var searchPaths = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "profiles", "frameworks", $"{frameworkOrPath}.profile.json"),
            Path.Combine(Environment.CurrentDirectory, "profiles", $"{frameworkOrPath}.profile.json"),
            Path.Combine(Environment.CurrentDirectory, $"{frameworkOrPath}.profile.json")
        };

        return searchPaths.FirstOrDefault(File.Exists);
    }
}
