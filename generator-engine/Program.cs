using ApiGenerator.Cli.Analyzers;
using ApiGenerator.Cli.Commands;
using ApiGenerator.Cli.Documentation;
using ApiGenerator.Cli.Generators;
using ApiGenerator.Cli.Llm;
using ApiGenerator.Cli.SqlParser;
using ApiGenerator.Cli.Templates;
using System.CommandLine;

try
{
    var root = new RootCommand("API Generator CLI");

    var schemaOption = new Option<string?>("--schema", "Path to SQL schema file.");
    var outputOption = new Option<string?>("--output", () => "GeneratedApi", "Output directory.");
    var projectOption = new Option<string?>("--project", "Project folder, .csproj, or .sln to update in Default Framework mode, or analyze/learn from.");
    var profileOption = new Option<string?>("--profile", "Optional company standard profile.");
    var frameworkOption = new Option<string?>("--framework", "Optional framework preset profile or preset id.");
    var connectionStringOption = new Option<string?>("--connection-string", "Optional database connection string override written into appsettings.");
    var windowsAuthOption = new Option<FeatureSelectionMode?>("--windows-auth", "Feature toggle for Windows Authentication in framework-pack generation: Enable or Disable.");
    var unitTestsOption = new Option<FeatureSelectionMode?>("--unit-tests", "Feature toggle for Unit Tests in framework-pack generation: Enable or Disable.");
    var postmanCollectionOption = new Option<FeatureSelectionMode?>("--postman-collection", "Feature toggle for example Postman collection generation: Enable or Disable.");
    var llmEnabledOption = new Option<bool>("--llm-enabled", "Refine generated files with a stateless LLM pass.");
    var llmUrlOption = new Option<string?>("--llm-url", "OpenAI-compatible base URL or full chat/completions endpoint.");
    var llmModelOption = new Option<string?>("--llm-model", "LLM model or deployment name.");
    var llmTokenOption = new Option<string?>("--llm-token", "LLM token. Prefer API_GENERATOR_LLM_TOKEN environment variable instead.");
    var llmMaxConcurrencyOption = new Option<int?>("--llm-max-concurrency", "Maximum number of parallel LLM refinement requests. Defaults to the built-in safe concurrency.");
    var overwriteModeOption = new Option<OverwriteMode>("--overwrite-mode", () => OverwriteMode.Skip, "Overwrite behavior: Skip, Overwrite, or Fail.");
    var dryRunOption = new Option<bool>("--dry-run", "Plan generation without writing files.");
    var templatesOption = new Option<string?>("--templates", $"Folder with template files that replace built-in templates of the same name. Defaults to '{ScribanTemplateRenderer.WorkspaceTemplatesFolder}' when it exists.");
    var logFormatOption = new Option<LogFormat>("--log-format", () => LogFormat.Text, "Console output format: Text or Json (one JSON object per line).");

    var generateCommand = new Command("generate", "Generate API from schema.");
    generateCommand.AddOption(schemaOption);
    generateCommand.AddOption(outputOption);
    generateCommand.AddOption(projectOption);
    generateCommand.AddOption(profileOption);
    generateCommand.AddOption(frameworkOption);
    generateCommand.AddOption(connectionStringOption);
    generateCommand.AddOption(windowsAuthOption);
    generateCommand.AddOption(unitTestsOption);
    generateCommand.AddOption(postmanCollectionOption);
    generateCommand.AddOption(llmEnabledOption);
    generateCommand.AddOption(llmUrlOption);
    generateCommand.AddOption(llmModelOption);
    generateCommand.AddOption(llmTokenOption);
    generateCommand.AddOption(llmMaxConcurrencyOption);
    generateCommand.AddOption(overwriteModeOption);
    generateCommand.AddOption(dryRunOption);
    generateCommand.AddOption(templatesOption);

    var learnCommand = new Command("learn", "Learn company standard from an existing API project.");
    learnCommand.AddOption(projectOption);
    learnCommand.AddOption(outputOption);

    var analyzeCommand = new Command("analyze", "Analyze architecture and emit a profile preview.");
    analyzeCommand.AddOption(projectOption);

    var addEndpointCommand = new Command("add-endpoint", "Add a recipe endpoint (GetByCode, GetActiveList, Search, BulkInsert, GetByDateRange) to a generated solution.");
    var entityOption = new Option<string?>("--entity", "Entity name, for example Users.");
    var recipeOption = new Option<string?>("--recipe", "Recipe: GetByCode, GetActiveList, Search, BulkInsert or GetByDateRange.");
    var fieldOption = new Option<string?>("--field", "Entity property the recipe filters on.");
    addEndpointCommand.AddOption(projectOption);
    addEndpointCommand.AddOption(entityOption);
    addEndpointCommand.AddOption(recipeOption);
    addEndpointCommand.AddOption(fieldOption);
    addEndpointCommand.AddOption(dryRunOption);

    var entitiesCommand = new Command("entities", "List the entities of a generated solution and their properties (for add-endpoint).");
    entitiesCommand.AddOption(projectOption);

    var documentCommand = new Command("document", "Generate project documentation.");
    documentCommand.AddOption(outputOption);

    var sqlSchemaParser = new SqlSchemaParser();
    var templateRenderer = new ScribanTemplateRenderer(AppContext.BaseDirectory);
    var documentationGenerator = new MarkdownDocumentationGenerator();
    var projectAnalyzer = new RoslynProjectAnalyzer();
    var profileSerializer = new StandardProfileSerializer();
    var frameworkPresetResolver = new FrameworkPresetResolver();
    var llmRefiner = new OpenAiCompatibleLlmRefiner();
    var solutionGenerator = new CleanArchitectureSolutionGenerator(templateRenderer, documentationGenerator, profileSerializer, frameworkPresetResolver, llmRefiner);

    generateCommand.SetHandler(async (context) =>
    {
        await RunHandledAsync(context, logFormatOption, () => GenerateCommandHandler.HandleAsync(
            context.ParseResult.GetValueForOption(schemaOption),
            context.ParseResult.GetValueForOption(outputOption)!,
            context.ParseResult.GetValueForOption(projectOption),
            context.ParseResult.GetValueForOption(profileOption),
            context.ParseResult.GetValueForOption(frameworkOption),
            context.ParseResult.GetValueForOption(connectionStringOption),
            context.ParseResult.GetValueForOption(windowsAuthOption),
            context.ParseResult.GetValueForOption(unitTestsOption),
            context.ParseResult.GetValueForOption(postmanCollectionOption),
            context.ParseResult.GetValueForOption(llmEnabledOption),
            context.ParseResult.GetValueForOption(llmUrlOption),
            context.ParseResult.GetValueForOption(llmModelOption),
            context.ParseResult.GetValueForOption(llmTokenOption),
            context.ParseResult.GetValueForOption(llmMaxConcurrencyOption),
            context.ParseResult.GetValueForOption(overwriteModeOption),
            context.ParseResult.GetValueForOption(dryRunOption),
            context.ParseResult.GetValueForOption(templatesOption),
            templateRenderer,
            sqlSchemaParser,
            solutionGenerator,
            projectAnalyzer));
    });

    learnCommand.SetHandler(async (context) =>
    {
        await RunHandledAsync(context, logFormatOption, () => LearnCommandHandler.HandleAsync(
            context.ParseResult.GetValueForOption(projectOption),
            context.ParseResult.GetValueForOption(outputOption)!,
            projectAnalyzer,
            profileSerializer));
    });

    analyzeCommand.SetHandler(async (context) =>
    {
        await RunHandledAsync(context, logFormatOption, () => AnalyzeCommandHandler.HandleAsync(
            context.ParseResult.GetValueForOption(projectOption),
            projectAnalyzer));
    });

    addEndpointCommand.SetHandler(async (context) =>
    {
        await RunHandledAsync(context, logFormatOption, () => AddEndpointCommandHandler.HandleAsync(
            context.ParseResult.GetValueForOption(projectOption),
            context.ParseResult.GetValueForOption(entityOption),
            context.ParseResult.GetValueForOption(recipeOption),
            context.ParseResult.GetValueForOption(fieldOption),
            context.ParseResult.GetValueForOption(dryRunOption),
            new ApiGenerator.Cli.Recipes.EndpointRecipeGenerator()));
    });

    entitiesCommand.SetHandler(async (context) =>
    {
        await RunHandledAsync(context, logFormatOption, () =>
        {
            var projectPath = context.ParseResult.GetValueForOption(projectOption)
                ?? throw new CliInputException("entities requires --project <generated solution folder>.");
            var entities = ApiGenerator.Cli.Recipes.EntityCatalog.Describe(projectPath);
            CliLog.Info(
                "entities",
                entities.Count == 0
                    ? "No entities found."
                    : string.Join(Environment.NewLine, entities.Select(entity => $"{entity.Name}: {string.Join(", ", entity.Properties.Select(property => $"{property.Name} ({property.Type})"))}")),
                new { entities });
            return Task.CompletedTask;
        });
    });

    documentCommand.SetHandler(async (context) =>
    {
        await RunHandledAsync(context, logFormatOption, () => DocumentCommandHandler.HandleAsync(
            context.ParseResult.GetValueForOption(outputOption)!,
            documentationGenerator));
    });

    root.AddGlobalOption(logFormatOption);
    root.AddCommand(generateCommand);
    root.AddCommand(learnCommand);
    root.AddCommand(analyzeCommand);
    root.AddCommand(addEndpointCommand);
    root.AddCommand(entitiesCommand);
    root.AddCommand(documentCommand);

    return await root.InvokeAsync(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task RunHandledAsync(System.CommandLine.Invocation.InvocationContext context, Option<LogFormat> logFormatOption, Func<Task> handler)
{
    CliLog.Format = context.ParseResult.GetValueForOption(logFormatOption);
    try
    {
        await handler();
    }
    catch (Exception exception)
    {
        var exitCode = CliExitCodes.FromException(exception);
        CliLog.Error("command-failed", exception.Message, new { exitCode, category = CliExitCodes.Category(exitCode) });
        context.ExitCode = exitCode;
    }
}
