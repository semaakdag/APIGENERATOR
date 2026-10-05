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
