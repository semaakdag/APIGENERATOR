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

    [Fact]
    public async Task Folders_named_after_the_sample_model_become_per_entity_folders()
    {
        // Same reference, but every artifact sits in a folder carrying the model name.
        var reference = Path.Combine(directory, "Acme");
        CopyDirectory(ReferenceProject(), reference);
        void Move(string from, string toFolder)
        {
            var source = Path.Combine(reference, from);
            var target = Path.Combine(reference, toFolder, Path.GetFileName(from));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
        }

        Move("src/Acme.Business/Models/Requests/CustomerCreateRequest.cs", "src/Acme.Business/Models/Requests/Customer");
        Move("src/Acme.Business/Models/Requests/CustomerUpdateRequest.cs", "src/Acme.Business/Models/Requests/Customer");
        Move("src/Acme.Business/Models/Responses/CustomerResponse.cs", "src/Acme.Business/Models/Responses/CustomerModels");
        Move("src/Acme.DataAccess/Repositories/CustomerRepository.cs", "src/Acme.DataAccess/Repositories/CustomerRepositories");
        Move("src/Acme.WebApi/Controllers/V1/CustomerController.cs", "src/Acme.WebApi/Controllers/V1/CustomerOperations");
        Move("src/Acme.Core/Entities/Customer.cs", "src/Acme.Core/Entities/Customers");
        Move("test/Acme.Tests/Services/CustomerServiceTests.cs", "test/Acme.Tests/Services/CustomerTests");

        var output = Path.Combine(directory, "Shop");
        var manifest = await CreateGenerator().GenerateAsync(
            new SqlSchemaParser().Parse("CREATE TABLE Orders (Id INT NOT NULL PRIMARY KEY, Number NVARCHAR(20) NOT NULL);"),
            new GenerationRequest
            {
                OutputPath = output,
                ReferenceProjectPath = reference,
                LearnedProfile = await new RoslynProjectAnalyzer().LearnAsync(reference),
                Features = new GenerationFeatureSelection { UnitTests = FeatureSelectionMode.Enable }
            });
        var paths = manifest.GeneratedFiles.Select(file => file.RelativePath).ToList();

        Assert.DoesNotContain(paths, path => path.Contains("Customer", StringComparison.Ordinal));
        Assert.Contains("src/Shop.Business/Models/Requests/Order/OrderCreateRequest.cs", paths);
        Assert.Contains("src/Shop.Business/Models/Responses/OrderModels/OrderResponse.cs", paths);
        Assert.Contains("src/Shop.DataAccess/Repositories/OrderRepositories/OrderRepository.cs", paths);
        Assert.Contains("src/Shop.WebApi/Controllers/V1/OrderOperations/OrderController.cs", paths);
        Assert.Contains("src/Shop.Core/Entities/Orders/Order.cs", paths);
        Assert.Contains("test/Shop.Tests/Services/OrderTests/OrderServiceTests.cs", paths);
    }

    [Fact]
    public async Task Module_folders_of_the_reference_do_not_leak_into_generated_projects()
    {
        // Reference grouped by module: Sales/Customer and Hr/Employee, nested inside each layer's folders.
        var reference = Path.Combine(directory, "Acme");
        CopyDirectory(ReferenceProject(), reference);
        var moves = new Dictionary<string, string>
        {
            ["src/Acme.Business/Services/Customers"] = "src/Acme.Business/Services/Sales/Customers",
            ["src/Acme.Business/Models/Requests"] = "src/Acme.Business/Models/Sales/Requests",
            ["src/Acme.Business/Models/Responses"] = "src/Acme.Business/Models/Sales/Responses",
            ["src/Acme.Core/Entities"] = "src/Acme.Core/Entities/Sales",
            ["src/Acme.DataAccess/Repositories"] = "src/Acme.DataAccess/Repositories/Sales",
            ["src/Acme.WebApi/Controllers/V1"] = "src/Acme.WebApi/Controllers/V1/Sales",
            ["test/Acme.Tests/Services"] = "test/Acme.Tests/Services/Sales"
        };
        foreach (var (from, to) in moves)
        {
            var temporary = Path.Combine(reference, from + "-moving");
            Directory.Move(Path.Combine(reference, from), temporary);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(reference, to))!);
            Directory.Move(temporary, Path.Combine(reference, to));
        }

        foreach (var file in Directory.EnumerateFiles(reference, "*.cs", SearchOption.AllDirectories).Where(file => Path.GetFileName(file).Contains("Customer")).ToList())
        {
            var target = file.Replace("Customers", "Employees").Replace("Customer", "Employee").Replace($"{Path.DirectorySeparatorChar}Sales", $"{Path.DirectorySeparatorChar}Hr");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, File.ReadAllText(file).Replace("Customers", "Employees").Replace("Customer", "Employee"));
        }

        var controller = Path.Combine(reference, "src", "Acme.WebApi", "Controllers", "V1", "Sales", "CustomerController.cs");
        File.WriteAllText(controller, File.ReadAllText(controller).Replace("namespace Acme.WebApi.Controllers.V1;", "namespace Acme.WebApi.Controllers.V1.Sales;"));

        var output = Path.Combine(directory, "Shop");
        var manifest = await CreateGenerator().GenerateAsync(
            new SqlSchemaParser().Parse("CREATE TABLE Orders (Id INT NOT NULL PRIMARY KEY, Number NVARCHAR(20) NOT NULL);"),
            new GenerationRequest
            {
                OutputPath = output,
                ReferenceProjectPath = reference,
                LearnedProfile = await new RoslynProjectAnalyzer().LearnAsync(reference),
                Features = new GenerationFeatureSelection { UnitTests = FeatureSelectionMode.Enable }
            });
        var paths = manifest.GeneratedFiles.Select(file => file.RelativePath).ToList();

        Assert.DoesNotContain(paths, path => path.Contains("/Sales", StringComparison.Ordinal) || path.Contains("/Hr", StringComparison.Ordinal));
        Assert.Contains("src/Shop.Business/Services/Orders/OrderService.cs", paths);
        Assert.Contains("src/Shop.Business/Models/Requests/OrderCreateRequest.cs", paths);
        Assert.Contains("src/Shop.WebApi/Controllers/V1/OrderController.cs", paths);
        Assert.Contains("test/Shop.Tests/Services/OrderServiceTests.cs", paths);
        Assert.Contains("namespace Shop.WebApi.Controllers.V1;", File.ReadAllText(Path.Combine(output, "src", "Shop.WebApi", "Controllers", "V1", "OrderController.cs")));
        Assert.DoesNotContain(Directory.EnumerateFiles(output, "*.cs", SearchOption.AllDirectories), file =>
            File.ReadAllText(file).Contains("Sales", StringComparison.Ordinal) || File.ReadAllText(file).Contains("Customer", StringComparison.Ordinal));
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                    !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
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
