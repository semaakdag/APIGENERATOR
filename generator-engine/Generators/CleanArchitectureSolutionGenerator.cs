using ApiGenerator.Cli.Analyzers;
using ApiGenerator.Cli.Documentation;
using ApiGenerator.Cli.Llm;
using ApiGenerator.Cli.SqlParser;
using ApiGenerator.Cli.Templates;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiGenerator.Cli.Generators;

public sealed class CleanArchitectureSolutionGenerator
{
    private readonly ITemplateRenderer templateRenderer;
    private readonly MarkdownDocumentationGenerator documentationGenerator;
    private readonly StandardProfileSerializer profileSerializer;
    private readonly FrameworkPresetResolver frameworkPresetResolver;
    private readonly OpenAiCompatibleLlmRefiner llmRefiner;
    private readonly GenerationFileWriter fileWriter = new();

    public CleanArchitectureSolutionGenerator(
        ITemplateRenderer templateRenderer,
        MarkdownDocumentationGenerator documentationGenerator,
        StandardProfileSerializer profileSerializer,
        FrameworkPresetResolver frameworkPresetResolver,
        OpenAiCompatibleLlmRefiner llmRefiner)
    {
        this.templateRenderer = templateRenderer;
        this.documentationGenerator = documentationGenerator;
        this.profileSerializer = profileSerializer;
        this.frameworkPresetResolver = frameworkPresetResolver;
        this.llmRefiner = llmRefiner;
    }

    public async Task<GenerationManifest> GenerateAsync(DatabaseSchema schema, GenerationRequest request)
    {
        var applyInPlace = ShouldApplyInPlace(request);
        var (profile, resolvedFrameworkPath, profileWarnings) = await LoadProfileAsync(request.ProfilePath, request.FrameworkPath, request.LearnedProfile, request.Features, applyInPlace);
        var templateErrors = templateRenderer.Validate(
            profile.TemplateOverrides.Select(entry => ($"profile override '{entry.Key}'", entry.Value))
                .Concat(profile.SharedFiles.Select(file => ($"shared file '{file.RelativePath}'", file.Template)))
                .Concat(profile.AdditionalProjects.Select(project => ($"additional project '{project.RelativePath}'", project.Template))));
        if (templateErrors.Count > 0)
        {
            throw new ApiGenerator.Cli.Commands.CliInputException(
                "Template validation failed; no files were written:" + Environment.NewLine +
                string.Join(Environment.NewLine, templateErrors.Select(error => $"- {error}")));
        }

        var solutionName = ResolveSolutionName(request, applyInPlace);
        var layout = ResolveLayoutContext(request, profile, solutionName, applyInPlace);
        var connectionString = ResolveConnectionString(profile, solutionName, request.ConnectionString);
        var entityModels = BuildEntityModels(solutionName, schema, profile, layout);
        var solutionModel = new SolutionTemplateModel
        {
            SolutionName = solutionName,
            Entities = entityModels,
            Profile = profile,
            Layout = layout,
            ApiProjectName = layout.ApiProjectName,
            ApiProjectPath = BuildSourcePath(layout, "Api", string.Empty, $"{layout.ApiProjectName}.csproj"),
            ApplicationProjectName = layout.ApplicationProjectName,
            ApplicationProjectPath = BuildSourcePath(layout, "Application", string.Empty, $"{layout.ApplicationProjectName}.csproj"),
            InfrastructureProjectName = layout.InfrastructureProjectName,
            InfrastructureProjectPath = BuildSourcePath(layout, "Infrastructure", string.Empty, $"{layout.InfrastructureProjectName}.csproj"),
            DomainProjectName = layout.DomainProjectName,
            DomainProjectPath = BuildSourcePath(layout, "Domain", string.Empty, $"{layout.DomainProjectName}.csproj"),
            TestsProjectName = layout.TestsProjectName,
            TestsProjectPath = BuildTestsPath(layout, string.Empty, $"{layout.TestsProjectName}.csproj"),
            ApiToApplicationProjectReference = BuildProjectReferencePath(BuildSourcePath(layout, "Api", string.Empty, $"{layout.ApiProjectName}.csproj"), BuildSourcePath(layout, "Application", string.Empty, $"{layout.ApplicationProjectName}.csproj")),
            ApiToInfrastructureProjectReference = BuildProjectReferencePath(BuildSourcePath(layout, "Api", string.Empty, $"{layout.ApiProjectName}.csproj"), BuildSourcePath(layout, "Infrastructure", string.Empty, $"{layout.InfrastructureProjectName}.csproj")),
            ApplicationToDomainProjectReference = BuildProjectReferencePath(BuildSourcePath(layout, "Application", string.Empty, $"{layout.ApplicationProjectName}.csproj"), BuildSourcePath(layout, "Domain", string.Empty, $"{layout.DomainProjectName}.csproj")),
            InfrastructureToApplicationProjectReference = BuildProjectReferencePath(BuildSourcePath(layout, "Infrastructure", string.Empty, $"{layout.InfrastructureProjectName}.csproj"), BuildSourcePath(layout, "Application", string.Empty, $"{layout.ApplicationProjectName}.csproj")),
            InfrastructureToDomainProjectReference = BuildProjectReferencePath(BuildSourcePath(layout, "Infrastructure", string.Empty, $"{layout.InfrastructureProjectName}.csproj"), BuildSourcePath(layout, "Domain", string.Empty, $"{layout.DomainProjectName}.csproj")),
            TestsToApiProjectReference = BuildProjectReferencePath(BuildTestsPath(layout, string.Empty, $"{layout.TestsProjectName}.csproj"), BuildSourcePath(layout, "Api", string.Empty, $"{layout.ApiProjectName}.csproj")),
            TestsToApplicationProjectReference = BuildProjectReferencePath(BuildTestsPath(layout, string.Empty, $"{layout.TestsProjectName}.csproj"), BuildSourcePath(layout, "Application", string.Empty, $"{layout.ApplicationProjectName}.csproj")),
            ConnectionStringName = profile.Framework.ConnectionStringName,
            ConnectionString = connectionString,
            JsonEscapedConnectionString = JsonEncodedText.Encode(connectionString).ToString(),
            ApiDataNamespace = BuildNamespaceFromRelativePath(solutionName, BuildSourcePath(layout, "Api", ResolveFolder(profile, "data", "Data"), "AppDbContext.cs")),
            ApiAdditionalProjectReferences = BuildAdditionalProjectReferences(
                profile,
                solutionName,
                BuildSourcePath(layout, "Api", string.Empty, $"{layout.ApiProjectName}.csproj"),
                BuildSourcePath(layout, "Api", string.Empty, $"{layout.ApiProjectName}.csproj"),
                BuildSourcePath(layout, "Application", string.Empty, $"{layout.ApplicationProjectName}.csproj"),
                BuildSourcePath(layout, "Infrastructure", string.Empty, $"{layout.InfrastructureProjectName}.csproj"),
                BuildSourcePath(layout, "Domain", string.Empty, $"{layout.DomainProjectName}.csproj"),
                BuildTestsPath(layout, string.Empty, $"{layout.TestsProjectName}.csproj")),
            ApplicationAdditionalProjectReferences = BuildAdditionalProjectReferences(
                profile,
                solutionName,
                BuildSourcePath(layout, "Application", string.Empty, $"{layout.ApplicationProjectName}.csproj"),
                BuildSourcePath(layout, "Api", string.Empty, $"{layout.ApiProjectName}.csproj"),
                BuildSourcePath(layout, "Application", string.Empty, $"{layout.ApplicationProjectName}.csproj"),
                BuildSourcePath(layout, "Infrastructure", string.Empty, $"{layout.InfrastructureProjectName}.csproj"),
                BuildSourcePath(layout, "Domain", string.Empty, $"{layout.DomainProjectName}.csproj"),
                BuildTestsPath(layout, string.Empty, $"{layout.TestsProjectName}.csproj")),
            InfrastructureAdditionalProjectReferences = BuildAdditionalProjectReferences(
                profile,
                solutionName,
                BuildSourcePath(layout, "Infrastructure", string.Empty, $"{layout.InfrastructureProjectName}.csproj"),
                BuildSourcePath(layout, "Api", string.Empty, $"{layout.ApiProjectName}.csproj"),
                BuildSourcePath(layout, "Application", string.Empty, $"{layout.ApplicationProjectName}.csproj"),
                BuildSourcePath(layout, "Infrastructure", string.Empty, $"{layout.InfrastructureProjectName}.csproj"),
                BuildSourcePath(layout, "Domain", string.Empty, $"{layout.DomainProjectName}.csproj"),
                BuildTestsPath(layout, string.Empty, $"{layout.TestsProjectName}.csproj")),
            DomainAdditionalProjectReferences = BuildAdditionalProjectReferences(
                profile,
                solutionName,
                BuildSourcePath(layout, "Domain", string.Empty, $"{layout.DomainProjectName}.csproj"),
                BuildSourcePath(layout, "Api", string.Empty, $"{layout.ApiProjectName}.csproj"),
                BuildSourcePath(layout, "Application", string.Empty, $"{layout.ApplicationProjectName}.csproj"),
                BuildSourcePath(layout, "Infrastructure", string.Empty, $"{layout.InfrastructureProjectName}.csproj"),
                BuildSourcePath(layout, "Domain", string.Empty, $"{layout.DomainProjectName}.csproj"),
                BuildTestsPath(layout, string.Empty, $"{layout.TestsProjectName}.csproj")),
            TestAdditionalProjectReferences = BuildAdditionalProjectReferences(
                profile,
                solutionName,
                BuildTestsPath(layout, string.Empty, $"{layout.TestsProjectName}.csproj"),
                BuildSourcePath(layout, "Api", string.Empty, $"{layout.ApiProjectName}.csproj"),
                BuildSourcePath(layout, "Application", string.Empty, $"{layout.ApplicationProjectName}.csproj"),
                BuildSourcePath(layout, "Infrastructure", string.Empty, $"{layout.InfrastructureProjectName}.csproj"),
                BuildSourcePath(layout, "Domain", string.Empty, $"{layout.DomainProjectName}.csproj"),
                BuildTestsPath(layout, string.Empty, $"{layout.TestsProjectName}.csproj")),
            HttpPort = ComputePort(solutionName, 5100, 300),
            HttpsPort = ComputePort(solutionName, 7100, 300)
        };
        var llmPrompt = BuildLlmPrompt(solutionModel);

        var plannedFiles = new List<PlannedFileEntry>();
        plannedFiles.AddRange(await BuildSolutionScaffoldAsync(solutionModel));

        foreach (var model in entityModels)
        {
            plannedFiles.AddRange(await BuildEntityArtifactsAsync(model));
        }

        plannedFiles.AddRange(await BuildFrameworkArtifactsAsync(solutionModel));
        plannedFiles.AddRange(await BuildSharedFilesAsync(solutionModel));
        plannedFiles.Add(documentationGenerator.BuildApiProjectDocxPlan(solutionModel));
        plannedFiles.AddRange(await documentationGenerator.BuildPlanAsync(solutionModel, schema));
        if (!layout.ApplyInPlace)
        {
            plannedFiles.AddRange(BuildProfileArtifacts(solutionModel));
            plannedFiles.AddRange(BuildLlmArtifacts(solutionModel, request.Llm, llmPrompt));
        }

        var llmSummary = BuildDisabledLlmSummary(request.Llm);
        if (request.Llm?.Enabled == true && !request.DryRun)
        {
            var refinement = await llmRefiner.RefineAsync(plannedFiles, profile, llmPrompt, request.Llm);
            plannedFiles = refinement.Files.ToList();
            llmSummary = refinement.Summary;
        }

        var plan = new GenerationPlan
        {
            SolutionName = solutionName,
            OutputPath = layout.OutputRootPath,
            ProfilePath = request.ProfilePath,
            FrameworkPath = resolvedFrameworkPath,
            Llm = llmSummary,
            EntityCount = schema.Tables.Count,
            Files = plannedFiles,
            Warnings = schema.Diagnostics.Select(diagnostic => diagnostic.ToString()).Concat(profileWarnings).Concat(request.Warnings).ToList()
        };

        return await fileWriter.ExecuteAsync(plan, request.OverwriteMode, request.DryRun);
    }

    private async Task<(StandardProfile Profile, string? ResolvedFrameworkPath, IReadOnlyList<string> Warnings)> LoadProfileAsync(
        string? profilePath,
        string? frameworkPath,
        StandardProfile? learnedProfile,
        GenerationFeatureSelection? features,
        bool applyInPlace)
    {
        var resolvedFrameworkPath = frameworkPresetResolver.Resolve(frameworkPath);
        if (!string.IsNullOrWhiteSpace(frameworkPath) && resolvedFrameworkPath is null)
        {
            throw new ApiGenerator.Cli.Commands.CliInputException($"Framework preset '{frameworkPath}' was not found. Pass a preset id from profiles/frameworks or a path to a .profile.json file.");
        }

        var (merged, warnings) = await profileSerializer.LoadMergedWithWarningsAsync(StandardProfile.CreateDefault(), learnedProfile, [resolvedFrameworkPath, profilePath]);
        return (ApplyFeatureSelection(merged, features, applyInPlace), resolvedFrameworkPath, warnings);
    }

    private async Task<IReadOnlyList<PlannedFileEntry>> BuildEntityArtifactsAsync(EntityTemplateModel model)
    {
        var solutionName = model.SolutionName;
        var profile = model.Profile;
        var entityLayer = IsSingleApiLayout(profile) ? "Api" : "Domain";
        var applicationLayer = IsSingleApiLayout(profile) ? "Api" : "Application";
        var infrastructureLayer = IsSingleApiLayout(profile) ? "Api" : "Infrastructure";
        var files = new List<(string RelativePath, string TemplateName, string ArtifactKey, string Category)>
        {
            (BuildSourcePath(model.Layout, entityLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "entities", "Entities"), model.EntityName), $"{model.EntityTypeName}.cs"), "Entity.sbncs", "entity", "source"),
            (BuildRepositoryInterfacePath(model.Layout, profile, model.RepositoryInterfaceName), "RepositoryInterface.sbncs", "repositoryInterface", "source"),
            (BuildSourcePath(model.Layout, infrastructureLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "repositories", "Repositories"), model.EntityName), $"{model.RepositoryImplementationName}.cs"), "RepositoryImplementation.sbncs", "repositoryImplementation", "source")
        };

        if (UsesContractModels(profile))
        {
            files.Add((BuildSourcePath(model.Layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "dtos", "DTOs"), model.EntityName), $"{model.DtoName}.cs"), "Dto.sbncs", "dto", "source"));
            files.Add((BuildSourcePath(model.Layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "requests", "Requests"), model.EntityName), $"{model.CreateRequestName}.cs"), "CreateRequest.sbncs", "createRequest", "source"));
            files.Add((BuildSourcePath(model.Layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "requests", "Requests"), model.EntityName), $"{model.UpdateRequestName}.cs"), "UpdateRequest.sbncs", "updateRequest", "source"));
            files.Add((BuildSourcePath(model.Layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "responses", "Responses"), model.EntityName), $"{model.ResponseName}.cs"), "ResponseModel.sbncs", "response", "source"));
        }

        if (UsesServiceLayer(profile))
        {
            files.Add((BuildSourcePath(model.Layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "services", "Services"), model.EntityName), $"{model.ServiceInterfaceName}.cs"), "ServiceInterface.sbncs", "serviceInterface", "source"));
            files.Add((BuildSourcePath(model.Layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "services", "Services"), model.EntityName), $"{model.ServiceImplementationName}.cs"), "ServiceImplementation.sbncs", "serviceImplementation", "source"));
        }

        if (IncludesUnitTests(profile))
        {
            files.Add((BuildTestsPath(model.Layout, ResolveFolder(profile, "tests", string.Empty), $"{model.TestClassName}.cs"), "UnitTests.sbncs", "unitTests", "test"));
        }

        if (UsesControllerArtifacts(profile))
        {
            files.Add((BuildSourcePath(model.Layout, "Api", ExpandEntityFolderTokens(ResolveFolder(profile, "controllers", "Controllers"), model.EntityName), $"{model.ControllerName}.cs"), "Controller.sbncs", "controller", "source"));
        }
        else
        {
            files.Add((BuildSourcePath(model.Layout, "Api", ExpandEntityFolderTokens(ResolveFolder(profile, "endpoints", "Endpoints"), model.EntityName), $"{model.EndpointModuleName}.cs"), "EndpointModule.sbncs", "endpointModule", "source"));
        }

        var plannedFiles = new List<PlannedFileEntry>();

        foreach (var file in files)
        {
            plannedFiles.Add(await BuildTemplateFileAsync(file.RelativePath, file.TemplateName, file.ArtifactKey, model, file.Category, profile));
        }

        return plannedFiles;
    }

    private async Task<IReadOnlyList<PlannedFileEntry>> BuildSolutionScaffoldAsync(SolutionTemplateModel model)
    {
        var solutionName = model.SolutionName;
        var profile = model.Profile;
        var files = new List<(string RelativePath, string TemplateName, string ArtifactKey, string Category)>();

        if (model.Layout.ApplyInPlace)
        {
            files.Add((BuildSourcePath(model.Layout, "Api", string.Empty, "Program.cs"), "ApiProgram.sbncs", "apiProgram", "source"));

            if (UsesAppSettings(profile))
            {
                files.Add((BuildSourcePath(model.Layout, "Api", string.Empty, "appsettings.json"), "AppSettings.sbnjson", "appSettings", "config"));
                files.Add((BuildSourcePath(model.Layout, "Api", string.Empty, "appsettings.Development.json"), "AppSettingsDevelopment.sbnjson", "appSettingsDevelopment", "config"));
            }

            if (IsSingleApiLayout(profile))
            {
                files.Add((BuildSourcePath(model.Layout, "Api", "Properties", "launchSettings.json"), "LaunchSettings.sbnjson", "launchSettings", "config"));
            }
        }
        else if (IsSingleApiLayout(profile))
        {
            files.Add(BuildPrimaryProjectFile(model, "Api", model.ApiProjectPath, "ApiProject.sbnxml", "apiProject"));
            files.Add((BuildSourcePath(model.Layout, "Api", string.Empty, "Program.cs"), "ApiProgram.sbncs", "apiProgram", "source"));

            if (UsesAppSettings(profile))
            {
                files.Add((BuildSourcePath(model.Layout, "Api", string.Empty, "appsettings.json"), "AppSettings.sbnjson", "appSettings", "config"));
                files.Add((BuildSourcePath(model.Layout, "Api", string.Empty, "appsettings.Development.json"), "AppSettingsDevelopment.sbnjson", "appSettingsDevelopment", "config"));
            }

            files.Add((BuildSourcePath(model.Layout, "Api", "Properties", "launchSettings.json"), "LaunchSettings.sbnjson", "launchSettings", "config"));
        }
        else
        {
            files.Add(BuildPrimaryProjectFile(model, "Domain", model.DomainProjectPath, "DomainProject.sbnxml", "domainProject"));
            files.Add(BuildPrimaryProjectFile(model, "Application", model.ApplicationProjectPath, "ApplicationProject.sbnxml", "applicationProject"));
            files.Add(BuildPrimaryProjectFile(model, "Infrastructure", model.InfrastructureProjectPath, "InfrastructureProject.sbnxml", "infrastructureProject"));
            files.Add(BuildPrimaryProjectFile(model, "Api", model.ApiProjectPath, "ApiProject.sbnxml", "apiProject"));
            files.Add((BuildSourcePath(model.Layout, "Api", string.Empty, "Program.cs"), "ApiProgram.sbncs", "apiProgram", "source"));
        }

        if (!model.Layout.ApplyInPlace && IncludesUnitTests(profile))
        {
            files.Add(BuildPrimaryProjectFile(model, "Tests", model.TestsProjectPath, "TestProject.sbnxml", "testProject"));
        }

        var plannedFiles = new List<PlannedFileEntry>();

        foreach (var file in files)
        {
            plannedFiles.Add(await BuildTemplateFileAsync(file.RelativePath, file.TemplateName, file.ArtifactKey, model, file.Category, profile));
        }

        if (!model.Layout.ApplyInPlace)
        {
            foreach (var additionalProject in model.Profile.AdditionalProjects)
            {
                var normalizedAdditionalPath = NormalizeRelativeTemplatePath(additionalProject.RelativePath, model.SolutionName);
                if (IsPrimaryProjectPath(model, normalizedAdditionalPath) ||
                    plannedFiles.Any(file => file.RelativePath.Equals(normalizedAdditionalPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                plannedFiles.Add(new PlannedFileEntry
                {
                    RelativePath = normalizedAdditionalPath,
                    Category = additionalProject.Category,
                    ArtifactKey = "additionalProject",
                    Content = await templateRenderer.RenderContentAsync(additionalProject.Template, model, $"additional project '{additionalProject.RelativePath}'")
                });
            }
        }

        if (!model.Layout.ApplyInPlace)
        {
            plannedFiles.Add(new PlannedFileEntry
            {
                RelativePath = $"{solutionName}.sln",
                Category = "solution",
                ArtifactKey = "solution",
                Content = BuildSolutionFile(model)
            });
        }

        return plannedFiles;
    }

    private async Task<IReadOnlyList<PlannedFileEntry>> BuildFrameworkArtifactsAsync(SolutionTemplateModel model)
    {
        if (!IsSingleApiLayout(model.Profile))
        {
            return [];
        }

        return
        [
            await BuildTemplateFileAsync(
                BuildSourcePath(model.Layout, "Api", ResolveFolder(model.Profile, "data", "Data"), "AppDbContext.cs"),
                "AppDbContext.sbncs",
                "dbContext",
                model,
                "source",
                model.Profile)
        ];
    }

    private async Task<IReadOnlyList<PlannedFileEntry>> BuildSharedFilesAsync(SolutionTemplateModel model)
    {
        if (model.Profile.SharedFiles.Count == 0)
        {
            return [];
        }

        var plannedFiles = new List<PlannedFileEntry>();

        foreach (var sharedFile in model.Profile.SharedFiles)
        {
            plannedFiles.Add(new PlannedFileEntry
            {
                RelativePath = NormalizeRelativeTemplatePath(sharedFile.RelativePath, model.SolutionName),
                Category = sharedFile.Category,
                ArtifactKey = "sharedFiles",
                Content = await templateRenderer.RenderContentAsync(sharedFile.Template, model, $"shared file '{sharedFile.RelativePath}'")
            });
        }

        return plannedFiles;
    }

    private IReadOnlyList<PlannedFileEntry> BuildProfileArtifacts(SolutionTemplateModel model)
    {
        return
        [
            new PlannedFileEntry
            {
                RelativePath = Path.Combine("docs", "STANDARD-PROFILE.json"),
                Category = "documentation",
                ArtifactKey = "standardProfile",
                Content = StandardProfileSerializer.Serialize(model.Profile)
            }
        ];
    }

    private static IReadOnlyList<PlannedFileEntry> BuildLlmArtifacts(
        SolutionTemplateModel model,
        LlmExecutionSettings? llmSettings,
        string llmPrompt)
    {
        if (!model.Profile.Llm.Enabled && llmSettings?.Enabled != true)
        {
            return [];
        }

        return
        [
            new PlannedFileEntry
            {
                RelativePath = Path.Combine("docs", "LLM-STANDARD-PROMPT.md"),
                Category = "documentation",
                ArtifactKey = "llmPrompt",
                Content = llmPrompt
            }
        ];
    }

    private async Task<PlannedFileEntry> BuildTemplateFileAsync(
        string relativePath,
        string templateName,
        string artifactKey,
        object model,
        string category,
        StandardProfile profile)
    {
        var content = await RenderArtifactAsync(templateName, artifactKey, model, profile);
        return new PlannedFileEntry
        {
            RelativePath = relativePath,
            Category = category,
            ArtifactKey = artifactKey,
            Content = content
        };
    }

    private async Task<string> RenderArtifactAsync(string templateName, string artifactKey, object model, StandardProfile profile)
    {
        if (profile.TemplateOverrides.TryGetValue(artifactKey, out var overrideTemplate))
        {
            return await templateRenderer.RenderContentAsync(overrideTemplate, model, $"profile override '{artifactKey}'");
        }

        if (profile.TemplateOverrides.TryGetValue(templateName, out overrideTemplate))
        {
            return await templateRenderer.RenderContentAsync(overrideTemplate, model, $"profile override '{templateName}'");
        }

        if (LooksLikeInlineTemplate(templateName))
        {
            return await templateRenderer.RenderContentAsync(templateName, model, $"inline template for '{artifactKey}'");
        }

        return await templateRenderer.RenderAsync(templateName, model);
    }

    private static bool LooksLikeInlineTemplate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.TrimStart();
        return trimmed.StartsWith("<Project", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("{{", StringComparison.Ordinal);
    }

    private static IReadOnlyList<EntityTemplateModel> BuildEntityModels(string solutionName, DatabaseSchema schema, StandardProfile profile, ProjectLayoutContext layout)
    {
        var recipeEndpoints = ApiGenerator.Cli.Recipes.EndpointStore.Load(layout.OutputRootPath);
        return schema.Tables.Select(table =>
        {
            var baseEntityName = ToPascalCase(table.Name);
            var primaryKey = table.Columns.FirstOrDefault(column => column.IsPrimaryKey) ?? table.Columns.First();
            var keyColumns = table.Columns.Any(column => column.IsPrimaryKey)
                ? table.Columns.Where(column => column.IsPrimaryKey).ToList()
                : [primaryKey];
            var keyPropertyNames = keyColumns.Select(column => ToPascalCase(column.Name)).ToList();
            var keys = keyColumns
                .Select(column =>
                {
                    var propertyName = ToPascalCase(column.Name);
                    var type = MapToClrType(column.SqlType, false);
                    return new EntityKeyModel
                    {
                        PropertyName = propertyName,
                        Type = type,
                        // A single key keeps the conventional "id" parameter so routes stay /api/Entity/{id}.
                        ParameterName = keyColumns.Count == 1 ? "id" : ToCamelCaseIdentifier(propertyName),
                        Sample = BuildSampleValue(type, 1),
                        MissingSample = BuildSampleValue(type, 2)
                    };
                })
                .ToList();
            var entityTypeName = ApplyNamingRule(profile, "entity", baseEntityName, "{Entity}");
            var dtoName = ApplyNamingRule(profile, "dto", baseEntityName, "{Entity}Dto");
            var createRequestName = ApplyNamingRule(profile, "createRequest", baseEntityName, "Create{Entity}Request");
            var updateRequestName = ApplyNamingRule(profile, "updateRequest", baseEntityName, "Update{Entity}Request");
            var responseName = ApplyNamingRule(profile, "response", baseEntityName, "{Entity}Response");
            var serviceInterfaceName = ApplyNamingRule(profile, "serviceInterface", baseEntityName, "I{Entity}Service");
            var serviceImplementationName = ApplyNamingRule(profile, "serviceImplementation", baseEntityName, "{Entity}Service");
            var repositoryInterfaceName = ApplyNamingRule(profile, "repositoryInterface", baseEntityName, "I{Entity}Repository");
            var repositoryImplementationName = ApplyNamingRule(profile, "repositoryImplementation", baseEntityName, "{Entity}Repository");
            var controllerName = ApplyNamingRule(profile, "controller", baseEntityName, "{Entity}Controller");
            var endpointModuleName = ApplyNamingRule(profile, "endpointModule", baseEntityName, "{Entity}Endpoints");
            var testClassName = ApplyNamingRule(profile, "test", baseEntityName, "{Entity}ServiceTests");

            var entityLayer = IsSingleApiLayout(profile) ? "Api" : "Domain";
            var applicationLayer = IsSingleApiLayout(profile) ? "Api" : "Application";
            var infrastructureLayer = IsSingleApiLayout(profile) ? "Api" : "Infrastructure";
            var entityPath = BuildSourcePath(layout, entityLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "entities", "Entities"), baseEntityName), $"{entityTypeName}.cs");
            var dtoPath = BuildSourcePath(layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "dtos", "DTOs"), baseEntityName), $"{dtoName}.cs");
            var createRequestPath = BuildSourcePath(layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "requests", "Requests"), baseEntityName), $"{createRequestName}.cs");
            var updateRequestPath = BuildSourcePath(layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "requests", "Requests"), baseEntityName), $"{updateRequestName}.cs");
            var responsePath = BuildSourcePath(layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "responses", "Responses"), baseEntityName), $"{responseName}.cs");
            var serviceInterfacePath = BuildSourcePath(layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "services", "Services"), baseEntityName), $"{serviceInterfaceName}.cs");
            var serviceImplementationPath = BuildSourcePath(layout, applicationLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "services", "Services"), baseEntityName), $"{serviceImplementationName}.cs");
            var repositoryInterfacePath = BuildRepositoryInterfacePath(layout, profile, repositoryInterfaceName);
            var repositoryImplementationPath = BuildSourcePath(layout, infrastructureLayer, ExpandEntityFolderTokens(ResolveFolder(profile, "repositories", "Repositories"), baseEntityName), $"{repositoryImplementationName}.cs");
            var controllerPath = BuildSourcePath(layout, "Api", ExpandEntityFolderTokens(ResolveFolder(profile, "controllers", "Controllers"), baseEntityName), $"{controllerName}.cs");
            var endpointPath = BuildSourcePath(layout, "Api", ExpandEntityFolderTokens(ResolveFolder(profile, "endpoints", "Endpoints"), baseEntityName), $"{endpointModuleName}.cs");
            var apiDataPath = BuildSourcePath(layout, "Api", ResolveFolder(profile, "data", "Data"), "AppDbContext.cs");

            return new EntityTemplateModel
            {
                SolutionName = solutionName,
                Layout = layout,
                EntityName = baseEntityName,
                PluralName = baseEntityName,
                EntityTypeName = entityTypeName,
                PrimaryKeyName = ToPascalCase(primaryKey.Name),
                PrimaryKeyType = MapToClrType(primaryKey.SqlType, false),
                TableName = table.Name,
                SchemaName = table.Schema,
                HasGeneratedKey = keyPropertyNames.Count == 1 && primaryKey.IsIdentity,
                HasCompositeKey = keys.Count > 1,
                RecipeMethods = recipeEndpoints.Where(endpoint => endpoint.Entity == baseEntityName).Select(endpoint => endpoint.Method).ToList(),
                Keys = keys,
                KeyParameters = string.Join(", ", keys.Select(key => $"{key.Type} {key.ParameterName}")),
                KeyArguments = string.Join(", ", keys.Select(key => key.ParameterName)),
                KeyRouteTemplate = string.Join("/", keys.Select(key => $"{{{key.ParameterName}}}")),
                KeyRouteValues = $"new {{ {string.Join(", ", keys.Select(key => $"{key.ParameterName} = result.{key.PropertyName}"))} }}",
                KeyArgumentsFromRequest = string.Join(", ", keys.Select(key => $"request.{key.PropertyName}")),
                KeyFindValues = $"new object?[] {{ {string.Join(", ", keys.Select(key => key.ParameterName))} }}",
                KeyFindValuesFromEntity = $"new object?[] {{ {string.Join(", ", keys.Select(key => $"entity.{key.PropertyName}"))} }}",
                KeyMatch = string.Join(" && ", keys.Select(key => $"EqualityComparer<{key.Type}>.Default.Equals(item.{key.PropertyName}, {key.ParameterName})")),
                KeyMatchEntity = string.Join(" && ", keys.Select(key => $"EqualityComparer<{key.Type}>.Default.Equals(item.{key.PropertyName}, entity.{key.PropertyName})")),
                KeyInterpolation = string.Join("/", keys.Select(key => $"{{{key.ParameterName}}}")),
                KeySampleArguments = string.Join(", ", keys.Select(key => key.Sample)),
                KeyMissingArguments = string.Join(", ", keys.Select(key => key.MissingSample)),
                PrimaryKeySample = BuildSampleValue(MapToClrType(primaryKey.SqlType, false), 1),
                MissingKeySample = BuildSampleValue(MapToClrType(primaryKey.SqlType, false), 2),
                KeyExpression = keyPropertyNames.Count == 1
                    ? $"item => item.{keyPropertyNames[0]}"
                    : $"item => new {{ {string.Join(", ", keyPropertyNames.Select(name => $"item.{name}"))} }}",
                DtoName = dtoName,
                CreateRequestName = createRequestName,
                UpdateRequestName = updateRequestName,
                ResponseName = responseName,
                ServiceInterfaceName = serviceInterfaceName,
                ServiceImplementationName = serviceImplementationName,
                RepositoryInterfaceName = repositoryInterfaceName,
                RepositoryImplementationName = repositoryImplementationName,
                ControllerName = controllerName,
                EndpointModuleName = endpointModuleName,
                TestClassName = testClassName,
                EntityNamespace = BuildNamespaceFromRelativePath(solutionName, entityPath),
                DtoNamespace = BuildNamespaceFromRelativePath(solutionName, dtoPath),
                CreateRequestNamespace = BuildNamespaceFromRelativePath(solutionName, createRequestPath),
                UpdateRequestNamespace = BuildNamespaceFromRelativePath(solutionName, updateRequestPath),
                ResponseNamespace = BuildNamespaceFromRelativePath(solutionName, responsePath),
                ServiceInterfaceNamespace = BuildNamespaceFromRelativePath(solutionName, serviceInterfacePath),
                ServiceImplementationNamespace = BuildNamespaceFromRelativePath(solutionName, serviceImplementationPath),
                RepositoryInterfaceNamespace = BuildNamespaceFromRelativePath(solutionName, repositoryInterfacePath),
                RepositoryImplementationNamespace = BuildNamespaceFromRelativePath(solutionName, repositoryImplementationPath),
                ControllerNamespace = BuildNamespaceFromRelativePath(solutionName, controllerPath),
                EndpointNamespace = BuildNamespaceFromRelativePath(solutionName, endpointPath),
                ApiDataNamespace = BuildNamespaceFromRelativePath(solutionName, apiDataPath),
                Properties = table.Columns.Select(column => new EntityPropertyModel
                {
                    Name = ToPascalCase(column.Name),
                    Type = MapToClrType(column.SqlType, column.IsNullable && !column.IsPrimaryKey),
                    Required = !column.IsNullable || column.IsPrimaryKey,
                    IsPrimaryKey = column.IsPrimaryKey,
                    ColumnName = column.Name,
                    IsRowVersion = column.SqlType is "rowversion" or "timestamp",
                    StoreType = column.StoreType,
                    IsKeyWithoutIdentity = keyPropertyNames.Contains(ToPascalCase(column.Name)) && !column.IsIdentity,
                    IsGenerated = keyPropertyNames.Count == 1 && keyPropertyNames.Contains(ToPascalCase(column.Name)) && column.IsIdentity,
                    SampleValue = BuildSampleValue(MapToClrType(column.SqlType, false), 1),
                    KeyParameterName = keys.FirstOrDefault(key => key.PropertyName == ToPascalCase(column.Name))?.ParameterName ?? string.Empty,
                    References = column.ReferencedTable is null
                        ? string.Empty
                        : column.ReferencedColumn is null ? column.ReferencedTable : $"{column.ReferencedTable}.{column.ReferencedColumn}"
                }).ToList(),
                Profile = profile
            };
        }).ToList();
    }

    private static string ApplyNamingRule(StandardProfile profile, string ruleName, string entityName, string fallbackPattern)
    {
        var pattern = profile.NamingRules.TryGetValue(ruleName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallbackPattern;
        return pattern.Replace("{Entity}", entityName, StringComparison.Ordinal);
    }

    private static string ResolveFolder(StandardProfile profile, string key, string fallback)
    {
        if (!profile.Folders.TryGetValue(key, out var configuredFolder) || string.IsNullOrWhiteSpace(configuredFolder))
        {
            return fallback;
        }

        var normalized = configuredFolder.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

        while (segments.Count > 0 && ShouldTrimFolderSegment(segments[0], key))
        {
            segments.RemoveAt(0);
        }

        return segments.Count == 0 ? fallback : Path.Combine(segments.ToArray());
    }

    private static string ExpandEntityFolderTokens(string folder, string entityName)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return folder;
        }

        return folder
            .Replace("{{ EntityName }}", entityName, StringComparison.OrdinalIgnoreCase)
            .Replace("{Entity}", entityName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{ EntityTypeName }}", entityName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldTrimFolderSegment(string segment, string key)
    {
        var normalized = segment.Replace("{{ SolutionName }}", string.Empty, StringComparison.OrdinalIgnoreCase).Trim('.', '{', '}', ' ');

        return normalized.Equals("src", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("api", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("application", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("infrastructure", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("domain", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".Api", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".Application", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".Infrastructure", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".Domain", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) ||
               (key.Equals("tests", StringComparison.OrdinalIgnoreCase) && normalized.Equals("unittests", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSourcePath(ProjectLayoutContext layout, string layerName, string folder, string fileName)
    {
        var renderedFolder = folder.Replace("{{ SolutionName }}", layout.SolutionName, StringComparison.OrdinalIgnoreCase);
        var (basePath, relativeFolder) = ResolveArtifactBasePath(layout, layerName, renderedFolder);
        return CombineRelativePath(basePath, relativeFolder, fileName);
    }

    private static string BuildRepositoryInterfacePath(ProjectLayoutContext layout, StandardProfile profile, string fileName)
    {
        if (IsSingleApiLayout(profile))
        {
            return BuildSourcePath(layout, "Api", ResolveFolder(profile, "repositories", "Repositories"), $"{fileName}.cs");
        }

        return BuildSourcePath(layout, "Application", Path.Combine("Abstractions", "Persistence"), $"{fileName}.cs");
    }

    private static string BuildTestsPath(ProjectLayoutContext layout, string folder, string fileName) =>
        CombineRelativePath(layout.TestsBasePath, folder, fileName);

    private static (string BasePath, string Folder) ResolveArtifactBasePath(ProjectLayoutContext layout, string layerName, string folder)
    {
        var normalizedFolder = folder.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var segments = normalizedFolder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0)
        {
            return (GetLayerBasePath(layout, layerName), string.Empty);
        }

        var firstSegment = segments[0];
        if (layout.AdditionalProjectBasePaths.TryGetValue(firstSegment, out var additionalBasePath))
        {
            segments.RemoveAt(0);
            return (additionalBasePath, segments.Count == 0 ? string.Empty : Path.Combine(segments.ToArray()));
        }

        var standardProjectBasePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFileName(layout.ApiBasePath)] = layout.ApiBasePath,
            [Path.GetFileName(layout.ApplicationBasePath)] = layout.ApplicationBasePath,
            [Path.GetFileName(layout.InfrastructureBasePath)] = layout.InfrastructureBasePath,
            [Path.GetFileName(layout.DomainBasePath)] = layout.DomainBasePath,
            [Path.GetFileName(layout.TestsBasePath)] = layout.TestsBasePath
        };

        if (standardProjectBasePaths.TryGetValue(firstSegment, out var standardBasePath))
        {
            segments.RemoveAt(0);
            return (standardBasePath, segments.Count == 0 ? string.Empty : Path.Combine(segments.ToArray()));
        }

        return (GetLayerBasePath(layout, layerName), normalizedFolder);
    }

    private static string NormalizeRelativeTemplatePath(string relativePath, string solutionName)
    {
        var rendered = relativePath.Replace("{{ SolutionName }}", solutionName, StringComparison.OrdinalIgnoreCase);
        return rendered.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static string BuildNamespaceFromRelativePath(string solutionName, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0)
        {
            return solutionName;
        }

        segments.RemoveAt(segments.Count - 1);
        if (segments.Count == 0)
        {
            return solutionName;
        }

        if (segments[0].Equals("src", StringComparison.OrdinalIgnoreCase) ||
            segments[0].Equals("tests", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(0);
        }

        if (segments.Count == 0)
        {
            return solutionName;
        }

        var projectRootIndex = segments.FindIndex(segment =>
            segment.Contains(solutionName, StringComparison.OrdinalIgnoreCase));

        if (projectRootIndex > 0)
        {
            segments = segments.Skip(projectRootIndex).ToList();
        }

        return string.Join(".", segments.Select(segment => segment.Replace('-', '_')));
    }

    private static bool ShouldApplyInPlace(GenerationRequest request)
    {
        _ = request;
        return false;
    }

    private static string ResolveSolutionName(GenerationRequest request, bool applyInPlace)
    {
        if (!applyInPlace)
        {
            return BuildSolutionName(request.OutputPath);
        }

        var profileName = request.LearnedProfile?.ProfileName;
        if (!string.IsNullOrWhiteSpace(profileName) &&
            profileName.EndsWith(" Standard", StringComparison.OrdinalIgnoreCase))
        {
            return BuildSolutionName(profileName[..^" Standard".Length]);
        }

        return BuildSolutionName(request.ReferenceProjectPath!);
    }

    private static ProjectLayoutContext ResolveLayoutContext(GenerationRequest request, StandardProfile profile, string solutionName, bool applyInPlace)
    {
        if (!applyInPlace)
        {
            var additionalProjectBasePaths = BuildAdditionalProjectBasePaths(profile, solutionName);
            var apiProject = ResolvePrimaryProject(profile, solutionName, additionalProjectBasePaths, "api", ["controllers", "endpoints", "data"], $"{solutionName}.Api", Path.Combine("src", $"{solutionName}.Api"));
            var applicationProject = ResolvePrimaryProject(profile, solutionName, additionalProjectBasePaths, "application", ["services", "requests", "responses"], $"{solutionName}.Application", Path.Combine("src", $"{solutionName}.Application"));
            var infrastructureProject = ResolvePrimaryProject(profile, solutionName, additionalProjectBasePaths, "infrastructure", ["repositories"], $"{solutionName}.Infrastructure", Path.Combine("src", $"{solutionName}.Infrastructure"));
            var domainProject = ResolvePrimaryProject(profile, solutionName, additionalProjectBasePaths, "domain", ["entities"], $"{solutionName}.Domain", Path.Combine("src", $"{solutionName}.Domain"));
            var testsProject = ResolvePrimaryProject(profile, solutionName, additionalProjectBasePaths, "tests", ["tests"], $"{solutionName}.UnitTests", Path.Combine("tests", $"{solutionName}.UnitTests"));
            return new ProjectLayoutContext
            {
                SolutionName = solutionName,
                OutputRootPath = Path.GetFullPath(request.OutputPath),
                ApiProjectName = apiProject.ProjectName,
                ApiBasePath = apiProject.BasePath,
                ApplicationProjectName = applicationProject.ProjectName,
                ApplicationBasePath = applicationProject.BasePath,
                InfrastructureProjectName = infrastructureProject.ProjectName,
                InfrastructureBasePath = infrastructureProject.BasePath,
                DomainProjectName = domainProject.ProjectName,
                DomainBasePath = domainProject.BasePath,
                TestsProjectName = testsProject.ProjectName,
                TestsBasePath = testsProject.BasePath,
                AdditionalProjectBasePaths = additionalProjectBasePaths,
                ApplyInPlace = false
            };
        }

        var referenceRoot = Path.GetFullPath(request.ReferenceProjectPath!);
        var workspaceRoot = ResolveWorkspaceRoot(referenceRoot);

        if (IsSingleApiLayout(profile))
        {
            var apiRoot = DetectSingleApiRoot(workspaceRoot, referenceRoot, solutionName);
            return new ProjectLayoutContext
            {
                SolutionName = solutionName,
                OutputRootPath = workspaceRoot,
                ApiProjectName = Path.GetFileName(apiRoot),
                ApiBasePath = ToRelativeBasePath(workspaceRoot, apiRoot),
                ApplicationProjectName = $"{solutionName}.Application",
                ApplicationBasePath = string.Empty,
                InfrastructureProjectName = $"{solutionName}.Infrastructure",
                InfrastructureBasePath = string.Empty,
                DomainProjectName = $"{solutionName}.Domain",
                DomainBasePath = string.Empty,
                TestsProjectName = $"{solutionName}.UnitTests",
                TestsBasePath = DetectTestsProjectBasePath(workspaceRoot, solutionName),
                AdditionalProjectBasePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ApplyInPlace = true
            };
        }

        return new ProjectLayoutContext
        {
            SolutionName = solutionName,
            OutputRootPath = workspaceRoot,
            ApiProjectName = Path.GetFileName(Path.GetFullPath(Path.Combine(workspaceRoot, DetectLayerBasePath(workspaceRoot, solutionName, "Api"))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            ApiBasePath = DetectLayerBasePath(workspaceRoot, solutionName, "Api"),
            ApplicationProjectName = Path.GetFileName(Path.GetFullPath(Path.Combine(workspaceRoot, DetectLayerBasePath(workspaceRoot, solutionName, "Application"))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            ApplicationBasePath = DetectLayerBasePath(workspaceRoot, solutionName, "Application"),
            InfrastructureProjectName = Path.GetFileName(Path.GetFullPath(Path.Combine(workspaceRoot, DetectLayerBasePath(workspaceRoot, solutionName, "Infrastructure"))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            InfrastructureBasePath = DetectLayerBasePath(workspaceRoot, solutionName, "Infrastructure"),
            DomainProjectName = Path.GetFileName(Path.GetFullPath(Path.Combine(workspaceRoot, DetectLayerBasePath(workspaceRoot, solutionName, "Domain"))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            DomainBasePath = DetectLayerBasePath(workspaceRoot, solutionName, "Domain"),
            TestsProjectName = Path.GetFileName(Path.GetFullPath(Path.Combine(workspaceRoot, DetectTestsProjectBasePath(workspaceRoot, solutionName))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            TestsBasePath = DetectTestsProjectBasePath(workspaceRoot, solutionName),
            AdditionalProjectBasePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ApplyInPlace = true
        };
    }

    private static string ResolveWorkspaceRoot(string referenceRoot)
    {
        var current = new DirectoryInfo(referenceRoot);

        while (current is not null)
        {
            if (current.GetFiles("*.sln").Any() ||
                Directory.Exists(Path.Combine(current.FullName, "src")) ||
                Directory.Exists(Path.Combine(current.FullName, "tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return referenceRoot;
    }

    private static string DetectSingleApiRoot(string workspaceRoot, string referenceRoot, string solutionName)
    {
        if (LooksLikeApiProjectRoot(referenceRoot))
        {
            return referenceRoot;
        }

        var preferred = Path.Combine(workspaceRoot, "src", $"{solutionName}.Api");
        if (Directory.Exists(preferred))
        {
            return preferred;
        }

        var candidate = Directory.EnumerateFiles(workspaceRoot, "Program.cs", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .FirstOrDefault(path => LooksLikeApiProjectRoot(path!));

        return candidate ?? referenceRoot;
    }

    private static string DetectLayerBasePath(string workspaceRoot, string solutionName, string layerName)
    {
        var preferred = Path.Combine(workspaceRoot, "src", $"{solutionName}.{layerName}");
        if (Directory.Exists(preferred))
        {
            return ToRelativeBasePath(workspaceRoot, preferred);
        }

        var matchingProject = Directory.EnumerateDirectories(workspaceRoot, $"*.{layerName}", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(matchingProject))
        {
            return ToRelativeBasePath(workspaceRoot, matchingProject);
        }

        if (layerName.Equals("Api", StringComparison.OrdinalIgnoreCase))
        {
            return ToRelativeBasePath(workspaceRoot, DetectSingleApiRoot(workspaceRoot, workspaceRoot, solutionName));
        }

        return Path.Combine("src", $"{solutionName}.{layerName}");
    }

    private static string DetectTestsProjectBasePath(string workspaceRoot, string solutionName)
    {
        var preferred = Path.Combine(workspaceRoot, "tests", $"{solutionName}.UnitTests");
        if (Directory.Exists(preferred))
        {
            return ToRelativeBasePath(workspaceRoot, preferred);
        }

        var matchingProject = Directory.EnumerateDirectories(workspaceRoot, "*.UnitTests", SearchOption.AllDirectories)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(matchingProject)
            ? Path.Combine("tests", $"{solutionName}.UnitTests")
            : ToRelativeBasePath(workspaceRoot, matchingProject);
    }

    private static IReadOnlyDictionary<string, string> BuildAdditionalProjectBasePaths(StandardProfile profile, string solutionName)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var additionalProject in profile.AdditionalProjects)
        {
            var relativeProjectPath = NormalizeRelativeTemplatePath(additionalProject.RelativePath, solutionName);
            var projectName = Path.GetFileNameWithoutExtension(relativeProjectPath);
            var projectBasePath = Path.GetDirectoryName(relativeProjectPath) ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(projectBasePath))
            {
                paths[projectName] = projectBasePath;
            }
        }

        return paths;
    }

    private static (string ProjectName, string BasePath) ResolvePrimaryProject(
        StandardProfile profile,
        string solutionName,
        IReadOnlyDictionary<string, string> additionalProjectBasePaths,
        string projectRoleKey,
        IReadOnlyList<string> folderKeys,
        string defaultProjectName,
        string defaultBasePath)
    {
        if (profile.ProjectBasePaths.TryGetValue(projectRoleKey, out var configuredBasePath) &&
            !string.IsNullOrWhiteSpace(configuredBasePath))
        {
            var normalizedBasePath = NormalizeRelativeTemplatePath(configuredBasePath, solutionName);
            var learnedProjectName = Path.GetFileName(normalizedBasePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(learnedProjectName))
            {
                return (learnedProjectName, normalizedBasePath);
            }
        }

        foreach (var key in folderKeys)
        {
            if (!profile.Folders.TryGetValue(key, out var configuredFolder) || string.IsNullOrWhiteSpace(configuredFolder))
            {
                continue;
            }

            var normalized = configuredFolder.Replace("{{ SolutionName }}", solutionName, StringComparison.OrdinalIgnoreCase)
                .Replace('\\', '/')
                .Trim('/');
            var firstSegment = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstSegment))
            {
                continue;
            }

            if (additionalProjectBasePaths.TryGetValue(firstSegment, out var basePath))
            {
                return (firstSegment, basePath);
            }
        }

        return (defaultProjectName, defaultBasePath);
    }

    private static (string RelativePath, string TemplateName, string ArtifactKey, string Category) BuildPrimaryProjectFile(
        SolutionTemplateModel model,
        string role,
        string relativeProjectPath,
        string fallbackTemplateName,
        string artifactKey)
    {
        _ = model;
        _ = role;
        return (relativeProjectPath, fallbackTemplateName, artifactKey, "project");
    }

    private static bool IsPrimaryProjectPath(SolutionTemplateModel model, string relativePath)
    {
        return relativePath.Equals(model.ApiProjectPath, StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals(model.ApplicationProjectPath, StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals(model.InfrastructureProjectPath, StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals(model.DomainProjectPath, StringComparison.OrdinalIgnoreCase) ||
               relativePath.Equals(model.TestsProjectPath, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildAdditionalProjectReferences(
        StandardProfile profile,
        string solutionName,
        string baseProjectPath,
        params string[] excludedProjectPaths)
    {
        if (profile.AdditionalProjects.Count == 0)
        {
            return [];
        }

        var normalizedBaseProjectPath = NormalizeRelativeTemplatePath(baseProjectPath, solutionName);
        var baseProjectDirectory = Path.GetDirectoryName(normalizedBaseProjectPath) ?? string.Empty;
        var excludedPaths = excludedProjectPaths
            .Select(projectPath => NormalizeRelativeTemplatePath(projectPath, solutionName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedProjectNames = excludedPaths
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return profile.AdditionalProjects
            .Select(project => NormalizeRelativeTemplatePath(project.RelativePath, solutionName))
            .Where(projectPath => !string.IsNullOrWhiteSpace(projectPath))
            .Where(projectPath => !excludedPaths.Contains(projectPath))
            .Where(projectPath => !excludedProjectNames.Contains(Path.GetFileNameWithoutExtension(projectPath)))
            .Select(projectPath => Path.GetRelativePath(baseProjectDirectory, projectPath)
                .Replace(Path.DirectorySeparatorChar, '\\')
                .Replace(Path.AltDirectorySeparatorChar, '\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(projectPath => projectPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildProjectReferencePath(string fromProjectPath, string toProjectPath)
    {
        var fromDirectory = Path.GetDirectoryName(fromProjectPath) ?? string.Empty;
        return Path.GetRelativePath(fromDirectory, toProjectPath)
            .Replace(Path.DirectorySeparatorChar, '\\')
            .Replace(Path.AltDirectorySeparatorChar, '\\');
    }

    private static bool LooksLikeApiProjectRoot(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        return File.Exists(Path.Combine(path, "Program.cs")) ||
               Directory.EnumerateFiles(path, "*.csproj", SearchOption.TopDirectoryOnly).Any();
    }

    private static string ToRelativeBasePath(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return relative is "." ? string.Empty : relative;
    }

    private static string GetLayerBasePath(ProjectLayoutContext layout, string layerName) =>
        layerName switch
        {
            "Api" => layout.ApiBasePath,
            "Application" => layout.ApplicationBasePath,
            "Infrastructure" => layout.InfrastructureBasePath,
            "Domain" => layout.DomainBasePath,
            _ => string.Empty
        };

    private static string CombineRelativePath(string basePath, string folder, string fileName)
    {
        var segments = new List<string>();

        if (!string.IsNullOrWhiteSpace(basePath))
        {
            segments.AddRange(basePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(segment => !string.IsNullOrWhiteSpace(segment) && segment != "."));
        }

        if (!string.IsNullOrWhiteSpace(folder))
        {
            segments.AddRange(folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(segment => !string.IsNullOrWhiteSpace(segment) && segment != "."));
        }

        segments.Add(fileName);
        return Path.Combine(segments.ToArray());
    }

    private static string ToPascalCase(string input)
    {
        var parts = Regex.Split(input, @"[^A-Za-z0-9]+")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length == 0)
        {
            return "Generated";
        }

        return string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while"
    };

    private static string ToCamelCaseIdentifier(string pascalName)
    {
        var camel = pascalName.Length == 0 ? "key" : char.ToLowerInvariant(pascalName[0]) + pascalName[1..];
        return CSharpKeywords.Contains(camel) || camel is "cancellationToken" or "request" or "service" or "repository"
            ? camel + "Key"
            : camel;
    }

    // Literal for sample data in generated tests; `variant` yields distinct values for the same type.
    private static string BuildSampleValue(string clrType, int variant) => clrType switch
    {
        "int" => $"{variant}",
        "long" => $"{variant}L",
        "short" => $"(short){variant}",
        "byte" => $"(byte){variant}",
        "bool" => variant == 1 ? "true" : "false",
        "double" => $"{variant}.5d",
        "float" => $"{variant}.5f",
        "decimal" => $"{variant}.25m",
        "DateTime" => $"new DateTime(2026, 1, {variant})",
        "DateTimeOffset" => $"new DateTimeOffset(2026, 1, {variant}, 0, 0, 0, TimeSpan.Zero)",
        "DateOnly" => $"new DateOnly(2026, 1, {variant})",
        "TimeOnly" => $"new TimeOnly(9, {variant})",
        "Guid" => $"Guid.Parse(\"00000000-0000-0000-0000-00000000000{variant}\")",
        "byte[]" => $"new byte[] {{ {variant} }}",
        _ => $"\"sample-{variant}\""
    };

    private static string MapToClrType(string sqlType, bool isNullable)
    {
        var clrType = sqlType.ToLowerInvariant() switch
        {
            "int" => "int",
            "integer" => "int",
            "bigint" => "long",
            "smallint" => "short",
            "tinyint" => "byte",
            "bit" => "bool",
            "float" => "double",
            "real" => "float",
            "datetime" => "DateTime",
            "datetime2" => "DateTime",
            "smalldatetime" => "DateTime",
            "datetimeoffset" => "DateTimeOffset",
            "date" => "DateOnly",
            "time" => "TimeOnly",
            "decimal" => "decimal",
            "numeric" => "decimal",
            "money" => "decimal",
            "smallmoney" => "decimal",
            "nvarchar" => "string",
            "varchar" => "string",
            "nchar" => "string",
            "char" => "string",
            "text" => "string",
            "ntext" => "string",
            "uniqueidentifier" => "Guid",
            "binary" => "byte[]",
            "varbinary" => "byte[]",
            "image" => "byte[]",
            "rowversion" => "byte[]",
            "timestamp" => "byte[]",
            _ => "string"
        };

        if (clrType is "string" or "byte[]")
        {
            return isNullable ? $"{clrType}?" : clrType;
        }

        return isNullable ? $"{clrType}?" : clrType;
    }

    private static string BuildSolutionName(string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFileName(fullPath);
        var normalized = Regex.Replace(candidate, @"[^A-Za-z0-9]", string.Empty);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "GeneratedApi";
        }

        return char.IsLetter(normalized[0]) ? normalized : $"Generated{normalized}";
    }

    private static string BuildSolutionFile(SolutionTemplateModel model)
    {
        var projects = BuildSolutionProjects(model);
        var folderEntries = BuildSolutionFolderEntries(projects, model.SolutionName);

        const string projectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
        const string solutionFolderTypeGuid = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
        var builder = new StringBuilder();
        builder.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        builder.AppendLine("# Visual Studio Version 17");
        builder.AppendLine("VisualStudioVersion = 17.0.31903.59");
        builder.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");

        foreach (var folder in folderEntries)
        {
            builder.AppendLine($@"Project(""{solutionFolderTypeGuid}"") = ""{folder.Name}"", ""{folder.Name}"", ""{folder.Id}""");
            builder.AppendLine("EndProject");
        }

        foreach (var project in projects)
        {
            builder.AppendLine($@"Project(""{projectTypeGuid}"") = ""{project.Name}"", ""{project.Path}"", ""{project.Id}""");
            builder.AppendLine("EndProject");
        }

        builder.AppendLine("Global");
        builder.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        builder.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
        builder.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");

        foreach (var project in projects)
        {
            builder.AppendLine($"\t\t{project.Id}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            builder.AppendLine($"\t\t{project.Id}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            builder.AppendLine($"\t\t{project.Id}.Release|Any CPU.ActiveCfg = Release|Any CPU");
            builder.AppendLine($"\t\t{project.Id}.Release|Any CPU.Build.0 = Release|Any CPU");
        }

        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
        builder.AppendLine("\t\tHideSolutionNode = FALSE");
        builder.AppendLine("\tEndGlobalSection");
        if (folderEntries.Count > 0)
        {
            builder.AppendLine("\tGlobalSection(NestedProjects) = preSolution");

            foreach (var folder in folderEntries.Where(entry => !string.IsNullOrWhiteSpace(entry.ParentId)))
            {
                builder.AppendLine($"\t\t{folder.Id} = {folder.ParentId}");
            }

            foreach (var project in projects)
            {
                if (!string.IsNullOrWhiteSpace(project.ParentFolderId))
                {
                    builder.AppendLine($"\t\t{project.Id} = {project.ParentFolderId}");
                }
            }

            builder.AppendLine("\tEndGlobalSection");
        }
        builder.AppendLine("EndGlobal");
        return builder.ToString();
    }

    private static IReadOnlyList<SolutionProjectEntry> BuildSolutionProjects(SolutionTemplateModel model)
    {
        var projects = new List<SolutionProjectEntry>();

        if (IsSingleApiLayout(model.Profile))
        {
            projects.Add(CreateSolutionProject(model.SolutionName, model.ApiProjectName, model.ApiProjectPath.Replace(Path.DirectorySeparatorChar, '\\')));
        }
        else
        {
            projects.Add(CreateSolutionProject(model.SolutionName, model.DomainProjectName, model.DomainProjectPath.Replace(Path.DirectorySeparatorChar, '\\')));
            projects.Add(CreateSolutionProject(model.SolutionName, model.ApplicationProjectName, model.ApplicationProjectPath.Replace(Path.DirectorySeparatorChar, '\\')));
            projects.Add(CreateSolutionProject(model.SolutionName, model.InfrastructureProjectName, model.InfrastructureProjectPath.Replace(Path.DirectorySeparatorChar, '\\')));
            projects.Add(CreateSolutionProject(model.SolutionName, model.ApiProjectName, model.ApiProjectPath.Replace(Path.DirectorySeparatorChar, '\\')));
        }

        if (IncludesUnitTests(model.Profile))
        {
            projects.Add(CreateSolutionProject(model.SolutionName, model.TestsProjectName, model.TestsProjectPath.Replace(Path.DirectorySeparatorChar, '\\')));
        }

        foreach (var additionalProject in model.Profile.AdditionalProjects)
        {
            var relativeProjectPath = NormalizeRelativeTemplatePath(additionalProject.RelativePath, model.SolutionName)
                .Replace(Path.DirectorySeparatorChar, '\\');
            var projectName = Path.GetFileNameWithoutExtension(relativeProjectPath);
            if (IsPrimaryProjectPath(model, relativeProjectPath.Replace('\\', Path.DirectorySeparatorChar)))
            {
                continue;
            }

            projects.Add(CreateSolutionProject(model.SolutionName, projectName, relativeProjectPath));
        }

        var folderEntries = BuildSolutionFolderEntries(projects, model.SolutionName);
        var parentFolderLookup = folderEntries.ToDictionary(folder => folder.FolderPath, folder => folder.Id, StringComparer.OrdinalIgnoreCase);

        return projects
            .Select(project =>
            {
                var directory = Path.GetDirectoryName(project.Path.Replace('\\', Path.DirectorySeparatorChar)) ?? string.Empty;
                var folderPath = NormalizeSolutionFolderPath(directory);
                return string.IsNullOrWhiteSpace(folderPath) || !parentFolderLookup.TryGetValue(folderPath, out var parentFolderId)
                    ? project
                    : project with { ParentFolderId = parentFolderId };
            })
            .ToList();
    }

    private static IReadOnlyList<SolutionFolderEntry> BuildSolutionFolderEntries(IReadOnlyList<SolutionProjectEntry> projects, string solutionName)
    {
        var entries = new Dictionary<string, SolutionFolderEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            var directory = Path.GetDirectoryName(project.Path.Replace('\\', Path.DirectorySeparatorChar)) ?? string.Empty;
            var normalizedDirectory = NormalizeSolutionFolderPath(directory);
            if (string.IsNullOrWhiteSpace(normalizedDirectory))
            {
                continue;
            }

            var segments = normalizedDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var currentPath = string.Empty;

            foreach (var segment in segments)
            {
                currentPath = string.IsNullOrWhiteSpace(currentPath)
                    ? segment
                    : Path.Combine(currentPath, segment);

                if (entries.ContainsKey(currentPath))
                {
                    continue;
                }

                var parentPath = Path.GetDirectoryName(currentPath) ?? string.Empty;
                entries[currentPath] = new SolutionFolderEntry(
                    segment,
                    currentPath,
                    CreateDeterministicProjectId(solutionName, $"folder:{currentPath}"),
                    string.IsNullOrWhiteSpace(parentPath) ? null : CreateDeterministicProjectId(solutionName, $"folder:{parentPath}"));
            }
        }

        return entries.Values
            .OrderBy(entry => entry.FolderPath.Count(character => character == Path.DirectorySeparatorChar))
            .ThenBy(entry => entry.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeSolutionFolderPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.Empty;
        }

        var segments = directory
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !segment.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (segments.Count <= 1)
        {
            return string.Empty;
        }

        segments.RemoveAt(segments.Count - 1);
        return Path.Combine(segments.ToArray());
    }

    private static SolutionProjectEntry CreateSolutionProject(string solutionName, string name, string path) =>
        new(name, path, CreateDeterministicProjectId(solutionName, name), null);

    private sealed record SolutionFolderEntry(string Name, string FolderPath, string Id, string? ParentId);

    private sealed record SolutionProjectEntry(string Name, string Path, string Id, string? ParentFolderId);

    private static string BuildLlmPrompt(SolutionTemplateModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Stateless LLM Standard Prompt");
        builder.AppendLine();
        builder.AppendLine("Use this prompt as a self-contained compatibility package when asking an LLM to refine generated code.");
        builder.AppendLine();
        builder.AppendLine("## Non-Negotiable Rules");

        foreach (var instruction in model.Profile.Llm.Instructions)
        {
            builder.AppendLine($"- {instruction}");
        }

        builder.AppendLine();
        builder.AppendLine("## Current Profile");
        builder.AppendLine($"- Profile: {model.Profile.ProfileName}");
        builder.AppendLine($"- Architecture: {model.Profile.ArchitectureStyle}");
        builder.AppendLine($"- Framework preset: {model.Profile.Framework.DisplayName} ({model.Profile.Framework.Id})");
        builder.AppendLine($"- API style: {model.Profile.Framework.ApiStyle}");
        builder.AppendLine($"- Service layer: {model.Profile.Framework.UseServiceLayer}");
        builder.AppendLine($"- Contract models: {model.Profile.Framework.UseContractModels}");
        builder.AppendLine($"- Controller base class: {model.Profile.Controller.BaseClass}");
        builder.AppendLine($"- Controller route template: {model.Profile.Controller.RouteTemplate}");
        builder.AppendLine($"- Logging enabled: {model.Profile.Logging.Enabled}");
        if (!model.Profile.Framework.DatabaseProvider.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- Database provider: {model.Profile.Framework.DatabaseProvider}");
            builder.AppendLine($"- Connection string name: {model.ConnectionStringName}");
        }

        if (model.Profile.Framework.UseWindowsAuthentication)
        {
            builder.AppendLine("- Windows authentication: enabled");
        }

        if (!string.IsNullOrWhiteSpace(model.Profile.Logging.HelperClassName))
        {
            builder.AppendLine($"- Logging helper: {model.Profile.Logging.HelperClassName}");
        }

        builder.AppendLine();
        builder.AppendLine("## Naming Rules");

        foreach (var namingRule in model.Profile.NamingRules.OrderBy(entry => entry.Key))
        {
            builder.AppendLine($"- {namingRule.Key}: {namingRule.Value}");
        }

        builder.AppendLine();
        builder.AppendLine("## Folder Rules");

        foreach (var folderRule in model.Profile.Folders.OrderBy(entry => entry.Key))
        {
            builder.AppendLine($"- {folderRule.Key}: {folderRule.Value}");
        }

        builder.AppendLine();
        builder.AppendLine("## Target Artifacts");

        foreach (var artifact in model.Profile.Llm.ApplyToArtifacts)
        {
            builder.AppendLine($"- {artifact}");
        }

        if (model.Profile.Llm.ApplyToArtifacts.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Artifact Prompt Profiles");

            foreach (var artifact in model.Profile.Llm.ApplyToArtifacts
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(artifact => artifact, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"### {artifact}");
                foreach (var line in BuildArtifactPromptProfileSummary(artifact))
                {
                    builder.AppendLine($"- {line}");
                }

                builder.AppendLine();
            }
        }

        if (model.Profile.Llm.ContextAssets.Count > 0)
        {
            builder.AppendLine("## Available Context Assets");

            foreach (var asset in model.Profile.Llm.ContextAssets.OrderBy(asset => asset, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {asset}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Suggested Prompt Wrapper");
        builder.AppendLine("Provide the profile JSON, the current file content, and the requested change in the same prompt.");
        builder.AppendLine("Ask the model to return only the revised code for the current file.");
        builder.AppendLine("Do not let the model assume hidden repository state beyond what is included in that single request.");
        return builder.ToString();
    }

    private static IReadOnlyList<string> BuildArtifactPromptProfileSummary(string artifactKey) =>
        artifactKey.ToLowerInvariant() switch
        {
            "apiprogram" =>
            [
                "Treat this file as the composition root and preserve startup, DI, middleware, auth, and swagger conventions.",
                "Avoid speculative packages or bootstrap structures that are not evidenced by the profile or support assets."
            ],
            "controller" or "endpointmodule" =>
            [
                "Keep the HTTP surface thin, preserve route and response conventions, and delegate business behavior to services.",
                "Avoid moving persistence or orchestration logic into the endpoint layer."
            ],
            "serviceimplementation" or "serviceinterface" =>
            [
                "Preserve service-layer orchestration, async signatures, validation flow, and collaboration patterns.",
                "Avoid mixing transport or low-level persistence details into application services."
            ],
            "repositoryimplementation" or "repositoryinterface" or "dbcontext" =>
            [
                "Preserve persistence conventions, provider setup, DbContext usage, and query style shown by the profile.",
                "Avoid business-logic drift inside persistence artifacts."
            ],
            "entity" =>
            [
                "Preserve entity or domain model shape, naming, keys, and nullability conventions.",
                "Avoid transport-only behavior or framework annotations unless the learned templates already use them."
            ],
            "dto" or "createrequest" or "updaterequest" or "response" =>
            [
                "Preserve contract-model naming and serializer-friendly shape.",
                "Avoid mixing persistence-only concerns into API transport models."
            ],
            "unittests" =>
            [
                "Prefer meaningful assertions and project-consistent mocking style.",
                "Avoid placeholder tests that only assert non-null or trivial behavior."
            ],
            _ =>
            [
                "Preserve the artifact's current responsibility and local project conventions.",
                "Avoid unrelated structural rewrites."
            ]
        };

    private static LlmExecutionSummary BuildDisabledLlmSummary(LlmExecutionSettings? settings)
    {
        if (settings is null)
        {
            return new LlmExecutionSummary
            {
                Enabled = false,
                Applied = false,
                ConfiguredMaxConcurrency = null,
                EffectiveMaxConcurrency = 0,
                TargetFiles = 0,
                RefinedFiles = 0,
                SkippedFiles = 0
            };
        }

        return new LlmExecutionSummary
        {
            Enabled = settings.Enabled,
            Applied = false,
            Url = settings.Url,
            Model = settings.Model,
            ConfiguredMaxConcurrency = settings.MaxConcurrency,
            EffectiveMaxConcurrency = 0,
            TargetFiles = 0,
            RefinedFiles = 0,
            SkippedFiles = 0
        };
    }

    // Framework.UseControllers is the single source of truth: Program.cs templates read the same flag, so files and
    // registrations can never disagree (Patterns only describe what a learned project looked like).
    private static bool UsesControllerArtifacts(StandardProfile profile) => profile.Framework.UseControllers;

    private static bool UsesServiceLayer(StandardProfile profile) =>
        profile.Framework.UseServiceLayer;

    private static bool UsesContractModels(StandardProfile profile) =>
        profile.Framework.UseContractModels;

    private static bool IsSingleApiLayout(StandardProfile profile) =>
        profile.Framework.ProjectLayout.Equals("single-api", StringComparison.OrdinalIgnoreCase);

    private static bool IncludesUnitTests(StandardProfile profile) =>
        profile.Framework.IncludeUnitTests;

    private static bool UsesAppSettings(StandardProfile profile) =>
        profile.Framework.UseAppSettings;

    private static StandardProfile ApplyFeatureSelection(StandardProfile profile, GenerationFeatureSelection? features, bool applyInPlace)
    {
        if (applyInPlace || features is null || !features.HasOverrides)
        {
            return profile;
        }

        var framework = profile.Framework;
        var adjustedFramework = new FrameworkStandard
        {
            Id = framework.Id,
            DisplayName = framework.DisplayName,
            ApiStyle = framework.ApiStyle,
            ProjectLayout = framework.ProjectLayout,
            UseControllers = framework.UseControllers,
            UseSwagger = framework.UseSwagger,
            IncludeUnitTests = ResolveFeatureSelection(features.UnitTests, framework.IncludeUnitTests),
            UseServiceLayer = framework.UseServiceLayer,
            UseContractModels = framework.UseContractModels,
            DatabaseProvider = framework.DatabaseProvider,
            UseWindowsAuthentication = ResolveFeatureSelection(features.WindowsAuthentication, framework.UseWindowsAuthentication),
            UseAppSettings = framework.UseAppSettings,
            GeneratePostmanCollection = ResolveFeatureSelection(features.PostmanCollection, framework.GeneratePostmanCollection),
            ConnectionStringName = framework.ConnectionStringName,
            DefaultConnectionString = framework.DefaultConnectionString,
            SupportsFrameworkSelection = framework.SupportsFrameworkSelection
        };

        return new StandardProfile
        {
            ProfileKind = profile.ProfileKind,
            SchemaVersion = profile.SchemaVersion,
            ProfileName = profile.ProfileName,
            Description = profile.Description,
            SourceProjectPath = profile.SourceProjectPath,
            ArchitectureStyle = profile.ArchitectureStyle,
            Framework = adjustedFramework,
            ProjectBasePaths = profile.ProjectBasePaths,
            Folders = profile.Folders,
            NamingRules = profile.NamingRules,
            Patterns = profile.Patterns,
            Controller = profile.Controller,
            Logging = profile.Logging,
            TemplateOverrides = profile.TemplateOverrides,
            SharedFiles = profile.SharedFiles,
            AdditionalProjects = profile.AdditionalProjects,
            Llm = profile.Llm
        };
    }

    private static bool ResolveFeatureSelection(FeatureSelectionMode mode, bool currentValue) =>
        mode switch
        {
            FeatureSelectionMode.Enable => true,
            FeatureSelectionMode.Disable => false,
            _ => currentValue
        };

    private static string ResolveConnectionString(StandardProfile profile, string solutionName, string? overrideConnectionString)
    {
        if (!string.IsNullOrWhiteSpace(overrideConnectionString))
        {
            return overrideConnectionString.Trim();
        }

        var value = string.IsNullOrWhiteSpace(profile.Framework.DefaultConnectionString)
            ? "Server=.;Database={{ SolutionName }}Db;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
            : profile.Framework.DefaultConnectionString;

        return value.Replace("{{ SolutionName }}", solutionName, StringComparison.OrdinalIgnoreCase);
    }

    private static int ComputePort(string solutionName, int basePort, int range)
    {
        var sum = solutionName.Sum(character => character);
        return basePort + (sum % range);
    }

    private static string CreateDeterministicProjectId(string solutionName, string projectName)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"{solutionName}:{projectName}"));
        return new Guid(bytes).ToString("B").ToUpperInvariant();
    }

}


