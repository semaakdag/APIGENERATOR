using ApiGenerator.Cli.Analyzers;
using ApiGenerator.Cli.Commands;
using ApiGenerator.Cli.Documentation;
using ApiGenerator.Cli.Generators;
using ApiGenerator.Cli.Llm;
using ApiGenerator.Cli.SqlParser;
using ApiGenerator.Cli.Templates;
using Xunit;

namespace ApiGenerator.Cli.Tests;

public sealed class ReferenceGenerationTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("reference-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static string ReferenceProject()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "tools", "cases", "reference-acme")))
        {
            current = current.Parent;
        }

        return Path.Combine(current?.FullName ?? throw new InvalidOperationException("Repository root not found."), "tools", "cases", "reference-acme");
    }

    private static CleanArchitectureSolutionGenerator CreateGenerator() =>
        new(
            new ScribanTemplateRenderer(AppContext.BaseDirectory),
            new MarkdownDocumentationGenerator(),
            new StandardProfileSerializer(),
            new FrameworkPresetResolver(),
            new OpenAiCompatibleLlmRefiner());

    private async Task<GenerationManifest> GenerateAsync(string output, string sql, OverwriteMode mode, bool dryRun = false) =>
        await CreateGenerator().GenerateAsync(new SqlSchemaParser().Parse(sql), new GenerationRequest
        {
            OutputPath = output,
            ReferenceProjectPath = ReferenceProject(),
            LearnedProfile = await new RoslynProjectAnalyzer().LearnAsync(ReferenceProject()),
            OverwriteMode = mode,
            DryRun = dryRun,
            Features = new GenerationFeatureSelection { UnitTests = FeatureSelectionMode.Enable }
        });

    [Fact]
    public async Task Learner_reads_folders_names_and_patterns_from_the_reference()
    {
        var profile = await new RoslynProjectAnalyzer().LearnAsync(ReferenceProject());

        Assert.Equal("{{ SolutionName }}.DataAccess/Repositories/Interfaces", profile.Folders["repositoryInterfaces"]);
        Assert.Equal("{{ SolutionName }}.Business/Services/{{ EntityPluralName }}", profile.Folders["services"]);
        Assert.Equal("Services", profile.Folders["tests"]);
        Assert.Equal("{Entity}CreateRequest", profile.NamingRules["createRequest"]);
        Assert.Equal("{Entity}UpdateRequest", profile.NamingRules["updateRequest"]);
        Assert.False(profile.Patterns["usesDtos"]);
        Assert.True(profile.Patterns["singularEntityNames"]);
    }

    [Fact]
    public async Task Generation_mirrors_the_reference_structure()
    {
        var output = Path.Combine(directory, "Shop");
        var manifest = await GenerateAsync(output, "CREATE TABLE Orders (Id INT NOT NULL PRIMARY KEY, Number NVARCHAR(20) NOT NULL);", OverwriteMode.Skip);
        var paths = manifest.GeneratedFiles.Select(file => file.RelativePath).ToList();

        Assert.Contains("src/Shop.WebApi/Controllers/V1/OrderController.cs", paths);
        Assert.Contains("src/Shop.Business/Services/Orders/IOrderService.cs", paths);
        Assert.Contains("src/Shop.Business/Models/Requests/OrderCreateRequest.cs", paths);
        Assert.Contains("src/Shop.DataAccess/Repositories/Interfaces/IOrderRepository.cs", paths);
        Assert.Contains("src/Shop.Core/Entities/Order.cs", paths);
        Assert.Contains("test/Shop.Tests/Services/OrderServiceTests.cs", paths);
        Assert.DoesNotContain(paths, path => path.EndsWith("Dto.cs", StringComparison.Ordinal) || path.Contains("Abstractions", StringComparison.Ordinal));
        Assert.Contains("Shop.DataAccess.csproj", File.ReadAllText(Path.Combine(output, "src", "Shop.Business", "Shop.Business.csproj")));
        Assert.DoesNotContain("Shop.Business.csproj", File.ReadAllText(Path.Combine(output, "src", "Shop.DataAccess", "Shop.DataAccess.csproj")));
        Assert.Contains("using Shop.Business.Services.Orders;", File.ReadAllText(Path.Combine(output, "src", "Shop.WebApi", "Controllers", "V1", "OrderController.cs")));
    }

    [Fact]
    public async Task Overwrite_regenerates_from_scratch_and_keeps_user_files()
    {
        var output = Path.Combine(directory, "Shop");
        await GenerateAsync(output, "CREATE TABLE Orders (Id INT NOT NULL PRIMARY KEY, Number NVARCHAR(20) NOT NULL);", OverwriteMode.Skip);
        File.WriteAllText(Path.Combine(output, "NOTES.md"), "mine");
        const string invoices = "CREATE TABLE Invoices (Id INT NOT NULL PRIMARY KEY, Number NVARCHAR(20) NOT NULL);";

        var preview = await GenerateAsync(output, invoices, OverwriteMode.Overwrite, dryRun: true);
        Assert.Contains(preview.GeneratedFiles, file => file.Status == "would-delete" && file.RelativePath.EndsWith("OrderController.cs", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(output, "src", "Shop.WebApi", "Controllers", "V1", "OrderController.cs")));

        var manifest = await GenerateAsync(output, invoices, OverwriteMode.Overwrite);

        Assert.True(manifest.Summary.Deleted > 0);
        Assert.Empty(Directory.EnumerateFiles(output, "Order*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(output, "src", "Shop.Business", "Services", "Orders")));
        Assert.True(File.Exists(Path.Combine(output, "NOTES.md")));
        Assert.True(File.Exists(Path.Combine(output, "src", "Shop.WebApi", "Controllers", "V1", "InvoiceController.cs")));
    }

    [Fact]
    public async Task Skip_mode_keeps_files_of_earlier_generations()
    {
        var output = Path.Combine(directory, "Shop");
        await GenerateAsync(output, "CREATE TABLE Orders (Id INT NOT NULL PRIMARY KEY);", OverwriteMode.Skip);

        var manifest = await GenerateAsync(output, "CREATE TABLE Invoices (Id INT NOT NULL PRIMARY KEY);", OverwriteMode.Skip);

        Assert.Equal(0, manifest.Summary.Deleted);
        Assert.True(File.Exists(Path.Combine(output, "src", "Shop.WebApi", "Controllers", "V1", "OrderController.cs")));
    }

    [Fact]
    public async Task Output_inside_the_reference_project_is_rejected()
    {
        var exception = await Assert.ThrowsAsync<CliInputException>(() =>
            GenerateAsync(Path.Combine(ReferenceProject(), "src"), "CREATE TABLE Orders (Id INT NOT NULL PRIMARY KEY);", OverwriteMode.Overwrite));
        Assert.Contains("inside the reference project", exception.Message);
    }

    [Theory]
    [InlineData("Customers", "Customer")]
    [InlineData("Categories", "Category")]
    [InlineData("Addresses", "Address")]
    [InlineData("Boxes", "Box")]
    [InlineData("Statuses", "Status")]
    [InlineData("Status", "Status")]
    [InlineData("Order", "Order")]
    public void Singularize_handles_common_plurals(string plural, string singular) =>
        Assert.Equal(singular, CleanArchitectureSolutionGenerator.Singularize(plural));
}
