namespace ApiGenerator.Cli.Analyzers;

public sealed class StandardProfile
{
    public string ProfileKind { get; init; } = "Standard";
    public string SchemaVersion { get; init; } = "2.1";
    public string ProfileName { get; init; } = "Default Standard";
    public string? Description { get; init; }
    public string? SourceProjectPath { get; init; }
    public string ArchitectureStyle { get; init; } = "CleanArchitecture";
    public FrameworkStandard Framework { get; init; } = new();
    public Dictionary<string, string> ProjectBasePaths { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Folders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> NamingRules { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> Patterns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public ControllerStandard Controller { get; init; } = new();
    public LoggingStandard Logging { get; init; } = new();
    public Dictionary<string, string> TemplateOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<SharedFileTemplate> SharedFiles { get; init; } = [];
    public IReadOnlyList<AdditionalProjectTemplate> AdditionalProjects { get; init; } = [];
    public LlmStandard Llm { get; init; } = new();

    public static StandardProfile CreateDefault() =>
        new()
        {
            ProfileKind = "Standard",
            SchemaVersion = "2.1",
            ProfileName = "Default Standard",
            Description = "Built-in defaults used when no company profile is selected.",
            ArchitectureStyle = "CleanArchitecture",
            Framework = new FrameworkStandard
            {
                Id = "aspnet-webapi-swagger",
                DisplayName = "ASP.NET Core Web API + Swagger",
                ApiStyle = "controller",
                ProjectLayout = "layered",
                UseControllers = true,
                UseSwagger = true,
                IncludeUnitTests = true,
                UseServiceLayer = true,
                UseContractModels = true,
                DatabaseProvider = "none",
                UseWindowsAuthentication = false,
                UseAppSettings = false,
                GeneratePostmanCollection = false,
                ConnectionStringName = "DefaultConnection",
                DefaultConnectionString = "Server=.;Database={{ SolutionName }}Db;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True",
                SupportsFrameworkSelection = true
            },
            Folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["controllers"] = "Controllers",
                ["endpoints"] = "Endpoints",
                ["data"] = "Data",
                ["services"] = "Services",
                ["repositories"] = "Repositories",
                ["entities"] = "Entities",
                ["dtos"] = "DTOs",
                ["requests"] = "Requests",
                ["responses"] = "Responses",
                ["tests"] = string.Empty
            },
            ProjectBasePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api"] = "src/{{ SolutionName }}.Api",
                ["application"] = "src/{{ SolutionName }}.Application",
                ["infrastructure"] = "src/{{ SolutionName }}.Infrastructure",
                ["domain"] = "src/{{ SolutionName }}.Domain",
                ["tests"] = "tests/{{ SolutionName }}.UnitTests"
            },
            NamingRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["serviceInterface"] = "I{Entity}Service",
                ["serviceImplementation"] = "{Entity}Service",
                ["repositoryInterface"] = "I{Entity}Repository",
                ["repositoryImplementation"] = "{Entity}Repository",
                ["controller"] = "{Entity}Controller",
                ["endpointModule"] = "{Entity}Endpoints",
                ["entity"] = "{Entity}",
                ["dto"] = "{Entity}Dto",
                ["createRequest"] = "Create{Entity}Request",
                ["updateRequest"] = "Update{Entity}Request",
                ["response"] = "{Entity}Response",
                ["test"] = "{Entity}ServiceTests"
            },
            Patterns = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["usesUnitOfWork"] = false,
                ["usesBaseEntity"] = false,
                ["usesLogHelper"] = false,
                ["usesControllers"] = true,
                ["usesSwagger"] = true,
                ["usesEndpointModules"] = false
            },
            Controller = new ControllerStandard
            {
                BaseClass = "ControllerBase",
                RouteTemplate = "api/[controller]",
                UseApiControllerAttribute = true
            },
            Logging = new LoggingStandard
            {
                Enabled = false
            },
            Llm = LlmStandard.CreateDefault()
        };
}

public sealed class FrameworkStandard
{
    public string Id { get; init; } = "aspnet-webapi-swagger";
    public string DisplayName { get; init; } = "ASP.NET Core Web API + Swagger";
    public string ApiStyle { get; init; } = "controller";
    public string ProjectLayout { get; init; } = "layered";
    public bool UseControllers { get; init; } = true;
    public bool UseSwagger { get; init; } = true;
    public bool IncludeUnitTests { get; init; } = true;
    public bool UseServiceLayer { get; init; } = true;
    public bool UseContractModels { get; init; } = true;
    public string DatabaseProvider { get; init; } = "none";
    public bool UseWindowsAuthentication { get; init; }
    public bool UseAppSettings { get; init; }
    public bool GeneratePostmanCollection { get; init; }
    public string ConnectionStringName { get; init; } = "DefaultConnection";
    public string DefaultConnectionString { get; init; } = "Server=.;Database={{ SolutionName }}Db;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
    public bool SupportsFrameworkSelection { get; init; } = true;
}

public sealed class ControllerStandard
{
    public string BaseClass { get; init; } = "ControllerBase";
    public string RouteTemplate { get; init; } = "api/[controller]";
    public bool UseApiControllerAttribute { get; init; } = true;
}

public sealed class LoggingStandard
{
    public bool Enabled { get; init; }
    public string? HelperClassName { get; init; }
    public string? HelperNamespace { get; init; }
    public string? HelperPath { get; init; }
    public string? ConstructorParameterType { get; init; }
    public string? ConstructorParameterName { get; init; }
    public string? FieldType { get; init; }
    public string? FieldName { get; init; }
}

public sealed class SharedFileTemplate
{
    public string RelativePath { get; init; } = string.Empty;
    public string Category { get; init; } = "support";
    public string Description { get; init; } = string.Empty;
    public string Template { get; init; } = string.Empty;
}

public sealed class AdditionalProjectTemplate
{
    public string RelativePath { get; init; } = string.Empty;
    public string Category { get; init; } = "project";
    public string Template { get; init; } = string.Empty;
}

public sealed class LlmStandard
{
    public bool Enabled { get; init; }
    public string Mode { get; init; } = "stateless";
    public IReadOnlyList<string> ApplyToArtifacts { get; init; } = [];
    public IReadOnlyList<string> Instructions { get; init; } = [];
    public IReadOnlyList<string> ContextAssets { get; init; } = [];

    public static LlmStandard CreateDefault() =>
        new()
        {
            Enabled = false,
            Mode = "stateless",
            ApplyToArtifacts =
            [
                "apiProgram",
                "controller",
                "endpointModule",
                "entity",
                "dto",
                "createRequest",
                "updateRequest",
                "response",
                "serviceInterface",
                "serviceImplementation",
                "repositoryInterface",
                "repositoryImplementation",
                "dbContext",
                "launchSettings",
                "unitTests",
                "sharedFiles"
            ],
            Instructions =
            [
                "Use only the current file, the current profile, and any provided example assets.",
                "Do not rely on prior conversation state or hidden memory.",
                "Preserve the naming, namespace, logging, and controller conventions defined in the profile.",
                "Return the full revised file content without markdown fences."
            ],
            ContextAssets = []
        };
}
