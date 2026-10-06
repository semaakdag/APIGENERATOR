using ApiGenerator.Cli.Analyzers;
using ApiGenerator.Cli.Commands;
using ApiGenerator.Cli.Documentation;
using ApiGenerator.Cli.Generators;
using ApiGenerator.Cli.Llm;
using ApiGenerator.Cli.SqlParser;
using ApiGenerator.Cli.Templates;
using Xunit;

namespace ApiGenerator.Cli.Tests;

public sealed class ScribanTemplateRendererTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("renderer-").FullName;
    private readonly ScribanTemplateRenderer renderer = new(AppContext.BaseDirectory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void Built_in_templates_are_valid() =>
        Assert.Empty(renderer.Validate([]));

    [Fact]
    public void Validation_reports_template_name_and_line()
    {
        var errors = renderer.Validate([("profile override 'controller'", "line one\n{{ for x in }}")]);
        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.StartsWith("profile override 'controller' (2:", error));
    }

    [Fact]
    public async Task Renders_inline_templates_with_member_names_as_written()
    {
        var output = await renderer.RenderContentAsync("Hello {{ Name }}", new { Name = "World" });
        Assert.Equal("Hello World", output);
    }

    [Fact]
    public async Task Templates_cannot_include_files()
    {
        var exception = await Assert.ThrowsAsync<CliInputException>(() =>
            renderer.RenderContentAsync("{{ include '/etc/passwd' }}", new { }, "profile override 'controller'"));
        Assert.Contains("Template render error in profile override 'controller' (1:", exception.Message);
    }

    [Fact]
    public async Task Override_folder_replaces_built_in_templates_and_warns_about_unknown_files()
    {
        File.WriteAllText(Path.Combine(directory, "Dto.sbncs"), "// custom {{ DtoName }}");
        File.WriteAllText(Path.Combine(directory, "Unknown.sbncs"), "x");

        var warnings = renderer.UseOverrideDirectory(directory);

        Assert.Contains("Template override 'Unknown.sbncs' does not match a built-in template and is ignored.", warnings);
        Assert.Equal("// custom UsersDto", await renderer.RenderAsync("Dto.sbncs", new { DtoName = "UsersDto" }));
    }

    [Fact]
    public void Missing_override_folder_is_rejected() =>
        Assert.Throws<CliInputException>(() => renderer.UseOverrideDirectory(Path.Combine(directory, "missing")));
}

public sealed class RoslynProjectAnalyzerTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("analyzer-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "profiles", "frameworks")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private async Task<string> GenerateAsync(string preset, string sql, bool unitTests)
    {
        var output = Path.Combine(directory, preset.Replace("-", string.Empty, StringComparison.Ordinal));
        var generator = new CleanArchitectureSolutionGenerator(
            new ScribanTemplateRenderer(AppContext.BaseDirectory),
            new MarkdownDocumentationGenerator(),
            new StandardProfileSerializer(),
            new FrameworkPresetResolver(),
            new OpenAiCompatibleLlmRefiner());
        await generator.GenerateAsync(new SqlSchemaParser().Parse(sql), new GenerationRequest
        {
            OutputPath = output,
            FrameworkPath = Path.Combine(RepositoryRoot(), "profiles", "frameworks", $"{preset}.profile.json"),
            Features = new GenerationFeatureSelection { UnitTests = unitTests ? FeatureSelectionMode.Enable : FeatureSelectionMode.Disable }
        });
        return output;
    }

    private const string Schema = """
        CREATE TABLE Accounts (AccountKey INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
        CREATE TABLE Pairs (LeftId INT NOT NULL, RightId INT NOT NULL, Label NVARCHAR(20) NULL, CONSTRAINT PK_Pairs PRIMARY KEY (LeftId, RightId));
        """;

    [Fact]
    public async Task Single_project_api_with_tests_is_learned_as_single_api()
    {
        var project = await GenerateAsync("minimal-api-swagger", Schema, unitTests: true);

        var profile = await new RoslynProjectAnalyzer().LearnAsync(project);

        Assert.Equal("single-api", profile.Framework.ProjectLayout);
        Assert.True(profile.Framework.IncludeUnitTests);
    }

    [Fact]
    public async Task Layered_api_is_learned_as_layered_with_service_layer()
    {
        var project = await GenerateAsync("aspnet-controller-swagger", Schema, unitTests: false);

        var profile = await new RoslynProjectAnalyzer().LearnAsync(project);

        Assert.Equal("layered", profile.Framework.ProjectLayout);
        Assert.True(profile.Framework.UseControllers);
    }

    [Fact]
    public async Task Learned_controller_template_uses_key_tokens_instead_of_sample_names()
    {
        var project = await GenerateAsync("aspnet-controller-swagger", Schema, unitTests: false);

        var profile = await new RoslynProjectAnalyzer().LearnAsync(project);
        var controller = profile.TemplateOverrides["controller"];

        Assert.Contains("{{ KeyParameters }}", controller);
        Assert.Contains("{{ KeyRouteTemplate }}", controller);
        Assert.DoesNotContain("AccountKey", controller);
        Assert.DoesNotContain("LeftId", controller);
        Assert.DoesNotContain("<param name=\"id\">", controller);
    }
}
