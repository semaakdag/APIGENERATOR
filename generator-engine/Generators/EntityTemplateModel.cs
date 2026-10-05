using ApiGenerator.Cli.Analyzers;

namespace ApiGenerator.Cli.Generators;

public sealed class EntityTemplateModel
{
    public required string SolutionName { get; init; }
    public required ProjectLayoutContext Layout { get; init; }
    public required string EntityName { get; init; }
    public required string PluralName { get; init; }
    public required string EntityTypeName { get; init; }
    public required string PrimaryKeyName { get; init; }
    public required string PrimaryKeyType { get; init; }
    public string TableName { get; init; } = string.Empty;
    public string? SchemaName { get; init; }
    public string KeyExpression { get; init; } = string.Empty;
    public required string DtoName { get; init; }
    public required string CreateRequestName { get; init; }
    public required string UpdateRequestName { get; init; }
    public required string ResponseName { get; init; }
    public required string ServiceInterfaceName { get; init; }
    public required string ServiceImplementationName { get; init; }
    public required string RepositoryInterfaceName { get; init; }
    public required string RepositoryImplementationName { get; init; }
    public required string ControllerName { get; init; }
    public required string EndpointModuleName { get; init; }
    public required string TestClassName { get; init; }
    public required string EntityNamespace { get; init; }
    public required string DtoNamespace { get; init; }
    public required string CreateRequestNamespace { get; init; }
    public required string UpdateRequestNamespace { get; init; }
    public required string ResponseNamespace { get; init; }
    public required string ServiceInterfaceNamespace { get; init; }
    public required string ServiceImplementationNamespace { get; init; }
    public required string RepositoryInterfaceNamespace { get; init; }
    public required string RepositoryImplementationNamespace { get; init; }
    public required string ControllerNamespace { get; init; }
    public required string EndpointNamespace { get; init; }
    public required string ApiDataNamespace { get; init; }
    public required IReadOnlyList<EntityPropertyModel> Properties { get; init; }
    public required StandardProfile Profile { get; init; }
}

public sealed class EntityPropertyModel
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool Required { get; init; }
    public bool IsPrimaryKey { get; init; }
    public string ColumnName { get; init; } = string.Empty;
    public bool IsRowVersion { get; init; }
    public string StoreType { get; init; } = string.Empty;
    public bool IsKeyWithoutIdentity { get; init; }
}

public sealed class SolutionTemplateModel
{
    public required string SolutionName { get; init; }
    public required IReadOnlyList<EntityTemplateModel> Entities { get; init; }
    public required StandardProfile Profile { get; init; }
    public required ProjectLayoutContext Layout { get; init; }
    public required string ApiProjectName { get; init; }
    public required string ApiProjectPath { get; init; }
    public required string ApplicationProjectName { get; init; }
    public required string ApplicationProjectPath { get; init; }
    public required string InfrastructureProjectName { get; init; }
    public required string InfrastructureProjectPath { get; init; }
    public required string DomainProjectName { get; init; }
    public required string DomainProjectPath { get; init; }
    public required string TestsProjectName { get; init; }
    public required string TestsProjectPath { get; init; }
    public string ApiToApplicationProjectReference { get; init; } = string.Empty;
    public string ApiToInfrastructureProjectReference { get; init; } = string.Empty;
    public string ApplicationToDomainProjectReference { get; init; } = string.Empty;
    public string InfrastructureToApplicationProjectReference { get; init; } = string.Empty;
    public string InfrastructureToDomainProjectReference { get; init; } = string.Empty;
    public string TestsToApiProjectReference { get; init; } = string.Empty;
    public string TestsToApplicationProjectReference { get; init; } = string.Empty;
    public required string ConnectionStringName { get; init; }
    public required string ConnectionString { get; init; }
    public required string JsonEscapedConnectionString { get; init; }
    public string ApiDataNamespace { get; init; } = string.Empty;
    public IReadOnlyList<string> ApiAdditionalProjectReferences { get; init; } = [];
    public IReadOnlyList<string> ApplicationAdditionalProjectReferences { get; init; } = [];
    public IReadOnlyList<string> InfrastructureAdditionalProjectReferences { get; init; } = [];
    public IReadOnlyList<string> DomainAdditionalProjectReferences { get; init; } = [];
    public IReadOnlyList<string> TestAdditionalProjectReferences { get; init; } = [];
    public int HttpPort { get; init; }
    public int HttpsPort { get; init; }
}

public sealed class ProjectLayoutContext
{
    public required string SolutionName { get; init; }
    public required string OutputRootPath { get; init; }
    public required string ApiProjectName { get; init; }
    public required string ApiBasePath { get; init; }
    public required string ApplicationProjectName { get; init; }
    public required string ApplicationBasePath { get; init; }
    public required string InfrastructureProjectName { get; init; }
    public required string InfrastructureBasePath { get; init; }
    public required string DomainProjectName { get; init; }
    public required string DomainBasePath { get; init; }
    public required string TestsProjectName { get; init; }
    public required string TestsBasePath { get; init; }
    public IReadOnlyDictionary<string, string> AdditionalProjectBasePaths { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool ApplyInPlace { get; init; }
}
