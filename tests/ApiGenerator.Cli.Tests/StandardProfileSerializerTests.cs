using ApiGenerator.Cli.Analyzers;
using Xunit;

namespace ApiGenerator.Cli.Tests;

public sealed class StandardProfileSerializerTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("profile-merge-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private async Task<StandardProfile> MergeAsync(StandardProfile baseProfile, string overlayJson)
    {
        var overlayPath = Path.Combine(directory, "overlay.profile.json");
        await File.WriteAllTextAsync(overlayPath, overlayJson);
        return await new StandardProfileSerializer().LoadMergedAsync(baseProfile, overlayPath);
    }

    private static StandardProfile ProfileWithSharedFile()
    {
        var profile = StandardProfile.CreateDefault();
        return StandardProfileSerializer.Deserialize(StandardProfileSerializer.Serialize(profile).Replace(
            "\"SharedFiles\": []",
            "\"SharedFiles\": [ { \"RelativePath\": \"src/Shared.cs\", \"Template\": \"// shared\" } ]"));
    }

    [Fact]
    public async Task Empty_overlay_array_keeps_inherited_entries()
    {
        var merged = await MergeAsync(ProfileWithSharedFile(), """{ "SharedFiles": [] }""");

        Assert.Equal("src/Shared.cs", Assert.Single(merged.SharedFiles).RelativePath);
    }

    [Fact]
    public async Task Non_empty_overlay_array_replaces_inherited_entries()
    {
        var merged = await MergeAsync(ProfileWithSharedFile(), """{ "SharedFiles": [ { "RelativePath": "src/Other.cs", "Template": "" } ] }""");

        Assert.Equal("src/Other.cs", Assert.Single(merged.SharedFiles).RelativePath);
    }

    [Fact]
    public async Task Overlay_objects_merge_by_key()
    {
        var baseProfile = StandardProfile.CreateDefault();
        var merged = await MergeAsync(baseProfile, """{ "TemplateOverrides": { "controller": "// custom" } }""");

        Assert.Equal("// custom", merged.TemplateOverrides["controller"]);
        Assert.Equal(baseProfile.Folders.Count, merged.Folders.Count);
    }

    [Fact]
    public async Task Missing_overlay_file_throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new StandardProfileSerializer().LoadMergedAsync(StandardProfile.CreateDefault(), Path.Combine(directory, "missing.json")));
    }
}

public sealed class ProfileVersionPolicyTests
{
    private static List<string> Check(string json)
    {
        var warnings = new List<string>();
        ProfileVersionPolicy.Check(System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject(), "/tmp/x.profile.json", warnings);
        return warnings;
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("2.1")]
    [InlineData("2.9")]
    public void Accepts_supported_major_version(string version) =>
        Assert.Empty(Check($$"""{ "SchemaVersion": "{{version}}" }"""));

    [Fact]
    public void Warns_when_version_is_missing_or_older()
    {
        Assert.Contains("has no SchemaVersion", Assert.Single(Check("{}")));
        Assert.Contains("older SchemaVersion 1.0", Assert.Single(Check("""{ "SchemaVersion": "1.0" }""")));
    }

    [Theory]
    [InlineData("3.0", "supports 2.x")]
    [InlineData("banana", "invalid SchemaVersion")]
    public void Rejects_newer_or_invalid_versions(string version, string expected)
    {
        var exception = Assert.Throws<ApiGenerator.Cli.Commands.CliInputException>(() => Check($$"""{ "SchemaVersion": "{{version}}" }"""));
        Assert.Contains(expected, exception.Message);
    }
}
