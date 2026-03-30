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

    public async Task<StandardProfile> LoadMergedAsync(StandardProfile baseProfile, params string?[] overlayPaths)
    {
        var rootNode = JsonNode.Parse(Serialize(baseProfile))?.AsObject()
            ?? throw new InvalidOperationException("Base profile could not be serialized.");

        foreach (var overlayPath in overlayPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!File.Exists(overlayPath))
            {
                throw new FileNotFoundException("Profile file was not found.", overlayPath);
            }

            var overlayJson = await File.ReadAllTextAsync(overlayPath!);
            var overlayNode = JsonNode.Parse(overlayJson)?.AsObject();
            if (overlayNode is null)
            {
                continue;
            }

            MergeObjects(rootNode, overlayNode);
        }

        return Deserialize(rootNode.ToJsonString(JsonOptions));
    }

    public async Task<StandardProfile> LoadMergedAsync(StandardProfile baseProfile, StandardProfile? inlineOverlay, params string?[] overlayPaths)
    {
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

            var overlayJson = await File.ReadAllTextAsync(overlayPath!);
            var overlayNode = JsonNode.Parse(overlayJson)?.AsObject();
            if (overlayNode is null)
            {
                continue;
            }

            MergeObjects(rootNode, overlayNode);
        }

        return Deserialize(rootNode.ToJsonString(JsonOptions));
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

            target[key] = sourceValue.DeepClone();
        }
    }
}
