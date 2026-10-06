using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiGenerator.Cli.Analyzers;

public sealed class StandardProfileSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string Serialize(StandardProfile profile) =>
        JsonSerializer.Serialize(profile, JsonOptions);

    public static StandardProfile Deserialize(string json) =>
        JsonSerializer.Deserialize<StandardProfile>(json, JsonOptions) ?? StandardProfile.CreateDefault();

    public async Task SaveAsync(StandardProfile profile, string path)
    {
        await File.WriteAllTextAsync(path, Serialize(profile));
    }

    public async Task<StandardProfile> LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return Deserialize(json);
    }

    public Task<StandardProfile> LoadMergedAsync(StandardProfile baseProfile, params string?[] overlayPaths) =>
        LoadMergedAsync(baseProfile, null, overlayPaths);

    public async Task<StandardProfile> LoadMergedAsync(StandardProfile baseProfile, StandardProfile? inlineOverlay, params string?[] overlayPaths) =>
        (await LoadMergedWithWarningsAsync(baseProfile, inlineOverlay, overlayPaths)).Profile;

    public async Task<(StandardProfile Profile, IReadOnlyList<string> Warnings)> LoadMergedWithWarningsAsync(
        StandardProfile baseProfile,
        StandardProfile? inlineOverlay,
        IReadOnlyList<string?> overlayPaths)
    {
        var warnings = new List<string>();
        var rootNode = JsonNode.Parse(Serialize(baseProfile))?.AsObject()
            ?? throw new InvalidOperationException("Base profile could not be serialized.");

        if (inlineOverlay is not null)
        {
            var inlineNode = JsonNode.Parse(Serialize(inlineOverlay))?.AsObject();
            if (inlineNode is not null)
            {
                MergeObjects(rootNode, inlineNode);
            }
        }

        foreach (var overlayPath in overlayPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!File.Exists(overlayPath))
            {
                throw new FileNotFoundException("Profile file was not found.", overlayPath);
            }

            var overlayNode = ParseProfileFile(overlayPath!, await File.ReadAllTextAsync(overlayPath!));
            if (overlayNode is null)
            {
                continue;
            }

            ProfileVersionPolicy.Check(overlayNode, overlayPath!, warnings);
            MergeObjects(rootNode, overlayNode);
        }

        return (Deserialize(rootNode.ToJsonString(JsonOptions)), warnings);
    }

    private static JsonObject? ParseProfileFile(string path, string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            })?.AsObject();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new ApiGenerator.Cli.Commands.CliInputException($"Profile file '{path}' is not a valid JSON object: {exception.Message}");
        }
    }

    private static void MergeObjects(JsonObject target, JsonObject source)
    {
        foreach ((var key, var sourceValue) in source)
        {
            if (sourceValue is null)
            {
                continue;
            }

            if (!target.TryGetPropertyValue(key, out var targetValue) || targetValue is null)
            {
                target[key] = sourceValue.DeepClone();
                continue;
            }

            if (targetValue is JsonObject targetObject && sourceValue is JsonObject sourceObject)
            {
                MergeObjects(targetObject, sourceObject);
                continue;
            }

            // An empty overlay array means "nothing declared", like an empty overlay object;
            // it must not wipe inherited entries (e.g. learned SharedFiles that learned overrides depend on).
            if (sourceValue is JsonArray { Count: 0 } && targetValue is JsonArray { Count: > 0 })
            {
                continue;
            }

            target[key] = sourceValue.DeepClone();
        }
    }
}

/// <summary>
/// Profiles carry a "major.minor" SchemaVersion. This CLI reads major version 2; newer majors are rejected,
/// older or missing versions are accepted with a warning because unknown sections are simply ignored.
/// </summary>
public static class ProfileVersionPolicy
{
    public const int SupportedMajorVersion = 2;

    public static void Check(JsonObject profile, string path, ICollection<string> warnings)
    {
        var rawVersion = profile.TryGetPropertyValue("SchemaVersion", out var node) ? node?.ToString() : null;
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            warnings.Add($"Profile '{Path.GetFileName(path)}' has no SchemaVersion; it is read as version {SupportedMajorVersion}.x.");
            return;
        }

        if (!Version.TryParse(rawVersion.Contains('.') ? rawVersion : rawVersion + ".0", out var version))
        {
            throw new ApiGenerator.Cli.Commands.CliInputException($"Profile '{path}' has an invalid SchemaVersion '{rawVersion}'. Use a major.minor value such as \"{SupportedMajorVersion}.1\".");
        }

        if (version.Major > SupportedMajorVersion)
        {
            throw new ApiGenerator.Cli.Commands.CliInputException(
                $"Profile '{path}' uses SchemaVersion {rawVersion}, but this API Generator supports {SupportedMajorVersion}.x. Update the API Generator to use this profile.");
        }

        if (version.Major < SupportedMajorVersion)
        {
            warnings.Add($"Profile '{Path.GetFileName(path)}' uses the older SchemaVersion {rawVersion}; sections it does not define fall back to defaults.");
        }
    }
}
