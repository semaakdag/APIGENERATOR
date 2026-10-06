using ApiGenerator.Cli.Analyzers;
using ApiGenerator.Cli.Generators;
using ApiGenerator.Cli.Llm;
using ApiGenerator.Cli.SqlParser;

namespace ApiGenerator.Cli.Commands;

public static class GenerateCommandHandler
{
    public static async Task HandleAsync(
        string? schemaPath,
        string outputPath,
        string? projectPath,
        string? profilePath,
        string? frameworkPath,
        string? connectionString,
        FeatureSelectionMode? windowsAuthentication,
        FeatureSelectionMode? unitTests,
        FeatureSelectionMode? postmanCollection,
        bool llmEnabled,
        string? llmUrl,
        string? llmModel,
        string? llmToken,
        int? llmMaxConcurrency,
        OverwriteMode overwriteMode,
        bool dryRun,
        string? templatesDirectory,
        ApiGenerator.Cli.Templates.ScribanTemplateRenderer templateRenderer,
        ApiGenerator.Cli.SqlParser.SqlSchemaParser parser,
        CleanArchitectureSolutionGenerator generator,
        ApiGenerator.Cli.Analyzers.RoslynProjectAnalyzer projectAnalyzer)
    {
        var usesDefaultFramework = string.IsNullOrWhiteSpace(frameworkPath);
        var resolvedProjectPath = ProjectPathResolver.TryResolveProjectRoot(projectPath);

        if (string.IsNullOrWhiteSpace(schemaPath) || !File.Exists(schemaPath))
        {
            throw new FileNotFoundException("Schema file was not found.", schemaPath);
        }

        if (usesDefaultFramework && string.IsNullOrWhiteSpace(projectPath))
        {
            throw new CliInputException("Default Framework mode requires --project so the selected project can be used as a reference when generating a new project.");
        }

        if (!usesDefaultFramework && !string.IsNullOrWhiteSpace(projectPath))
        {
            throw new CliInputException("--project is only supported when no framework pack is selected.");
        }

        if (!string.IsNullOrWhiteSpace(projectPath) && string.IsNullOrWhiteSpace(resolvedProjectPath))
        {
            throw new DirectoryNotFoundException($"Target project path was not found: '{projectPath}'. Select a project folder, .csproj, or .sln.");
        }

        var defaultTemplatesDirectory = Path.Combine(Environment.CurrentDirectory, ApiGenerator.Cli.Templates.ScribanTemplateRenderer.WorkspaceTemplatesFolder);
        var templateWarnings = templateRenderer.UseOverrideDirectory(
            !string.IsNullOrWhiteSpace(templatesDirectory) ? templatesDirectory
            : Directory.Exists(defaultTemplatesDirectory) ? defaultTemplatesDirectory
            : null);

        var sql = await File.ReadAllTextAsync(schemaPath);
        var parsedSchema = parser.Parse(sql);
        if (parsedSchema.Tables.Count == 0)
        {
            throw new CliInputException($"No CREATE TABLE statements were found in schema file '{schemaPath}'.");
        }

        var learnedProfile = usesDefaultFramework && !string.IsNullOrWhiteSpace(resolvedProjectPath)
            ? await projectAnalyzer.LearnAsync(resolvedProjectPath)
            : null;
        var schema = parsedSchema;
        var llmSettings = new LlmExecutionSettings
        {
            Enabled = llmEnabled,
            Url = llmUrl,
            Model = llmModel,
            MaxConcurrency = llmMaxConcurrency,
            Token = !string.IsNullOrWhiteSpace(llmToken)
                ? llmToken
                : Environment.GetEnvironmentVariable("API_GENERATOR_LLM_TOKEN")
        };
        var featureSelection = usesDefaultFramework
            ? null
            : new GenerationFeatureSelection
            {
                WindowsAuthentication = windowsAuthentication ?? FeatureSelectionMode.Inherit,
                UnitTests = unitTests ?? FeatureSelectionMode.Inherit,
                PostmanCollection = postmanCollection ?? FeatureSelectionMode.Inherit
            };

        if (llmSettings.Enabled && !llmSettings.IsConfigured)
        {
            throw new CliInputException("LLM refinement is enabled, but URL, model, or token is missing.");
        }

        var manifest = await generator.GenerateAsync(schema, new GenerationRequest
        {
            OutputPath = outputPath,
            ReferenceProjectPath = resolvedProjectPath,
            LearnedProfile = learnedProfile,
            ProfilePath = profilePath,
            FrameworkPath = frameworkPath,
            ConnectionString = connectionString,
            Features = featureSelection,
            Llm = llmSettings,
            OverwriteMode = overwriteMode,
            DryRun = dryRun,
            Warnings = templateWarnings
        });

        if (CliLog.Format == LogFormat.Json)
        {
            // Tools (the VS Code extension) read the full plan from this event, including dry runs that write no manifest file.
            CliLog.Info("manifest", "Generation manifest.", new { manifest });
        }

        foreach (var warning in manifest.Warnings)
        {
            CliLog.Warning("generation-warning", warning);
        }

        CliLog.Info(
            "generation-completed",
            $"{(dryRun ? "Planned" : "Generated")} solution '{manifest.SolutionName}' in '{manifest.OutputPath}'.",
            new { manifest.SolutionName, manifest.OutputPath, manifest.DryRun });
        CliLog.Info(
            "generation-summary",
            $"Files: {manifest.Summary.TotalFiles}, created: {manifest.Summary.Created}, updated: {manifest.Summary.Updated}, unchanged: {manifest.Summary.Unchanged}, conflicts: {manifest.Summary.Conflicts}",
            manifest.Summary);
        if (manifest.Llm?.Enabled == true)
        {
            CliLog.Info(
                "llm-summary",
                $"LLM: applied={manifest.Llm.Applied}, model={manifest.Llm.Model}, refined={manifest.Llm.RefinedFiles}/{manifest.Llm.TargetFiles}, concurrency={manifest.Llm.EffectiveMaxConcurrency}, configuredConcurrency={manifest.Llm.ConfiguredMaxConcurrency?.ToString() ?? "default"}, errors={manifest.Llm.Errors.Count}");
        }
    }
}
