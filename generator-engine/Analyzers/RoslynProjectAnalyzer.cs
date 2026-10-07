using ApiGenerator.Cli.SqlParser;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Text.RegularExpressions;

namespace ApiGenerator.Cli.Analyzers;

public sealed class RoslynProjectAnalyzer
{
    public async Task<StandardProfile> LearnAsync(string projectPath)
    {
        var fullProjectPath = ProjectPathResolver.ResolveProjectRoot(projectPath);
        var solutionFile = Directory.GetFiles(fullProjectPath, "*.sln").FirstOrDefault();
        var sourceFiles = EnumerateSourceFiles(fullProjectPath).ToList();
        var solution = await TryOpenSolutionAsync(solutionFile);
        var solutionName = GetSolutionName(fullProjectPath, solutionFile);
        var projectCatalog = LearnProjectCatalog(fullProjectPath, solutionName, sourceFiles);
        var sampleAssets = await LearnSampleAssetsAsync(fullProjectPath, solutionName, sourceFiles);
        var framework = LearnFrameworkStandard(fullProjectPath, sampleAssets, sourceFiles);
        var projectBasePaths = LearnProjectBasePaths(projectCatalog);
        var additionalProjects = BuildAdditionalProjects(fullProjectPath, solutionName, projectCatalog, projectBasePaths);
        var folders = LearnFolders(fullProjectPath, solutionName, sourceFiles);
        var namingRules = LearnNamingRules(sourceFiles);

        foreach (var entry in StandardProfile.CreateDefault().ProjectBasePaths)
        {
            projectBasePaths.TryAdd(entry.Key, entry.Value);
        }

        foreach (var entry in StandardProfile.CreateDefault().Folders)
        {
            folders.TryAdd(entry.Key, entry.Value);
        }

        foreach (var entry in StandardProfile.CreateDefault().NamingRules)
        {
            namingRules.TryAdd(entry.Key, entry.Value);
        }

        var logging = LearnLoggingStandard(solutionName, sampleAssets);
        var controller = LearnControllerStandard(sampleAssets.GetTemplate("controller"));
        var patterns = LearnPatterns(solution, sourceFiles, logging, framework);

        return new StandardProfile
        {
            ProfileKind = "Standard",
            SchemaVersion = "2.1",
            ProfileName = $"{solutionName} Standard",
            Description = $"Learned from '{solutionName}' with reusable framework, controller, swagger, and support-file templates.",
            SourceProjectPath = fullProjectPath,
            ArchitectureStyle = framework.ProjectLayout.Equals("single-api", StringComparison.OrdinalIgnoreCase)
                ? "SingleProject"
                : projectBasePaths.Values.Select(path => path.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 2
                    ? "CustomLayered"
                    : solution?.Projects.Any(project => project.Name.Contains("Clean", StringComparison.OrdinalIgnoreCase)) == true
                        ? "CleanArchitecture"
                        : "Layered",
            Framework = framework,
            ProjectBasePaths = projectBasePaths,
            Folders = folders,
            NamingRules = namingRules,
            Patterns = patterns,
            Controller = controller,
            Logging = logging,
            TemplateOverrides = BuildTemplateOverrides(sampleAssets),
            SharedFiles = BuildSharedFiles(solutionName, sampleAssets.SharedFiles),
            AdditionalProjects = additionalProjects,
            Llm = BuildLlmStandard(logging, framework, sampleAssets)
        };
    }

    public Task<DatabaseSchema> ReadExistingSchemaAsync(string projectPath)
    {
        var fullProjectPath = ProjectPathResolver.ResolveProjectRoot(projectPath);
        var sourceFiles = EnumerateSourceFiles(fullProjectPath).ToList();
        var tables = FindEntityFiles(sourceFiles)
            .Select(TryParseEntityTable)
            .Where(table => table is not null)
            .Cast<TableDefinition>()
            .GroupBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        return Task.FromResult(new DatabaseSchema
        {
            Tables = tables
        });
    }
    private static async Task<Solution?> TryOpenSolutionAsync(string? solutionFile)
    {
        if (string.IsNullOrWhiteSpace(solutionFile))
        {
            return null;
        }

        try
        {
            using var workspace = MSBuildWorkspace.Create();
            return await workspace.OpenSolutionAsync(solutionFile);
        }
        catch
        {
            return null;
        }
    }

    private static string GetSolutionName(string projectPath, string? solutionFile)
    {
        if (!string.IsNullOrWhiteSpace(solutionFile))
        {
            return Path.GetFileNameWithoutExtension(solutionFile);
        }

        return new DirectoryInfo(projectPath).Name;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string projectPath)
    {
        return Directory.EnumerateFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<LearnedSampleAssets> LearnSampleAssetsAsync(
        string projectPath,
        string solutionName,
        IReadOnlyList<string> sourceFiles)
    {
        var assets = new LearnedSampleAssets();
        await LearnPrimaryTemplatesAsync(assets, solutionName, sourceFiles);

        foreach (var supportFile in FindSupportFiles(sourceFiles))
        {
            var content = await File.ReadAllTextAsync(supportFile);
            if (!ShouldIncludeSupportFile(supportFile, content))
            {
                continue;
            }

            assets.SharedFiles.Add(new LearnedFileSample
            {
                Path = supportFile,
                Description = DescribeSupportFile(supportFile),
                Content = GeneralizeSupportFile(content, supportFile, solutionName)
            });
        }

        return assets;
    }

    private static IEnumerable<string> FindEntityFiles(IEnumerable<string> sourceFiles)
    {
        return sourceFiles
            .Where(path =>
            {
                var normalized = path.Replace('\\', '/');
                var fileName = Path.GetFileNameWithoutExtension(path);

                if (!(normalized.Contains("/Entities/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/Models/", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                if (normalized.Contains("/Requests/", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("/Responses/", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("/Dtos/", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("/DTOs/", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return !fileName.Contains("Base", StringComparison.OrdinalIgnoreCase) &&
                       !fileName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) &&
                       !fileName.EndsWith("Service", StringComparison.OrdinalIgnoreCase) &&
                       !fileName.EndsWith("Repository", StringComparison.OrdinalIgnoreCase) &&
                       !fileName.EndsWith("DbContext", StringComparison.OrdinalIgnoreCase) &&
                       !fileName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static TableDefinition? TryParseEntityTable(string path)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        var classMatch = Regex.Match(content, @"public\s+(?:sealed\s+)?class\s+(?<name>[A-Za-z0-9_]+)");
        if (!classMatch.Success)
        {
            return null;
        }

        var entityName = classMatch.Groups["name"].Value;
        var propertyMatches = Regex.Matches(content, @"public\s+(?<required>required\s+)?(?<type>[A-Za-z0-9_?.<>]+)\s+(?<name>[A-Za-z0-9_]+)\s*\{\s*get;\s*set;\s*\}");
        var columns = new List<ColumnDefinition>();

        foreach (Match propertyMatch in propertyMatches)
        {
            var propertyType = propertyMatch.Groups["type"].Value.Trim();
            var propertyName = propertyMatch.Groups["name"].Value.Trim();
            var sqlType = MapClrTypeToSql(propertyType);
            if (sqlType is null)
            {
                continue;
            }

            var isPrimaryKey = propertyName.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                               propertyName.Equals($"{entityName}Id", StringComparison.OrdinalIgnoreCase);
            columns.Add(new ColumnDefinition
            {
                Name = propertyName,
                SqlType = sqlType,
                IsNullable = IsClrTypeNullable(propertyType, propertyMatch.Groups["required"].Success),
                IsPrimaryKey = isPrimaryKey,
                IsForeignKey = !isPrimaryKey && propertyName.EndsWith("Id", StringComparison.OrdinalIgnoreCase),
                Length = null,
                DefaultValue = null
            });
        }

        if (columns.Count == 0)
        {
            return null;
        }

        return new TableDefinition
        {
            Name = entityName,
            Columns = columns
        };
    }

    private static string? MapClrTypeToSql(string clrType)
    {
        var normalized = clrType.TrimEnd('?');
        return normalized switch
        {
            "int" => "int",
            "long" => "bigint",
            "short" => "smallint",
            "byte" => "tinyint",
            "bool" => "bit",
            "DateTime" => "datetime",
            "DateTimeOffset" => "datetimeoffset",
            "DateOnly" => "date",
            "TimeOnly" => "time",
            "decimal" => "decimal",
            "double" => "float",
            "float" => "real",
            "Guid" => "uniqueidentifier",
            "byte[]" => "varbinary",
            "string" => "nvarchar",
            _ => null
        };
    }

    private static bool IsClrTypeNullable(string clrType, bool isRequiredKeywordPresent)
    {
        if (clrType.EndsWith("?", StringComparison.Ordinal))
        {
            return true;
        }

        return clrType.StartsWith("string", StringComparison.OrdinalIgnoreCase) && !isRequiredKeywordPresent;
    }
    private static async Task LearnPrimaryTemplatesAsync(
        LearnedSampleAssets assets,
        string solutionName,
        IReadOnlyList<string> sourceFiles)
    {
        await LearnTemplateAsync(assets, "controller", FindControllerFile(sourceFiles), (content, path) => GeneralizeControllerTemplate(ReplaceOwnNamespace(ReplaceArtifactNamespaceUsings(content, sourceFiles), "ControllerNamespace"), path, solutionName));
        await LearnTemplateAsync(assets, "endpointModule", FindEndpointFile(sourceFiles), (content, path) => GeneralizeEndpointTemplate(ReplaceOwnNamespace(ReplaceArtifactNamespaceUsings(content, sourceFiles), "EndpointNamespace"), path, solutionName));
        await LearnTemplateAsync(assets, "serviceImplementation", FindServiceImplementationFile(sourceFiles), (content, path) => GeneralizeEntityArtifactTemplate(content, path, solutionName, null, "Service"));
        await LearnTemplateAsync(assets, "serviceInterface", FindServiceInterfaceFile(sourceFiles), (content, path) => GeneralizeEntityArtifactTemplate(content, path, solutionName, null, "Service", trimInterfacePrefix: true));
        await LearnTemplateAsync(assets, "repositoryImplementation", FindRepositoryImplementationFile(sourceFiles), (content, path) => GeneralizeEntityArtifactTemplate(content, path, solutionName, null, "Repository"));
        await LearnTemplateAsync(assets, "repositoryInterface", FindRepositoryInterfaceFile(sourceFiles), (content, path) => GeneralizeEntityArtifactTemplate(content, path, solutionName, null, "Repository", trimInterfacePrefix: true));
        await LearnTemplateAsync(assets, "entity", FindEntityFile(sourceFiles), (content, path) => GeneralizeEntityArtifactTemplate(content, path, solutionName, null, string.Empty));
        await LearnTemplateAsync(assets, "dto", FindDtoFile(sourceFiles), (content, path) => GeneralizeEntityArtifactTemplate(content, path, solutionName, null, "Dto"));
        await LearnTemplateAsync(assets, "createRequest", FindCreateRequestFile(sourceFiles), (content, path) => GeneralizeEntityArtifactTemplate(content, path, solutionName, "Create", "Request"));
        await LearnTemplateAsync(assets, "updateRequest", FindUpdateRequestFile(sourceFiles), (content, path) => GeneralizeEntityArtifactTemplate(content, path, solutionName, "Update", "Request"));
        await LearnTemplateAsync(assets, "response", FindResponseFile(sourceFiles), (content, path) => GeneralizeEntityArtifactTemplate(content, path, solutionName, null, "Response"));
        await LearnTemplateAsync(assets, "unitTests", FindUnitTestFile(sourceFiles), (content, path) => GeneralizeEntityArtifactTemplate(content, path, solutionName, null, "ServiceTests"));
        await LearnTemplateAsync(assets, "apiProgram", FindProgramFile(sourceFiles), (content, _) => GeneralizeProgramTemplate(ReplaceProgramArtifactUsings(content, sourceFiles), solutionName));
    }

    private static async Task LearnTemplateAsync(
        LearnedSampleAssets assets,
        string artifactKey,
        string? path,
        Func<string, string, string> generalize)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(path);
        assets.TemplateOverrides[artifactKey] = generalize(content, path);
    }

    // A single-key sample ("id" parameter) generalizes to every entity; composite-key samples would hardcode their keys.
    private static readonly Regex SingleIdParameter = new(@"\(\s*[A-Za-z0-9_<>?.]+\s+id\s*,", RegexOptions.Compiled);

    private static string? PreferSingleKeySample(IEnumerable<string> candidates)
    {
        var list = candidates.ToList();
        return list.FirstOrDefault(path => SingleIdParameter.IsMatch(SafeRead(path))) ?? list.FirstOrDefault();
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? FindControllerFile(IEnumerable<string> sourceFiles) =>
        PreferSingleKeySample(sourceFiles.Where(path => Path.GetFileName(path).EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase)));

    private static string? FindEndpointFile(IEnumerable<string> sourceFiles) =>
        PreferSingleKeySample(sourceFiles.Where(path =>
            (Path.GetFileName(path).EndsWith("Endpoints.cs", StringComparison.OrdinalIgnoreCase) ||
             Path.GetFileName(path).EndsWith("Endpoint.cs", StringComparison.OrdinalIgnoreCase)) &&
            (FileContains(path, "MapGroup") ||
             FileContains(path, "MapGet(") ||
             FileContains(path, "IEndpointRouteBuilder"))));

    private static string? FindServiceImplementationFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path =>
            Path.GetFileName(path).EndsWith("Service.cs", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(path).StartsWith("I", StringComparison.OrdinalIgnoreCase)) ??
        sourceFiles.FirstOrDefault(path =>
            Path.GetFileName(path).EndsWith("Handler.cs", StringComparison.OrdinalIgnoreCase));

    private static string? FindServiceInterfaceFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path =>
            Path.GetFileName(path).StartsWith("I", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path).EndsWith("Service.cs", StringComparison.OrdinalIgnoreCase));

    private static string? FindRepositoryImplementationFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path =>
            Path.GetFileName(path).EndsWith("Repository.cs", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(path).StartsWith("I", StringComparison.OrdinalIgnoreCase));

    private static string? FindRepositoryInterfaceFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path =>
            Path.GetFileName(path).StartsWith("I", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path).EndsWith("Repository.cs", StringComparison.OrdinalIgnoreCase));

    private static string? FindEntityFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path =>
            path.Contains($"{Path.DirectorySeparatorChar}Entities{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(path).Contains("Base", StringComparison.OrdinalIgnoreCase)) ??
        sourceFiles.FirstOrDefault(path =>
            path.Replace('\\', '/').Contains(".Entities/", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(path).Contains("Base", StringComparison.OrdinalIgnoreCase)) ??
        sourceFiles.FirstOrDefault(path =>
            path.Contains($"{Path.DirectorySeparatorChar}Models{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains($"{Path.DirectorySeparatorChar}Requests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains($"{Path.DirectorySeparatorChar}Responses{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains($"{Path.DirectorySeparatorChar}Dtos{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains($"{Path.DirectorySeparatorChar}DTOs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(path).Contains("Base", StringComparison.OrdinalIgnoreCase));

    private static string? FindDtoFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path => Path.GetFileName(path).EndsWith("Dto.cs", StringComparison.OrdinalIgnoreCase));

    private static string? FindCreateRequestFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path => Path.GetFileName(path).StartsWith("Create", StringComparison.OrdinalIgnoreCase) &&
                                           Path.GetFileName(path).EndsWith("Request.cs", StringComparison.OrdinalIgnoreCase)) ??
        FindAnyRequestFile(sourceFiles);

    private static string? FindUpdateRequestFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path => Path.GetFileName(path).StartsWith("Update", StringComparison.OrdinalIgnoreCase) &&
                                           Path.GetFileName(path).EndsWith("Request.cs", StringComparison.OrdinalIgnoreCase)) ??
        FindAnyRequestFile(sourceFiles);

    private static string? FindResponseFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path => Path.GetFileName(path).EndsWith("Response.cs", StringComparison.OrdinalIgnoreCase));

    private static string? FindAnyRequestFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path => Path.GetFileName(path).EndsWith("Request.cs", StringComparison.OrdinalIgnoreCase));

    private static string? FindServiceTestFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path => Path.GetFileName(path).EndsWith("ServiceTests.cs", StringComparison.OrdinalIgnoreCase)) ??
        FindUnitTestFile(sourceFiles);

    private static string? FindUnitTestFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path => Path.GetFileName(path).EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase));

    private static string? FindProgramFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path => string.Equals(Path.GetFileName(path), "Program.cs", StringComparison.OrdinalIgnoreCase));

    private static string? FindDbContextFile(IEnumerable<string> sourceFiles) =>
        sourceFiles.FirstOrDefault(path =>
            Path.GetFileName(path).EndsWith("DbContext.cs", StringComparison.OrdinalIgnoreCase) ||
            FileContains(path, ": DbContext"));

    private static IEnumerable<string> FindSupportFiles(IEnumerable<string> sourceFiles)
    {
        var excludedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            FindControllerFile(sourceFiles) ?? string.Empty,
            FindEndpointFile(sourceFiles) ?? string.Empty,
            FindServiceImplementationFile(sourceFiles) ?? string.Empty,
            FindServiceInterfaceFile(sourceFiles) ?? string.Empty,
            FindRepositoryImplementationFile(sourceFiles) ?? string.Empty,
            FindRepositoryInterfaceFile(sourceFiles) ?? string.Empty,
            FindEntityFile(sourceFiles) ?? string.Empty,
            FindDtoFile(sourceFiles) ?? string.Empty,
            FindCreateRequestFile(sourceFiles) ?? string.Empty,
            FindUpdateRequestFile(sourceFiles) ?? string.Empty,
            FindResponseFile(sourceFiles) ?? string.Empty,
            FindUnitTestFile(sourceFiles) ?? string.Empty,
            FindProgramFile(sourceFiles) ?? string.Empty
        };

        var supportFileMarkers = new[]
        {
            "LogHelper",
            "Logger",
            "Logging",
            "DependencyProvider",
            "DependencyManager",
            "Startup",
            "Swagger",
            "BaseController",
            "BaseApiController",
            "Extensions",
            "Extension",
            "Middleware",
            "Filter",
            "Options",
            "Configuration",
            "Policy",
            "ExceptionHandler",
            "HealthCheck",
            "MappingProfile"
        };
        var excludedSupportNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Class1",
            "WeatherForecast",
            "WeatherForecastController"
        };

        return sourceFiles
            .Where(path => !excludedFiles.Contains(path))
            .Where(path =>
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                var normalized = path.Replace('\\', '/');

                if (normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (excludedSupportNames.Contains(fileName))
                {
                    return false;
                }

                return IsSupportFileCandidate(path, fileName, supportFileMarkers);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string DescribeSupportFile(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (fileName.Contains("LogHelper", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Logger", StringComparison.OrdinalIgnoreCase))
        {
            return "Learned logging helper template.";
        }

        if (fileName.Contains("Swagger", StringComparison.OrdinalIgnoreCase))
        {
            return "Learned swagger support template.";
        }

        if (fileName.Contains("BaseController", StringComparison.OrdinalIgnoreCase))
        {
            return "Learned base controller template.";
        }

        if (fileName.Contains("Middleware", StringComparison.OrdinalIgnoreCase))
        {
            return "Learned middleware template.";
        }

        if (fileName.Contains("Filter", StringComparison.OrdinalIgnoreCase))
        {
            return "Learned filter template.";
        }

        if (fileName.Contains("Extension", StringComparison.OrdinalIgnoreCase))
        {
            return "Learned extension template.";
        }

        if (fileName.Contains("Options", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Configuration", StringComparison.OrdinalIgnoreCase))
        {
            return "Learned configuration template.";
        }

        return "Learned support file template.";
    }

    private static FrameworkStandard LearnFrameworkStandard(
        string projectPath,
        LearnedSampleAssets sampleAssets,
        IReadOnlyList<string> sourceFiles)
    {
        var usesControllers = sampleAssets.TemplateOverrides.ContainsKey("controller") || !sampleAssets.TemplateOverrides.ContainsKey("endpointModule");
        var apiStyle = usesControllers ? "controller" : "minimal";
        var hasSwaggerSupport = sampleAssets.SharedFiles.Any(file =>
            Path.GetFileNameWithoutExtension(file.Path).Contains("Swagger", StringComparison.OrdinalIgnoreCase)) ||
            (sampleAssets.GetTemplate("apiProgram")?.Contains("Swagger", StringComparison.OrdinalIgnoreCase) ?? false);
        var usesServiceLayer = sampleAssets.TemplateOverrides.ContainsKey("serviceImplementation") ||
                               sampleAssets.TemplateOverrides.ContainsKey("serviceInterface");
        var usesContractModels = sampleAssets.TemplateOverrides.ContainsKey("dto") ||
                                 sampleAssets.TemplateOverrides.ContainsKey("createRequest") ||
                                 sampleAssets.TemplateOverrides.ContainsKey("updateRequest") ||
                                 sampleAssets.TemplateOverrides.ContainsKey("response");
        var usesSingleApiLayout = DetectSingleApiLayout(projectPath, sourceFiles);
        var includesUnitTests = FindUnitTestFile(sourceFiles) is not null ||
                                Directory.Exists(Path.Combine(projectPath, "tests"));
        var programTemplate = sampleAssets.GetTemplate("apiProgram") ?? string.Empty;
        var databaseProvider = programTemplate.Contains("UseSqlServer", StringComparison.OrdinalIgnoreCase)
            ? "sqlserver"
            : programTemplate.Contains("UseInMemoryDatabase", StringComparison.OrdinalIgnoreCase)
                ? "inmemory"
                : "none";
        var useWindowsAuthentication = programTemplate.Contains("AddNegotiate", StringComparison.OrdinalIgnoreCase) ||
                                       programTemplate.Contains("UseAuthentication", StringComparison.OrdinalIgnoreCase);
        var useAppSettings = File.Exists(Path.Combine(projectPath, "appsettings.json")) ||
                             File.Exists(Path.Combine(projectPath, "src", $"{GetSolutionName(projectPath, Directory.GetFiles(projectPath, "*.sln").FirstOrDefault())}.Api", "appsettings.json"));
        var generatePostmanCollection = Directory.EnumerateFiles(projectPath, "*.postman_collection.json", SearchOption.AllDirectories).Any();

        return new FrameworkStandard
        {
            Id = usesControllers ? "learned-controller-api" : "learned-minimal-api",
            DisplayName = usesControllers ? "Learned Controller API" : "Learned Minimal API",
            ApiStyle = apiStyle,
            ProjectLayout = usesSingleApiLayout ? "single-api" : "layered",
            UseControllers = usesControllers,
            UseSwagger = hasSwaggerSupport,
            IncludeUnitTests = includesUnitTests,
            UseServiceLayer = usesServiceLayer,
            UseContractModels = usesContractModels,
            DatabaseProvider = databaseProvider,
            UseWindowsAuthentication = useWindowsAuthentication,
            UseAppSettings = useAppSettings,
            GeneratePostmanCollection = generatePostmanCollection,
            ConnectionStringName = "DefaultConnection",
            DefaultConnectionString = "Server=.;Database={{ SolutionName }}Db;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True",
            SupportsFrameworkSelection = true
        };
    }

    // Test projects sit next to single-project APIs too, so they must not count as extra layers.
    private static bool IsTestProject(string projectFilePath)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectFilePath);
        return projectName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
               projectName.EndsWith("Test", StringComparison.OrdinalIgnoreCase) ||
               FileContains(projectFilePath, "Microsoft.NET.Test.Sdk");
    }

    private static bool DetectSingleApiLayout(string projectPath, IReadOnlyList<string> sourceFiles)
    {
        var projectCount = Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.AllDirectories)
            .Count(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !IsTestProject(path));

        if (projectCount > 1)
        {
            return false;
        }

        var normalizedFiles = sourceFiles
            .Select(path => path.Replace('\\', '/'))
            .ToList();

        var hasLayeredMarkers = normalizedFiles.Any(path =>
            path.Contains(".Application/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(".Infrastructure/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(".Domain/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Application/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Infrastructure/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Domain/", StringComparison.OrdinalIgnoreCase));

        if (hasLayeredMarkers)
        {
            return false;
        }

        return normalizedFiles.Any(path =>
            path.Contains(".Api/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Controllers/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Endpoints/", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<LearnedProjectCatalogEntry> LearnProjectCatalog(
        string projectPath,
        string solutionName,
        IReadOnlyList<string> sourceFiles)
    {
        return Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var projectDirectory = Path.GetDirectoryName(path) ?? projectPath;
                var projectName = Path.GetFileNameWithoutExtension(path);
                var projectFiles = sourceFiles
                    .Where(file => IsFileUnderDirectory(file, projectDirectory))
                    .ToList();

                return new LearnedProjectCatalogEntry(
                    projectName,
                    path,
                    NormalizeLearnedRelativePath(Path.GetRelativePath(projectPath, projectDirectory), solutionName),
                    InferProjectRole(projectName, projectDirectory, projectFiles));
            })
            .ToList();
    }

    private static Dictionary<string, string> LearnProjectBasePaths(IReadOnlyList<LearnedProjectCatalogEntry> projectCatalog)
    {
        var projectBasePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AddProjectBasePath("api", "api");
        AddProjectBasePath("application", "application");
        AddProjectBasePath("infrastructure", "infrastructure");
        AddProjectBasePath("domain", "domain");
        AddProjectBasePath("tests", "tests");

        return projectBasePaths;

        void AddProjectBasePath(string key, string role)
        {
            var project = projectCatalog
                .Where(entry => entry.Role.Equals(role, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(GetRolePriority)
                .FirstOrDefault();

            if (project is null || string.IsNullOrWhiteSpace(project.BasePath))
            {
                return;
            }

            projectBasePaths[key] = project.BasePath;
        }
    }

    private static Dictionary<string, string> LearnFolders(string projectPath, string solutionName, IReadOnlyList<string> sourceFiles)
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entityNames = FindEntityFiles(sourceFiles)
            .Append(FindEntityFile(sourceFiles))
            .Where(file => file is not null)
            .Select(file => Path.GetFileNameWithoutExtension(file!))
            .Where(name => name.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        bool NameEndsWith(string path, string suffix) => Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        bool IsInterfaceName(string path) => Path.GetFileName(path) is { Length: > 1 } name && name[0] == 'I' && char.IsUpper(name[1]);

        AddFolder("controllers", FindControllerFile(sourceFiles), "Api", "Controllers", sourceFiles.Where(path => NameEndsWith(path, "Controller.cs")));
        AddFolder("endpoints", FindEndpointFile(sourceFiles), "Api", "Endpoints", sourceFiles.Where(path => NameEndsWith(path, "Endpoints.cs") || NameEndsWith(path, "Endpoint.cs")));
        AddFolder("data", FindDbContextFile(sourceFiles), "Api", "Data", []);
        AddFolder("services", FindServiceImplementationFile(sourceFiles), "Application", "Services", sourceFiles.Where(path => NameEndsWith(path, "Service.cs") && !IsInterfaceName(path)));
        AddFolder("repositories", FindRepositoryImplementationFile(sourceFiles), "Infrastructure", "Repositories", sourceFiles.Where(path => NameEndsWith(path, "Repository.cs") && !IsInterfaceName(path)));
        AddFolder("entities", FindEntityFile(sourceFiles), "Domain", "Entities", FindEntityFiles(sourceFiles));
        AddFolder("dtos", FindDtoFile(sourceFiles), "Application", "DTOs", sourceFiles.Where(path => NameEndsWith(path, "Dto.cs")));
        AddFolder("requests", FindCreateRequestFile(sourceFiles) ?? FindUpdateRequestFile(sourceFiles), "Application", "Requests", sourceFiles.Where(path => NameEndsWith(path, "Request.cs")));
        AddFolder("responses", FindResponseFile(sourceFiles), "Application", "Responses", sourceFiles.Where(path => NameEndsWith(path, "Response.cs")));
        AddFolder("repositoryInterfaces", FindRepositoryInterfaceFile(sourceFiles), "Application", "Abstractions/Persistence", sourceFiles.Where(path => NameEndsWith(path, "Repository.cs") && IsInterfaceName(path)));
        AddFolder("tests", FindServiceTestFile(sourceFiles), "Tests", string.Empty, sourceFiles.Where(path => NameEndsWith(path, "Tests.cs")));

        return folders;

        void AddFolder(string key, string? filePath, string layerName, string fallback, IEnumerable<string> sameKindFiles)
        {
            if (filePath is null)
            {
                return;
            }

            folders[key] = LearnLayerFolder(projectPath, solutionName, filePath, layerName, fallback, entityNames, sameKindFiles.ToList());
        }
    }

    private static string LearnLayerFolder(string projectPath, string solutionName, string filePath, string layerName, string fallback, IReadOnlyList<string> entityNames, IReadOnlyList<string> sameKindFiles)
    {
        var owningProjectDirectory = FindOwningProjectDirectory(projectPath, filePath);
        var fileDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(owningProjectDirectory) || string.IsNullOrWhiteSpace(fileDirectory))
        {
            return fallback;
        }

        var projectSegment = Path.GetFileName(owningProjectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var relativeFolder = KeepStructuralFolders(
            Path.GetRelativePath(owningProjectDirectory, fileDirectory),
            filePath,
            owningProjectDirectory,
            projectPath,
            sameKindFiles,
            entityNames);
        var normalizedRelativeFolder = NormalizeLearnedRelativePath(relativeFolder, solutionName);

        if (IsStandardLayerProject(projectSegment, layerName))
        {
            return string.IsNullOrWhiteSpace(normalizedRelativeFolder) ? fallback : GeneralizeFeatureFolderPath(normalizedRelativeFolder, filePath, entityNames);
        }

        var combined = string.IsNullOrWhiteSpace(normalizedRelativeFolder)
            ? projectSegment
            : Path.Combine(projectSegment, normalizedRelativeFolder);
        var generalized = NormalizeLearnedRelativePath(combined, solutionName);
        generalized = GeneralizeFeatureFolderPath(generalized, filePath, entityNames);
        return string.IsNullOrWhiteSpace(generalized) ? fallback : generalized;
    }

    private static string? FindOwningProjectDirectory(string projectPath, string filePath)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? projectPath);
        var projectRoot = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        while (current is not null && current.FullName.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (current.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).Any())
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsStandardLayerProject(string projectSegment, string layerName)
    {
        if (string.IsNullOrWhiteSpace(projectSegment))
        {
            return false;
        }

        return projectSegment.Equals(layerName, StringComparison.OrdinalIgnoreCase) ||
               projectSegment.EndsWith($".{layerName}", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLearnedRelativePath(string path, string solutionName)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return string.Empty;
        }

        return path
            .Replace(solutionName, "{{ SolutionName }}", StringComparison.OrdinalIgnoreCase)
            .Replace('\\', '/')
            .Trim('/');
    }

    // Comparing the sample with the other files of its kind in the same project tells structure from content: folders
    // every file shares (Services, Models/Requests) are kept, folders that differ from file to file belong to the
    // sample. Those become entity tokens when they carry the model name (Customers) and are dropped otherwise, so
    // module groupings of the reference (Sales/, Hr/) do not leak into generated projects.
    private static string KeepStructuralFolders(
        string relativeFolder,
        string filePath,
        string owningProjectDirectory,
        string projectPath,
        IReadOnlyList<string> sameKindFiles,
        IReadOnlyList<string> entityNames)
    {
        if (string.IsNullOrWhiteSpace(relativeFolder) || relativeFolder == ".")
        {
            return relativeFolder;
        }

        static string[] Split(string path) =>
            path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != ".")
                .ToArray();

        var segments = Split(relativeFolder);
        var siblings = sameKindFiles
            .Where(path => !string.Equals(path, filePath, StringComparison.Ordinal))
            .Where(path => string.Equals(FindOwningProjectDirectory(projectPath, path), owningProjectDirectory, StringComparison.Ordinal))
            .Select(path => Split(Path.GetRelativePath(owningProjectDirectory, Path.GetDirectoryName(path) ?? owningProjectDirectory)))
            .Where(other => other.Length == segments.Length)
            .ToList();
        if (siblings.Count == 0)
        {
            return relativeFolder;
        }

        var kept = new List<string>();
        for (var index = 0; index < segments.Length; index++)
        {
            if (siblings.All(other => string.Equals(other[index], segments[index], StringComparison.OrdinalIgnoreCase)))
            {
                kept.Add(segments[index]);
                continue;
            }

            var generalized = GeneralizeFeatureFolderPath(segments[index], filePath, entityNames);
            if (generalized.Contains("{{", StringComparison.Ordinal))
            {
                kept.Add(generalized);
            }
        }

        return kept.Count == 0 ? "." : string.Join(Path.DirectorySeparatorChar, kept);
    }

    // Folders named after the sample's model (Customers/, Customer/, CustomerModels/, CustomerOperations/) are learned as
    // entity tokens, so every generated entity gets its own folder instead of sharing the sample's.
    private static string GeneralizeFeatureFolderPath(string folderPath, string filePath, IReadOnlyList<string> entityNames)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return folderPath;
        }

        var featureName = ResolveSampleEntityName(filePath, entityNames);
        if (string.IsNullOrWhiteSpace(featureName))
        {
            return folderPath;
        }

        var forms = new Dictionary<string, string>(StringComparer.Ordinal);
        if (featureName.EndsWith('s') && featureName.Length > 1)
        {
            // Plural model names (Orders) keep the legacy behaviour: both forms map to the entity name.
            forms[featureName] = "{{ EntityName }}";
            forms[featureName[..^1]] = "{{ EntityName }}";
        }
        else
        {
            forms[$"{featureName}s"] = "{{ EntityPluralName }}";
            forms[$"{featureName}es"] = "{{ EntityPluralName }}";
            if (featureName.EndsWith('y'))
            {
                forms[$"{featureName[..^1]}ies"] = "{{ EntityPluralName }}";
            }

            forms[featureName] = "{{ EntityName }}";
        }

        var alternatives = string.Join('|', forms.Keys.OrderByDescending(form => form.Length).Select(Regex.Escape));
        // The model name must stand on a PascalCase boundary: CustomerModels matches, Customerish does not.
        var pattern = new Regex($"(?<![a-z])(?:{alternatives})(?![a-z])");
        var segments = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Contains("{{", StringComparison.Ordinal) || StructuralFolderNames.Contains(segment)
                ? segment
                : pattern.Replace(segment, match => forms[match.Value]))
            .ToArray();

        return string.Join('/', segments);
    }

    private static readonly HashSet<string> StructuralFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Entities", "Entity", "Models", "Model", "Services", "Service", "Repositories", "Repository", "Controllers",
        "Controller", "Requests", "Request", "Responses", "Response", "Interfaces", "Dtos", "Tests", "Endpoints", "Data"
    };

    // The sample's model is the longest known entity name that appears in the file name (CustomerCreateRequest ->
    // Customer); without entity files it is inferred from the file name's suffix.
    private static string ResolveSampleEntityName(string filePath, IReadOnlyList<string> entityNames)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var known = entityNames
            .Where(name => Regex.IsMatch(fileName, $"(?<![a-z]){Regex.Escape(name)}(?![a-z])"))
            .OrderByDescending(name => name.Length)
            .FirstOrDefault();
        return known ?? InferFeatureName(filePath);
    }

    private static string InferFeatureName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);

        if (name.StartsWith("I", StringComparison.OrdinalIgnoreCase) && name.Length > 1 && char.IsUpper(name[1]))
        {
            name = name[1..];
        }

        foreach (var prefix in new[] { "Create", "Update", "Delete", "Get", "List" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length)
            {
                name = name[prefix.Length..];
                break;
            }
        }

        foreach (var suffix in new[] { "Controller", "Endpoints", "Endpoint", "Service", "Handler", "Repository", "Dto", "Request", "Response", "ServiceTests" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return name;
    }

    private static Dictionary<string, string> LearnNamingRules(IReadOnlyList<string> sourceFiles)
    {
        var namingRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        LearnNamingRule(sourceFiles, "controller", path => Path.GetFileNameWithoutExtension(path).EndsWith("Controller", StringComparison.OrdinalIgnoreCase), null, "Controller", namingRules);
        LearnNamingRule(sourceFiles, "endpointModule", path => Path.GetFileNameWithoutExtension(path).EndsWith("Endpoints", StringComparison.OrdinalIgnoreCase), null, "Endpoints", namingRules);
        LearnNamingRule(sourceFiles, "serviceImplementation", path => Path.GetFileNameWithoutExtension(path).EndsWith("Service", StringComparison.OrdinalIgnoreCase) && !Path.GetFileNameWithoutExtension(path).StartsWith("I", StringComparison.OrdinalIgnoreCase), null, "Service", namingRules);
        LearnNamingRule(sourceFiles, "repositoryImplementation", path => Path.GetFileNameWithoutExtension(path).EndsWith("Repository", StringComparison.OrdinalIgnoreCase) && !Path.GetFileNameWithoutExtension(path).StartsWith("I", StringComparison.OrdinalIgnoreCase), null, "Repository", namingRules);
        LearnNamingRule(sourceFiles, "serviceInterface", path => Path.GetFileNameWithoutExtension(path).StartsWith("I", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(path).EndsWith("Service", StringComparison.OrdinalIgnoreCase), null, "Service", namingRules, trimInterfacePrefix: true);
        LearnNamingRule(sourceFiles, "repositoryInterface", path => Path.GetFileNameWithoutExtension(path).StartsWith("I", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(path).EndsWith("Repository", StringComparison.OrdinalIgnoreCase), null, "Repository", namingRules, trimInterfacePrefix: true);
        LearnNamingRule(sourceFiles, "dto", path => Path.GetFileNameWithoutExtension(path).EndsWith("Dto", StringComparison.OrdinalIgnoreCase), null, "Dto", namingRules);
        LearnVerbRequestNamingRule(sourceFiles, "createRequest", "Create", namingRules);
        LearnVerbRequestNamingRule(sourceFiles, "updateRequest", "Update", namingRules);
        LearnNamingRule(sourceFiles, "response", path => Path.GetFileNameWithoutExtension(path).EndsWith("Response", StringComparison.OrdinalIgnoreCase), null, "Response", namingRules);
        LearnNamingRule(sourceFiles, "test", path => Path.GetFileNameWithoutExtension(path).EndsWith("ServiceTests", StringComparison.OrdinalIgnoreCase), null, "ServiceTests", namingRules);
        return namingRules;
    }

    // Request names put the verb either before or after the entity (CreateCustomerRequest, CustomerCreateRequest);
    // the entity part is whatever remains once the verb and the "Request" suffix are removed.
    private static void LearnVerbRequestNamingRule(IReadOnlyList<string> sourceFiles, string ruleName, string verb, IDictionary<string, string> rules)
    {
        foreach (var file in sourceFiles)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.EndsWith("Request", StringComparison.Ordinal) || name.Length <= verb.Length + "Request".Length)
            {
                continue;
            }

            var stem = name[..^"Request".Length];
            string? template = null;
            if (stem.StartsWith(verb, StringComparison.Ordinal) && stem.Length > verb.Length && char.IsUpper(stem[verb.Length]))
            {
                template = $"{verb}{{Entity}}Request";
            }
            else if (stem.EndsWith(verb, StringComparison.Ordinal) && stem.Length > verb.Length)
            {
                template = $"{{Entity}}{verb}Request";
            }

            if (template is not null)
            {
                rules[ruleName] = template;
                return;
            }
        }
    }

    private static void LearnNamingRule(
        IReadOnlyList<string> sourceFiles,
        string ruleName,
        Func<string, bool> predicate,
        string? prefix,
        string suffix,
        IDictionary<string, string> rules,
        bool trimInterfacePrefix = false)
    {
        var file = sourceFiles.FirstOrDefault(predicate);
        if (file is null)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(file);
        if (trimInterfacePrefix && name.StartsWith("I", StringComparison.OrdinalIgnoreCase))
        {
            name = name[1..];
        }

        if (!string.IsNullOrWhiteSpace(prefix) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[prefix.Length..];
        }

        if (!string.IsNullOrWhiteSpace(suffix) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^suffix.Length];
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var builder = new List<string>();
        if (trimInterfacePrefix)
        {
            builder.Add("I");
        }

        if (!string.IsNullOrWhiteSpace(prefix))
        {
            builder.Add(prefix);
        }

        builder.Add("{Entity}");

        if (!string.IsNullOrWhiteSpace(suffix))
        {
            builder.Add(suffix);
        }

        rules[ruleName] = string.Concat(builder);
    }

    private static Dictionary<string, bool> LearnPatterns(
        Solution? solution,
        IReadOnlyList<string> sourceFiles,
        LoggingStandard logging,
        FrameworkStandard framework)
    {
        var usesControllers = framework.UseControllers;
        return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["usesUnitOfWork"] = solution?.Projects.Any(project => project.Name.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase)) == true ||
                                 sourceFiles.Any(path => Path.GetFileNameWithoutExtension(path).Contains("UnitOfWork", StringComparison.OrdinalIgnoreCase)),
            ["usesBaseEntity"] = sourceFiles.Any(path => Path.GetFileNameWithoutExtension(path).Equals("BaseEntity", StringComparison.OrdinalIgnoreCase)) ||
                                 sourceFiles.Any(path => FileContains(path, ": BaseEntity")),
            ["usesLogHelper"] = logging.Enabled,
            ["usesControllers"] = usesControllers,
            ["usesSwagger"] = framework.UseSwagger,
            ["usesEndpointModules"] = !usesControllers,
            // A reference with request/response models but no DTO classes should not get DTO files.
            ["singularEntityNames"] = LearnSingularEntityNames(sourceFiles),
            ["usesDtos"] = FindDtoFile(sourceFiles) is not null || (FindAnyRequestFile(sourceFiles) is null && FindResponseFile(sourceFiles) is null)
        };
    }

    // Entities named in the singular while a folder (or table mapping) uses the plural, e.g. Entities/Customer.cs with
    // Services/Customers/, mean tables should be generated as singular types.
    private static bool LearnSingularEntityNames(IReadOnlyList<string> sourceFiles)
    {
        var entityFile = FindEntityFile(sourceFiles);
        if (entityFile is null)
        {
            return false;
        }

        var entityName = Path.GetFileNameWithoutExtension(entityFile);
        if (entityName.EndsWith('s'))
        {
            return false;
        }

        var plurals = new[] { $"{entityName}s", $"{entityName}es", entityName.EndsWith('y') ? $"{entityName[..^1]}ies" : null }
            .Where(plural => plural is not null)
            .ToHashSet(StringComparer.Ordinal);
        var hasPluralFolder = sourceFiles.Any(path =>
            (Path.GetDirectoryName(path) ?? string.Empty)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => plurals.Contains(segment)));
        return hasPluralFolder ||
               plurals.Any(plural => FileContains(entityFile, $"\"{plural}\""));
    }

    private static ControllerStandard LearnControllerStandard(string? controllerTemplate)
    {
        if (string.IsNullOrWhiteSpace(controllerTemplate))
        {
            return StandardProfile.CreateDefault().Controller;
        }

        var baseClassMatch = Regex.Match(controllerTemplate, @"public\s+(?:sealed\s+)?class\s+\{\{\s*(?:ControllerName|EntityName)\s*\}\}\s*:\s*(?<baseClass>[A-Za-z0-9_<>.]+)");
        var routeMatch = Regex.Match(controllerTemplate, "\\[Route\\(\"(?<route>[^\"]+)\"\\)\\]");
        var hasApiController = controllerTemplate.Contains("[ApiController]", StringComparison.Ordinal);

        return new ControllerStandard
        {
            BaseClass = baseClassMatch.Success ? baseClassMatch.Groups["baseClass"].Value : "ControllerBase",
            RouteTemplate = routeMatch.Success ? routeMatch.Groups["route"].Value : "api/[controller]",
            UseApiControllerAttribute = hasApiController
        };
    }

    private static LoggingStandard LearnLoggingStandard(string solutionName, LearnedSampleAssets sampleAssets)
    {
        var loggingCarrier = string.Join(Environment.NewLine,
            sampleAssets.TemplateOverrides.Where(entry => entry.Key is "serviceImplementation" or "controller" or "endpointModule")
                .Select(entry => entry.Value));

        var logHelper = sampleAssets.SharedFiles.FirstOrDefault(file =>
            Path.GetFileNameWithoutExtension(file.Path).Contains("LogHelper", StringComparison.OrdinalIgnoreCase));
        var fieldMatch = Regex.Match(loggingCarrier, @"private\s+readonly\s+(?<type>(?:ILogger<[^>]+>|I?[A-Za-z0-9_<>.]+LogHelper))\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*;");
        var parameterMatch = Regex.Match(loggingCarrier, @"(?<type>ILogger<[^>]+>|I?[A-Za-z0-9_<>.]+LogHelper)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)");
        var helperClassName = logHelper is null ? null : Path.GetFileNameWithoutExtension(logHelper.Path);
        var helperNamespace = logHelper is null ? null : LearnNamespace(logHelper.Content);

        return new LoggingStandard
        {
            Enabled = logHelper is not null || parameterMatch.Success,
            HelperClassName = helperClassName,
            HelperNamespace = helperNamespace,
            HelperPath = logHelper is null ? null : BuildSharedFilePath(logHelper.Path, solutionName),
            ConstructorParameterType = parameterMatch.Success ? parameterMatch.Groups["type"].Value : null,
            ConstructorParameterName = parameterMatch.Success ? parameterMatch.Groups["name"].Value : null,
            FieldType = fieldMatch.Success ? fieldMatch.Groups["type"].Value.Trim() : null,
            FieldName = fieldMatch.Success ? fieldMatch.Groups["name"].Value.Trim() : null
        };
    }

    private static Dictionary<string, string> BuildTemplateOverrides(LearnedSampleAssets sampleAssets)
    {
        var reusableTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in sampleAssets.TemplateOverrides)
        {
            if (!ShouldPromoteAsReusableTemplate(entry.Key, entry.Value))
            {
                continue;
            }

            reusableTemplates[entry.Key] = entry.Value;
        }

        return reusableTemplates;
    }

    private static IReadOnlyList<SharedFileTemplate> BuildSharedFiles(string solutionName, IReadOnlyList<LearnedFileSample> sharedFiles)
    {
        return sharedFiles
            .Select(file => new SharedFileTemplate
            {
                RelativePath = BuildSharedFilePath(file.Path, solutionName),
                Category = "support",
                Description = file.Description,
                Template = file.Content
            })
            .DistinctBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<AdditionalProjectTemplate> BuildAdditionalProjects(
        string projectPath,
        string solutionName,
        IReadOnlyList<LearnedProjectCatalogEntry> projectCatalog,
        IReadOnlyDictionary<string, string> projectBasePaths)
    {
        var primaryProjectDirectories = projectBasePaths.Values
            .Select(basePath => NormalizeLearnedRelativePath(basePath, solutionName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return projectCatalog
            .Where(project => project.Role.Equals("additional", StringComparison.OrdinalIgnoreCase))
            .Where(project => !project.ProjectFilePath.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(project => !primaryProjectDirectories.Contains(project.BasePath))
            .Select(project => new AdditionalProjectTemplate
            {
                RelativePath = NormalizeLearnedRelativePath(Path.GetRelativePath(projectPath, project.ProjectFilePath), solutionName),
                Category = "project",
                Template = GeneralizeSolutionTokens(File.ReadAllText(project.ProjectFilePath), solutionName)
            })
            .DistinctBy(project => project.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSupportFileCandidate(string filePath, string fileName, IReadOnlyList<string> supportFileMarkers)
    {
        if (supportFileMarkers.Any(marker => fileName.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch
        {
            return false;
        }

        var hasDependencyRegistration =
            content.Contains("IServiceCollection", StringComparison.OrdinalIgnoreCase) &&
            (content.Contains("AddScoped", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("AddTransient", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("AddSingleton", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("AddDbContext", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("AddHttpClient", StringComparison.OrdinalIgnoreCase) ||
             content.Contains("AddAuthentication", StringComparison.OrdinalIgnoreCase));

        var hasPipelineSetup =
            content.Contains("IApplicationBuilder", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("WebApplication", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("app.Use", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("app.Map", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("UseSwagger", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("UseExceptionHandler", StringComparison.OrdinalIgnoreCase);

        var hasConfigSurface =
            content.Contains("IConfiguration", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("BindConfiguration", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Configure<", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("AuthorizationPolicy", StringComparison.OrdinalIgnoreCase);

        return hasDependencyRegistration || hasPipelineSetup || hasConfigSurface;
    }

    private static string InferProjectRole(string projectName, string projectDirectory, IReadOnlyList<string> projectFiles)
    {
        var normalizedDirectory = projectDirectory.Replace('\\', '/');

        if (normalizedDirectory.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
            projectName.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) ||
            projectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
        {
            return "tests";
        }

        if (projectName.EndsWith(".Api", StringComparison.OrdinalIgnoreCase) ||
            projectFiles.Any(path =>
                Path.GetFileName(path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/Controllers/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/Endpoints/", StringComparison.OrdinalIgnoreCase)))
        {
            return "api";
        }

        if (projectName.EndsWith(".App", StringComparison.OrdinalIgnoreCase) ||
            projectName.EndsWith(".Application", StringComparison.OrdinalIgnoreCase) ||
            projectFiles.Any(path =>
                path.Contains("/Services/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/Queries/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/Commands/", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).EndsWith("Handler.cs", StringComparison.OrdinalIgnoreCase)))
        {
            return "application";
        }

        if (projectName.EndsWith(".DataAccess", StringComparison.OrdinalIgnoreCase) ||
            projectName.EndsWith(".Infrastructure", StringComparison.OrdinalIgnoreCase) ||
            projectFiles.Any(path =>
                path.Contains("/Repositories/", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).EndsWith("DbContext.cs", StringComparison.OrdinalIgnoreCase) ||
                FileContains(path, ": DbContext")))
        {
            return "infrastructure";
        }

        if (projectName.EndsWith(".Entities", StringComparison.OrdinalIgnoreCase) ||
            projectName.EndsWith(".Domain", StringComparison.OrdinalIgnoreCase) ||
            projectFiles.Any(path =>
                path.Contains("/Entities/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/Models/", StringComparison.OrdinalIgnoreCase)))
        {
            return "domain";
        }

        return "additional";
    }

    private static int GetRolePriority(LearnedProjectCatalogEntry entry)
    {
        return entry.Role switch
        {
            "api" => GetProjectNamePriority(entry.ProjectName, ".Api"),
            "application" => Math.Max(GetProjectNamePriority(entry.ProjectName, ".App"), GetProjectNamePriority(entry.ProjectName, ".Application")),
            "infrastructure" => Math.Max(GetProjectNamePriority(entry.ProjectName, ".DataAccess"), GetProjectNamePriority(entry.ProjectName, ".Infrastructure")),
            "domain" => Math.Max(GetProjectNamePriority(entry.ProjectName, ".Entities"), GetProjectNamePriority(entry.ProjectName, ".Domain")),
            "tests" => Math.Max(GetProjectNamePriority(entry.ProjectName, ".UnitTests"), GetProjectNamePriority(entry.ProjectName, ".Tests")),
            _ => 0
        };
    }

    private static int GetProjectNamePriority(string projectName, string suffix) =>
        projectName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? 100 : 10;

    private static bool IsFileUnderDirectory(string filePath, string directoryPath)
    {
        var fullDirectoryPath = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullFilePath = Path.GetFullPath(filePath);
        return fullFilePath.StartsWith(fullDirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSharedFilePath(string filePath, string solutionName)
    {
        var owningProjectDirectory = FindOwningProjectDirectoryFromFile(filePath);
        var solutionRoot = FindSolutionRootFromFile(filePath);

        if (!string.IsNullOrWhiteSpace(owningProjectDirectory) && !string.IsNullOrWhiteSpace(solutionRoot))
        {
            var relativeProjectPath = Path.GetRelativePath(solutionRoot, owningProjectDirectory);
            var relativeFilePath = Path.GetRelativePath(owningProjectDirectory, filePath);
            var combined = Path.Combine(relativeProjectPath, relativeFilePath);
            var normalizedProjectPath = NormalizeLearnedRelativePath(combined, solutionName);
            if (!string.IsNullOrWhiteSpace(normalizedProjectPath))
            {
                return normalizedProjectPath;
            }
        }

        var normalized = filePath.Replace('\\', '/');
        var testsMatch = Regex.Match(normalized, @"^.*?/tests/[^/]+/(?<rest>.+)$", RegexOptions.IgnoreCase);
        if (testsMatch.Success)
        {
            var rest = testsMatch.Groups["rest"].Value.TrimStart('/');
            return $"src/{{{{ SolutionName }}}}.Api/Shared/TestSupport/{rest}";
        }

        return $"src/{{{{ SolutionName }}}}.Api/Shared/{Path.GetFileName(filePath)}";
    }

    private static string? FindOwningProjectDirectoryFromFile(string filePath)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? string.Empty);

        while (current is not null)
        {
            if (current.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).Any())
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? FindSolutionRootFromFile(string filePath)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? string.Empty);

        while (current is not null)
        {
            if (current.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly).Any())
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool ShouldPromoteAsReusableTemplate(string artifactKey, string template)
    {
        if (artifactKey.Equals("apiProgram", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (artifactKey.Equals("controller", StringComparison.OrdinalIgnoreCase))
        {
            return IsCrudControllerTemplate(template);
        }

        if (artifactKey.Equals("endpointModule", StringComparison.OrdinalIgnoreCase))
        {
            return IsCrudEndpointTemplate(template);
        }

        return false;
    }

    private static bool IsCrudControllerTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return false;
        }

        var actionMatches = Regex.Matches(template, @"public\s+(?:async\s+)?(?:Task<[^>]+>|Task|IActionResult|ActionResult<[^>]+>|ActionResult)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)");
        if (actionMatches.Count == 0)
        {
            return false;
        }

        var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Get",
            "GetAll",
            "GetById",
            "Create",
            "Post",
            "Update",
            "Put",
            "Delete"
        };

        return actionMatches
            .Select(match => match.Groups["name"].Value)
            .All(name => allowedNames.Contains(name));
    }

    private static bool IsCrudEndpointTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return false;
        }

        var endpointMatches = Regex.Matches(template, @"Map(?:Get|Post|Put|Delete)\(");
        return endpointMatches.Count > 0 && endpointMatches.Count <= 5;
    }

    private static LlmStandard BuildLlmStandard(
        LoggingStandard logging,
        FrameworkStandard framework,
        LearnedSampleAssets sampleAssets)
    {
        var reusableTemplateKeys = BuildTemplateOverrides(sampleAssets).Keys;
        var applyToArtifacts = reusableTemplateKeys
            .Concat(sampleAssets.SharedFiles.Count > 0 ? ["sharedFiles"] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key)
            .ToList();

        return new LlmStandard
        {
            Enabled = applyToArtifacts.Count > 0 || logging.Enabled,
            Mode = "stateless",
            ApplyToArtifacts = applyToArtifacts,
            ContextAssets = sampleAssets.SharedFiles.Select(file => Path.GetFileName(file.Path)).Distinct().ToArray(),
            Instructions =
            [
                "Use only the profile JSON, the current artifact content, and the example assets supplied in the current prompt.",
                "Do not rely on any hidden or prior session memory; regenerate conventions from the provided profile every time.",
                $"Preserve the selected framework preset '{framework.DisplayName}' and its API style '{framework.ApiStyle}'.",
                "Keep namespaces, DI registrations, controller or endpoint style, swagger wiring, and logging helpers aligned with the profile."
            ]
        };
    }

    // Usings that point at the sample's own artifacts (Services.Customers, Repositories.Interfaces, ...) become namespace
    // tokens, so generated files follow the learned folders instead of a namespace guessed from the sample entity name.
    private static string ReplaceArtifactNamespaceUsings(string content, IReadOnlyList<string> sourceFiles)
    {
        var artifacts = new (string? File, string Token)[]
        {
            (FindServiceInterfaceFile(sourceFiles), "ServiceInterfaceNamespace"),
            (FindServiceImplementationFile(sourceFiles), "ServiceImplementationNamespace"),
            (FindCreateRequestFile(sourceFiles), "CreateRequestNamespace"),
            (FindUpdateRequestFile(sourceFiles), "UpdateRequestNamespace"),
            (FindResponseFile(sourceFiles), "ResponseNamespace"),
            (FindDtoFile(sourceFiles), "DtoNamespace"),
            (FindEntityFile(sourceFiles), "EntityNamespace"),
            (FindRepositoryInterfaceFile(sourceFiles), "RepositoryInterfaceNamespace")
        };

        var result = content;
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, token) in artifacts)
        {
            if (file is null)
            {
                continue;
            }

            var artifactNamespace = LearnNamespace(File.ReadAllText(file));
            if (string.IsNullOrWhiteSpace(artifactNamespace) || !replaced.Add(artifactNamespace))
            {
                continue;
            }

            result = Regex.Replace(
                result,
                $@"(?m)^(\s*using\s+){Regex.Escape(artifactNamespace)}(\s*;)",
                $"$1{{{{ {token} }}}}$2");
        }

        return result;
    }

    // The sample's own namespace mirrors its folder (Controllers.V1.Sales); generated files use the namespace of the
    // folder they are written to instead.
    private static string ReplaceOwnNamespace(string content, string token) =>
        new Regex(@"(?m)^(\s*namespace\s+)[A-Za-z0-9_.]+").Replace(content, $"$1{{{{ {token} }}}}", 1);

    // Program.cs registers every entity, so the sample's per-entity usings become one de-duplicated using per namespace
    // of the generated services and repositories.
    private static string ReplaceProgramArtifactUsings(string content, IReadOnlyList<string> sourceFiles)
    {
        var artifacts = new (string? File, string Token)[]
        {
            (FindServiceInterfaceFile(sourceFiles), "ServiceInterfaceNamespace"),
            (FindServiceImplementationFile(sourceFiles), "ServiceImplementationNamespace"),
            (FindRepositoryInterfaceFile(sourceFiles), "RepositoryInterfaceNamespace"),
            (FindRepositoryImplementationFile(sourceFiles), "RepositoryImplementationNamespace")
        };

        var tokens = new List<string>();
        var result = content;
        var insertAt = -1;
        foreach (var (file, token) in artifacts)
        {
            var artifactNamespace = file is null ? null : LearnNamespace(File.ReadAllText(file));
            if (string.IsNullOrWhiteSpace(artifactNamespace))
            {
                continue;
            }

            var pattern = new Regex($@"(?m)^\s*using\s+{Regex.Escape(artifactNamespace)}\s*;[ \t]*\r?\n?");
            var match = pattern.Match(result);
            if (!match.Success && !tokens.Any(existing => NamespaceOf(existing) == artifactNamespace))
            {
                continue;
            }

            if (match.Success)
            {
                insertAt = insertAt < 0 ? match.Index : Math.Min(insertAt, match.Index);
                result = pattern.Replace(result, string.Empty, 1);
            }

            tokens.Add($"{token}|{artifactNamespace}");
        }

        if (tokens.Count == 0 || insertAt < 0)
        {
            return content;
        }

        var sources = string.Join(" | array.add_range ", tokens.Select(token => $"(Entities | array.map \"{token.Split('|')[0]}\")"));
        var block = $"{{{{ for ns in ({sources} | array.uniq) }}}}using {{{{ ns }}}};{Environment.NewLine}{{{{ end }}}}";
        return result.Insert(Math.Min(insertAt, result.Length), block);

        static string NamespaceOf(string entry) => entry.Split('|')[1];
    }

    private static string GeneralizeControllerTemplate(string content, string filePath, string solutionName)
    {
        var entityName = ExtractEntityName(filePath, null, "Controller");
        var generalized = GeneralizeEntityArtifactTemplate(content, filePath, solutionName, null, "Controller");
        generalized = Regex.Replace(generalized, @"\b(public\s+async\s+Task<IActionResult>\s+GetById\()([A-Za-z0-9_<>?.]+)(\s+id\b)", "$1{{ PrimaryKeyType }}$3");
        generalized = Regex.Replace(generalized, @"\b(public\s+async\s+Task<IActionResult>\s+Update\()([A-Za-z0-9_<>?.]+)(\s+id\b)", "$1{{ PrimaryKeyType }}$3");
        generalized = Regex.Replace(generalized, @"\b(public\s+async\s+Task<IActionResult>\s+Delete\()([A-Za-z0-9_<>?.]+)(\s+id\b)", "$1{{ PrimaryKeyType }}$3");
        generalized = GeneralizePrimaryKeyAccess(generalized);
        generalized = GeneralizeKeyParameters(generalized);
        generalized = generalized.Replace($"{entityName}Controller", "{{ ControllerName }}", StringComparison.Ordinal);
        return generalized;
    }

    private static string GeneralizeEndpointTemplate(string content, string filePath, string solutionName)
    {
        var generalized = GeneralizeEntityArtifactTemplate(content, filePath, solutionName, null, "Endpoints");
        generalized = Regex.Replace(generalized, "\"/api/[A-Za-z0-9_]+\"", "\"/api/{{ EntityName }}\"");
        generalized = Regex.Replace(generalized, @"\(\s*[A-Za-z0-9_<>?.]+\s+id\s*,", "({{ PrimaryKeyType }} id,");
        generalized = GeneralizePrimaryKeyAccess(generalized);
        generalized = GeneralizeKeyParameters(generalized);
        generalized = generalized.Replace("/{result.{{ PrimaryKeyName }}}", "/{{ for key in Keys }}{result.{{ key.PropertyName }}}{{ if !for.last }}/{{ end }}{{ end }}", StringComparison.Ordinal);
        return generalized;
    }

    private static string GeneralizeEntityArtifactTemplate(
        string content,
        string filePath,
        string solutionName,
        string? prefix,
        string suffix,
        bool trimInterfacePrefix = false)
    {
        var entityName = ExtractEntityName(filePath, prefix, suffix, trimInterfacePrefix);
        var generalized = GeneralizeSolutionTokens(content, solutionName);
        return ReplaceEntityTokens(generalized, entityName);
    }

    private static string ExtractEntityName(string filePath, string? prefix, string suffix, bool trimInterfacePrefix = false)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);

        if (trimInterfacePrefix && name.StartsWith("I", StringComparison.OrdinalIgnoreCase))
        {
            name = name[1..];
        }

        if (!string.IsNullOrWhiteSpace(prefix) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[prefix.Length..];
        }

        if (!string.IsNullOrWhiteSpace(suffix) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^suffix.Length];
        }

        return name;
    }

    private static string GeneralizeProgramTemplate(string content, string solutionName)
    {
        var generalized = GeneralizeSolutionTokens(content, solutionName);
        generalized = ReplaceFirst(generalized, @"builder\.Services\.AddScoped<I[A-Za-z0-9_]+Service,\s*[A-Za-z0-9_]+Service>\(\);\s*", "{{ for entity in Entities }}\nbuilder.Services.AddScoped<{{ entity.ServiceInterfaceName }}, {{ entity.ServiceImplementationName }}>();\n{{ end }}\n");
        generalized = ReplaceFirst(generalized, @"builder\.Services\.AddScoped<I[A-Za-z0-9_]+Repository,\s*[A-Za-z0-9_]+Repository>\(\);\s*", "{{ for entity in Entities }}\nbuilder.Services.AddScoped<{{ entity.RepositoryInterfaceName }}, {{ entity.RepositoryImplementationName }}>();\n{{ end }}\n");
        generalized = ReplaceFirst(generalized, @"app\.MapControllers\(\);\s*", "{{ if Profile.Framework.UseControllers }}app.MapControllers();\n{{ else }}{{ for entity in Entities }}app.Map{{ entity.EntityName }}Endpoints();\n{{ for method in entity.RecipeMethods }}app.Map{{ entity.EntityName }}{{ method }}Endpoint();\n{{ end }}{{ end }}{{ end }}\n");
        return generalized;
    }

    private static string GeneralizeSolutionTokens(string content, string solutionName)
    {
        return string.IsNullOrWhiteSpace(solutionName)
            ? content
            : Regex.Replace(content, $@"\b{Regex.Escape(solutionName)}\b", "{{ SolutionName }}");
    }

    private static string ReplaceEntityTokens(string content, string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            return content;
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"I{entityName}Service"] = "{{ ServiceInterfaceName }}",
            [$"{entityName}Service"] = "{{ ServiceImplementationName }}",
            [$"I{entityName}Repository"] = "{{ RepositoryInterfaceName }}",
            [$"{entityName}Repository"] = "{{ RepositoryImplementationName }}",
            [$"{entityName}Controller"] = "{{ ControllerName }}",
            [$"{entityName}Endpoints"] = "{{ EndpointModuleName }}",
            [$"{entityName}Response"] = "{{ ResponseName }}",
            [$"{entityName}Dto"] = "{{ DtoName }}",
            [$"Create{entityName}Request"] = "{{ CreateRequestName }}",
            [$"Update{entityName}Request"] = "{{ UpdateRequestName }}",
            [$"{entityName}CreateRequest"] = "{{ CreateRequestName }}",
            [$"{entityName}UpdateRequest"] = "{{ UpdateRequestName }}",
            [$"{entityName}ServiceTests"] = "{{ TestClassName }}",
            [entityName] = "{{ EntityTypeName }}"
        };

        var result = content;

        foreach (var replacement in replacements.OrderByDescending(entry => entry.Key.Length))
        {
            result = result.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
        }

        return result;
    }

    private static string? LearnNamespace(string content)
    {
        var match = Regex.Match(content, @"namespace\s+(?<namespace>[A-Za-z0-9_.]+)");
        return match.Success ? match.Groups["namespace"].Value : null;
    }

    private static string GeneralizeSupportFile(string content, string filePath, string solutionName)
    {
        var generalized = GeneralizeSolutionTokens(content, solutionName);
        var fileName = Path.GetFileName(filePath);

        if (fileName.Contains("DependencyProvider", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("DependencyManager", StringComparison.OrdinalIgnoreCase))
        {
            generalized = GeneralizeDependencyRegistrationTemplate(generalized);
        }

        if (fileName.Equals("Startup.cs", StringComparison.OrdinalIgnoreCase))
        {
            generalized = GeneralizeStartupTemplate(generalized);
        }

        return generalized;
    }

    private static bool ShouldIncludeSupportFile(string filePath, string content)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var normalizedPath = filePath.Replace('\\', '/');

        if (normalizedPath.Contains("/Samples/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/Sample/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/Examples/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/Example/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/Demo/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (fileName.Contains("Sample", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Example", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Demo", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Dummy", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Fake", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Placeholder", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsTrivialEmptyType(content))
        {
            return false;
        }

        if (fileName.Contains("DependencyProvider", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("DependencyManager", StringComparison.OrdinalIgnoreCase))
        {
            return content.Contains("IServiceCollection", StringComparison.OrdinalIgnoreCase) &&
                   (content.Contains("AddScoped", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("AddTransient", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("AddSingleton", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("AddDbContext", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("AddHttpClient", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("AddAuthentication", StringComparison.OrdinalIgnoreCase));
        }

        if (fileName.Equals("Startup", StringComparison.OrdinalIgnoreCase))
        {
            return content.Contains("ConfigureServices", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("Configure(", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("builder.Services", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("app.Use", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("app.Map", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool IsTrivialEmptyType(string content)
    {
        var normalized = Regex.Replace(content, @"using\s+[A-Za-z0-9_.]+\s*;\s*", string.Empty);
        normalized = Regex.Replace(normalized, @"namespace\s+[A-Za-z0-9_.]+\s*;\s*", string.Empty);
        normalized = Regex.Replace(normalized, @"namespace\s+[A-Za-z0-9_.]+\s*\{", "{");
        normalized = Regex.Replace(normalized, @"\s+", string.Empty);

        return Regex.IsMatch(
            normalized,
            @"^(public|internal)?(static)?(sealed)?class[A-Za-z0-9_]+\{\}$",
            RegexOptions.IgnoreCase) ||
               Regex.IsMatch(
                   normalized,
                   @"^(public|internal)?(static)?class[A-Za-z0-9_]+\{\}$",
                   RegexOptions.IgnoreCase);
    }

    private static string GeneralizeDependencyRegistrationTemplate(string content)
    {
        var generalized = content;

        generalized = Regex.Replace(
            generalized,
            @"^using\s+\{\{\s*SolutionName\s*\}\}\.[A-Za-z0-9_.]+(?:\.Queries(?:\.Handlers)?|\.Repositories|\.Abstractions\.Persistence)\s*;\r?\n",
            string.Empty,
            RegexOptions.Multiline);

        generalized = Regex.Replace(
            generalized,
            @"services\.Add(?<lifetime>Scoped|Transient|Singleton)<I[A-Za-z0-9_]+Service,\s*[A-Za-z0-9_]+Service>\(\);\s*",
            match => $"{{{{ for entity in Entities }}}}services.Add{match.Groups["lifetime"].Value}<{{{{ entity.ServiceInterfaceName }}}}, {{{{ entity.ServiceImplementationName }}}}>();{Environment.NewLine}{{{{ end }}}}{Environment.NewLine}");

        generalized = Regex.Replace(
            generalized,
            @"services\.Add(?<lifetime>Scoped|Transient|Singleton)<I[A-Za-z0-9_]+Repository,\s*[A-Za-z0-9_]+Repository>\(\);\s*",
            match => $"{{{{ for entity in Entities }}}}services.Add{match.Groups["lifetime"].Value}<{{{{ entity.RepositoryInterfaceName }}}}, {{{{ entity.RepositoryImplementationName }}}}>();{Environment.NewLine}{{{{ end }}}}{Environment.NewLine}");

        generalized = generalized.TrimEnd() + Environment.NewLine;

        if (generalized.Contains("{{ entity.ServiceInterfaceName }}", StringComparison.Ordinal))
        {
            generalized = $"{{{{ for entity in Entities }}}}using {{{{ entity.ServiceImplementationNamespace }}}};{Environment.NewLine}{{{{ end }}}}{Environment.NewLine}{generalized}";
        }

        if (generalized.Contains("{{ entity.RepositoryInterfaceName }}", StringComparison.Ordinal))
        {
            generalized = $"{{{{ for entity in Entities }}}}using {{{{ entity.RepositoryInterfaceNamespace }}}};{Environment.NewLine}using {{{{ entity.RepositoryImplementationNamespace }}}};{Environment.NewLine}{{{{ end }}}}{Environment.NewLine}{generalized}";
        }

        generalized = Regex.Replace(generalized, @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine);
        return generalized;
    }

    private static string GeneralizeStartupTemplate(string content)
    {
        var generalized = content;

        generalized = Regex.Replace(
            generalized,
            @"services\.Add(?<lifetime>Scoped|Transient|Singleton)<I[A-Za-z0-9_]+Service,\s*[A-Za-z0-9_]+Service>\(\);\s*",
            match => $"{{{{ for entity in Entities }}}}services.Add{match.Groups["lifetime"].Value}<{{{{ entity.ServiceInterfaceName }}}}, {{{{ entity.ServiceImplementationName }}}}>();{Environment.NewLine}{{{{ end }}}}{Environment.NewLine}");

        generalized = Regex.Replace(
            generalized,
            @"services\.Add(?<lifetime>Scoped|Transient|Singleton)<I[A-Za-z0-9_]+Repository,\s*[A-Za-z0-9_]+Repository>\(\);\s*",
            match => $"{{{{ for entity in Entities }}}}services.Add{match.Groups["lifetime"].Value}<{{{{ entity.RepositoryInterfaceName }}}}, {{{{ entity.RepositoryImplementationName }}}}>();{Environment.NewLine}{{{{ end }}}}{Environment.NewLine}");

        return generalized;
    }

    // The sample's key name (taken from the created-result access) is entity specific, so every member
    // access to it must become a token; otherwise other entities inherit the sample's key property.
    private static string GeneralizePrimaryKeyAccess(string template)
    {
        var keyMatch = Regex.Match(template, @"result\.(?<property>[A-Za-z_][A-Za-z0-9_]*)");
        if (!keyMatch.Success)
        {
            return template;
        }

        var keyName = keyMatch.Groups["property"].Value;
        return Regex.Replace(template, $@"(?<=\b(?:result|request|entity|item|existing|created|updated)\.){Regex.Escape(keyName)}\b", "{{ PrimaryKeyName }}");
    }

    // Learned samples address records by a single "id"; tokens let the same template serve composite keys.
    private static string GeneralizeKeyParameters(string template)
    {
        var generalized = template.Replace("{{ PrimaryKeyType }} id", "{{ KeyParameters }}", StringComparison.Ordinal);
        generalized = Regex.Replace(
            generalized,
            @"^(?<indent>[ \t]*)/// <param name=""id"">.*</param>\r?\n",
            "{{ for key in Keys }}${indent}/// <param name=\"{{ key.ParameterName }}\">{{ key.PropertyName }} of the record.</param>\n{{ end }}",
            RegexOptions.Multiline);
        generalized = generalized.Replace("\"{id}\"", "\"{{ KeyRouteTemplate }}\"", StringComparison.Ordinal);
        generalized = generalized.Replace("\"/{id}\"", "\"/{{ KeyRouteTemplate }}\"", StringComparison.Ordinal);
        generalized = generalized.Replace("new { id = result.{{ PrimaryKeyName }} }", "{{ KeyRouteValues }}", StringComparison.Ordinal);
        generalized = generalized.Replace("(request.{{ PrimaryKeyName }}, cancellationToken)", "({{ KeyArgumentsFromRequest }}, cancellationToken)", StringComparison.Ordinal);
        generalized = Regex.Replace(
            generalized,
            @"(?<indent>[ \t]*)request\.\{\{ PrimaryKeyName \}\} = id;",
            "{{ for key in Keys }}${indent}request.{{ key.PropertyName }} = {{ key.ParameterName }};\n{{ end }}");
        generalized = Regex.Replace(generalized, @"\(id,(?=\s*(request|cancellationToken)\b)", "({{ KeyArguments }},");
        generalized = generalized.Replace("{id}", "{{ KeyInterpolation }}", StringComparison.Ordinal);
        return generalized;
    }

    private static string ReplaceFirst(string input, string pattern, string replacement)
    {
        var match = Regex.Match(input, pattern);
        if (!match.Success)
        {
            return input;
        }

        return input[..match.Index] + replacement + input[(match.Index + match.Length)..];
    }

    private static bool FileContains(string path, string value)
    {
        try
        {
            return File.ReadAllText(path).Contains(value, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private sealed class LearnedSampleAssets
    {
        public Dictionary<string, string> TemplateOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<LearnedFileSample> SharedFiles { get; } = [];

        public string? GetTemplate(string key) =>
            TemplateOverrides.TryGetValue(key, out var template) ? template : null;
    }

    private sealed class LearnedFileSample
    {
        public required string Path { get; init; }
        public required string Description { get; init; }
        public required string Content { get; init; }
    }

    private sealed record LearnedProjectCatalogEntry(
        string ProjectName,
        string ProjectFilePath,
        string BasePath,
        string Role);
}


