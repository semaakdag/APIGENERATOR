using System.Text;
using System.Text.RegularExpressions;
using ApiGenerator.Cli.Commands;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ApiGenerator.Cli.Recipes;

public sealed class AddEndpointRequest
{
    public required string ProjectPath { get; init; }
    public required string Entity { get; init; }
    public required string Recipe { get; init; }
    public string? Field { get; init; }
    public bool DryRun { get; init; }
}

public sealed class AddEndpointResult
{
    public required EndpointRecord Endpoint { get; init; }
    public IReadOnlyList<string> CreatedFiles { get; init; } = [];
    public IReadOnlyList<string> UpdatedFiles { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool AlreadyExisted { get; init; }
}

/// <summary>
/// Adds a recipe endpoint to a generated solution. Every touched type gets a new partial file next to its original
/// declaration instead of editing method bodies; originals only gain the <c>partial</c> modifier when missing.
/// </summary>
public sealed class EndpointRecipeGenerator
{
    public AddEndpointResult Add(AddEndpointRequest request)
    {
        var root = Path.GetFullPath(request.ProjectPath);
        if (!Directory.Exists(root))
        {
            throw new CliInputException($"Solution folder '{request.ProjectPath}' was not found.");
        }

        var kind = RecipeRules.Parse(request.Recipe);
        var index = ProjectCodeIndex.Load(root);
        var context = RecipeContext.Resolve(index, request.Entity);
        var field = RecipeRules.ResolveField(kind, request.Field, context.Properties, context.EntityTypeName);
        var method = RecipeRules.MethodName(kind, field?.Name);
        var parameter = field is null ? string.Empty : RecipeRules.Camel(field.Value.Name);
        var record = BuildRecord(context, kind, field, method, parameter);

        var records = EndpointStore.Load(root).ToList();
        var existing = records.FirstOrDefault(item => item.Entity == context.EntityName && item.Method == method);
        if (existing is not null && existing.Files.All(file => File.Exists(Path.Combine(root, file))))
        {
            return new AddEndpointResult { Endpoint = existing, AlreadyExisted = true };
        }

        EnsureNoCollision(context, method);

        var writes = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var created = new List<string>();
        var updated = new List<string>();
        var builder = new MemberBuilder(context, kind, field, method, parameter);

        if (kind != RecipeKind.BulkInsert)
        {
            AddPartial(writes, context.RepositoryInterface, method, builder.RepositoryInterfaceMembers());
            foreach (var implementation in context.RepositoryImplementations)
            {
                AddPartial(writes, implementation, method, builder.RepositoryImplementationMembers(implementation), builder.RepositoryImplementationUsings(implementation));
            }
        }

        if (context.ServiceInterface is not null)
        {
            AddPartial(writes, context.ServiceInterface, method, builder.ServiceInterfaceMembers());
            foreach (var implementation in context.ServiceImplementations)
            {
                AddPartial(writes, implementation, method, builder.ServiceImplementationMembers(implementation));
            }
        }

        if (context.Controller is not null)
        {
            AddPartial(writes, context.Controller, method, builder.ControllerMembers(), "using Microsoft.AspNetCore.Http;\nusing Microsoft.AspNetCore.Mvc;");
        }
        else
        {
            AddPartial(writes, context.EndpointModule!, method, builder.EndpointModuleMembers(), "using Microsoft.AspNetCore.Builder;\nusing Microsoft.AspNetCore.Http;\nusing Microsoft.AspNetCore.Routing;");
            RegisterEndpointModule(index, context, method, records.Where(item => item.Entity == context.EntityName).Select(item => item.Method).ToList(), writes, warnings);
        }

        var testFile = builder.TestFile(index, root);
        if (testFile is null)
        {
            warnings.Add("No unit test project was found; recipe tests were not generated.");
        }
        else
        {
            writes[testFile.Value.Path] = testFile.Value.Content;
        }

        foreach (var (path, content) in MakeOriginalsPartial(context))
        {
            writes[path] = content;
        }

        foreach (var documentation in Directory.EnumerateFiles(root, "API-DOCUMENTATION.md", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(documentation);
            var withRow = InsertDocumentationRow(text, context.EntityTypeName, record);
            if (withRow != text)
            {
                writes[documentation] = withRow;
            }
        }

        foreach (var (path, content) in writes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            (File.Exists(path) ? updated : created).Add(relative);
            if (!request.DryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }
        }

        var stored = new EndpointRecord
        {
            Entity = record.Entity,
            Recipe = record.Recipe,
            Field = record.Field,
            Method = record.Method,
            HttpMethod = record.HttpMethod,
            Route = record.Route,
            RequestBody = record.RequestBody,
            Response = record.Response,
            Files = created.Where(file => file.EndsWith(".cs", StringComparison.Ordinal)).ToList()
        };

        if (!request.DryRun)
        {
            records.RemoveAll(item => item.Entity == stored.Entity && item.Method == stored.Method);
            records.Add(stored);
            EndpointStore.Save(root, records);
        }

        return new AddEndpointResult { Endpoint = stored, CreatedFiles = created, UpdatedFiles = updated, Warnings = warnings };
    }

    private static EndpointRecord BuildRecord(RecipeContext context, RecipeKind kind, (string Name, string Type)? field, string method, string parameter)
    {
        var relative = RecipeRules.RelativeRoute(kind, field?.Name, parameter);
        var responseName = ProjectCodeIndex.SimpleName(context.ResponseFullName);
        return new EndpointRecord
        {
            Entity = context.EntityName,
            Recipe = kind.ToString(),
            Field = field?.Name,
            Method = method,
            HttpMethod = kind == RecipeKind.BulkInsert ? "POST" : "GET",
            Route = $"{context.BaseRoute}/{relative}{RecipeRules.QuerySuffix(kind)}",
            RequestBody = kind == RecipeKind.BulkInsert ? $"List<{ProjectCodeIndex.SimpleName(context.CreateRequestFullName)}>" : "None",
            Response = kind == RecipeKind.GetByCode ? responseName : $"IReadOnlyList<{responseName}>"
        };
    }

    private static void EnsureNoCollision(RecipeContext context, string method)
    {
        var asyncName = method + "Async";
        var owners = new[] { context.RepositoryInterface }
            .Concat(context.RepositoryImplementations)
            .Concat(context.ServiceInterface is null ? [] : [context.ServiceInterface])
            .Concat(context.ServiceImplementations);
        var collision = owners.FirstOrDefault(type => type.HasMember(asyncName))?.Name
            ?? (context.Controller?.HasMember(method) == true ? context.Controller.Name : null);
        if (collision is not null)
        {
            throw new CliInputException($"'{collision}' already defines '{method}'. Remove it or choose another field.");
        }
    }

    private static void AddPartial(Dictionary<string, string> writes, TypeEntry target, string method, string members, string extraUsings = "")
    {
        var directory = Path.GetDirectoryName(target.File.Path)!;
        var path = Path.Combine(directory, $"{target.Name}.{method}.cs");
        var usings = MergeUsings(target.File.UsingBlock, extraUsings);
        var modifiers = string.Join(" ", target.Declaration.Modifiers.Select(modifier => modifier.Text).Where(text => text != "partial"));
        var keyword = target.Declaration.Keyword.Text;
        var content = new StringBuilder();
        if (usings.Length > 0)
        {
            content.AppendLine(usings).AppendLine();
        }

        if (target.File.Namespace.Length > 0)
        {
            content.AppendLine($"namespace {target.File.Namespace};").AppendLine();
        }

        content.AppendLine($"{modifiers} partial {keyword} {target.Name}".Trim());
        content.AppendLine("{");
        content.Append(members.TrimEnd()).AppendLine();
        content.AppendLine("}");
        writes[path] = content.ToString();
    }

    private static string MergeUsings(string existing, string extra)
    {
        var lines = existing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        foreach (var line in extra.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!lines.Contains(line))
            {
                lines.Add(line);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<(string Path, string Content)> MakeOriginalsPartial(RecipeContext context)
    {
        var targets = new[] { context.RepositoryInterface, context.Controller, context.EndpointModule, context.ServiceInterface }
            .Concat(context.RepositoryImplementations)
            .Concat(context.ServiceImplementations)
            .Where(type => type is not null && !type.IsPartial)
            .Cast<TypeEntry>()
            .GroupBy(type => type.File.Path);

        foreach (var group in targets)
        {
            var text = group.First().File.Text;
            foreach (var type in group.OrderByDescending(type => type.Declaration.Keyword.SpanStart))
            {
                var position = type.Declaration.Keyword.SpanStart;
                text = text[..position] + "partial " + text[position..];
            }

            yield return (group.Key, text);
        }
    }

    private static void RegisterEndpointModule(
        ProjectCodeIndex index,
        RecipeContext context,
        string method,
        IReadOnlyList<string> existingMethods,
        Dictionary<string, string> writes,
        List<string> warnings)
    {
        var registration = $"app.Map{context.EntityName}Endpoints();";
        var program = index.Files.FirstOrDefault(file => file.Text.Contains(registration, StringComparison.Ordinal));
        var call = $"app.Map{context.EntityName}{method}Endpoint();";
        if (program is null)
        {
            warnings.Add($"Could not find '{registration}'; add '{call}' to Program.cs.");
            return;
        }

        if (program.Text.Contains(call, StringComparison.Ordinal))
        {
            return;
        }

        // Append after the entity's last known registration so recipes stay in the order they were added.
        var knownCalls = new[] { registration }
            .Concat(existingMethods.Select(existingMethod => $"app.Map{context.EntityName}{existingMethod}Endpoint();"))
            .ToHashSet(StringComparer.Ordinal);
        var position = program.Text.Split('\n')
            .Select((line, lineIndex) => (Line: line.Trim(), Offset: program.Text.Split('\n').Take(lineIndex).Sum(previous => previous.Length + 1)))
            .Where(entry => knownCalls.Contains(entry.Line))
            .Select(entry => entry.Offset)
            .Max();
        var lineEnd = program.Text.IndexOf('\n', position);
        var insertAt = lineEnd < 0 ? program.Text.Length : lineEnd + 1;
        writes[program.Path] = program.Text[..insertAt] + call + Environment.NewLine + program.Text[insertAt..];
    }

    private static string InsertDocumentationRow(string text, string entityTypeName, EndpointRecord record)
    {
        if (text.Contains($"| {record.Method} |", StringComparison.Ordinal))
        {
            return text;
        }

        var header = Regex.Match(text, $@"^## {Regex.Escape(entityTypeName)}\r?$", RegexOptions.Multiline);
        if (!header.Success)
        {
            return text;
        }

        var deleteRow = Regex.Match(text[header.Index..], @"^\| Delete \|.*$", RegexOptions.Multiline);
        if (!deleteRow.Success)
        {
            return text;
        }

        var insertAt = header.Index + deleteRow.Index + deleteRow.Length;
        return text[..insertAt] + "\n" + record.DocumentationRow + text[insertAt..];
    }
}

/// <summary>What the existing code looks like for one entity: types, keys, field types and routes.</summary>
internal sealed class RecipeContext
{
    public required string EntityName { get; init; }
    public required string EntityTypeName { get; init; }
    public required string EntityFullName { get; init; }
    public required IReadOnlyDictionary<string, string> Properties { get; init; }
    public required TypeEntry RepositoryInterface { get; init; }
    public required IReadOnlyList<TypeEntry> RepositoryImplementations { get; init; }
    public TypeEntry? ServiceInterface { get; init; }
    public IReadOnlyList<TypeEntry> ServiceImplementations { get; init; } = [];
    public TypeEntry? Controller { get; init; }
    public TypeEntry? EndpointModule { get; init; }
    public required IReadOnlyList<(string Type, string Name)> KeyParameters { get; init; }
    public required IReadOnlyList<string> KeyProperties { get; init; }
    public required bool HasGeneratedKey { get; init; }
    public required string ResponseFullName { get; init; }
    public required string CreateRequestFullName { get; init; }
    public required string BaseRoute { get; init; }

    public static RecipeContext Resolve(ProjectCodeIndex index, string entity)
    {
        var repositoryInterface = index.FindInterface($"I{entity}Repository")
            ?? throw new CliInputException($"Could not find the repository interface 'I{entity}Repository' in the selected solution. Check the entity name.");
        var repositoryImplementations = index.ClassesImplementing(repositoryInterface.Name);
        if (repositoryImplementations.Count == 0)
        {
            throw new CliInputException($"No class implements '{repositoryInterface.Name}'.");
        }

        var getById = repositoryInterface.Method("GetByIdAsync")
            ?? throw new CliInputException($"'{repositoryInterface.Name}' has no GetByIdAsync method, so its key cannot be determined.");
        var entityText = TaskResultType(getById.ReturnType)
            ?? throw new CliInputException($"Could not read the entity type from '{repositoryInterface.Name}.GetByIdAsync'.");
        var entityFullName = index.ResolveFullName(entityText, repositoryInterface.File);
        var entityType = index.FindByFullName(entityFullName) ?? index.FindClass(ProjectCodeIndex.SimpleName(entityFullName))
            ?? throw new CliInputException($"Could not find the entity class '{entityFullName}'.");
        var properties = entityType.Declaration.Members.OfType<PropertyDeclarationSyntax>()
            .ToDictionary(property => property.Identifier.Text, property => property.Type.ToString(), StringComparer.Ordinal);

        var serviceInterface = index.FindInterface($"I{entity}Service");
        var serviceImplementations = serviceInterface is null ? [] : index.ClassesImplementing(serviceInterface.Name);
        var controller = index.FindClass($"{entity}Controller");
        var endpointModule = controller is null ? index.FindClass($"{entity}Endpoints") : null;
        if (controller is null && endpointModule is null)
        {
            throw new CliInputException($"Could not find '{entity}Controller' or '{entity}Endpoints' in the selected solution.");
        }

        if (endpointModule is not null && serviceInterface is null)
        {
            throw new CliInputException($"'{entity}Endpoints' needs a service interface 'I{entity}Service' to add recipe endpoints.");
        }

        var keyParameters = getById.ParameterList.Parameters
            .Where(parameter => parameter.Type?.ToString() != "CancellationToken")
            .Select(parameter => (parameter.Type!.ToString(), parameter.Identifier.Text))
            .ToList();
        var keyProperties = ResolveKeyProperties(controller ?? endpointModule!, keyParameters, properties, entity);

        var responseText = serviceInterface?.Method("GetByIdAsync") is { } serviceGetById ? TaskResultType(serviceGetById.ReturnType) : null;
        var responseFullName = responseText is null ? entityFullName : index.ResolveFullName(responseText, serviceInterface!.File);
        var createParameter = serviceInterface?.Method("CreateAsync")?.ParameterList.Parameters.FirstOrDefault();
        var createRequestFullName = createParameter?.Type is null ? entityFullName : index.ResolveFullName(createParameter.Type.ToString(), serviceInterface!.File);

        bool hasGeneratedKey;
        if (serviceInterface is not null)
        {
            var createRequest = index.FindByFullName(createRequestFullName);
            hasGeneratedKey = createRequest is not null &&
                              keyProperties.Count == 1 &&
                              !createRequest.Declaration.Members.OfType<PropertyDeclarationSyntax>().Any(property => property.Identifier.Text == keyProperties[0]);
        }
        else
        {
            hasGeneratedKey = keyProperties.Count == 1 &&
                              controller!.Declaration.ToString().Contains($"request.{keyProperties[0]} = default;", StringComparison.Ordinal);
        }

        return new RecipeContext
        {
            EntityName = entity,
            EntityTypeName = entityType.Name,
            EntityFullName = entityFullName,
            Properties = properties,
            RepositoryInterface = repositoryInterface,
            RepositoryImplementations = repositoryImplementations,
            ServiceInterface = serviceInterface,
            ServiceImplementations = serviceImplementations,
            Controller = controller,
            EndpointModule = endpointModule,
            KeyParameters = keyParameters,
            KeyProperties = keyProperties,
            HasGeneratedKey = hasGeneratedKey,
            ResponseFullName = responseFullName,
            CreateRequestFullName = createRequestFullName,
            BaseRoute = ResolveBaseRoute(controller, endpointModule, entity)
        };
    }

    private static string? TaskResultType(TypeSyntax returnType)
    {
        if (returnType is not GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic)
        {
            return null;
        }

        var argument = generic.TypeArgumentList.Arguments[0];
        return (argument is NullableTypeSyntax nullable ? nullable.ElementType : argument).ToString();
    }

    // The created-at route values ("new { id = result.Id }") name the key property behind each key parameter.
    private static IReadOnlyList<string> ResolveKeyProperties(
        TypeEntry api,
        IReadOnlyList<(string Type, string Name)> keyParameters,
        IReadOnlyDictionary<string, string> properties,
        string entity)
    {
        var routeValues = api.Parts.SelectMany(part => part.Declaration.DescendantNodes()).OfType<AnonymousObjectCreationExpressionSyntax>()
            .FirstOrDefault(node => node.Initializers.Any(initializer => initializer.Expression.ToString().StartsWith("result.", StringComparison.Ordinal)));
        if (routeValues is not null)
        {
            var mapped = keyParameters
                .Select(parameter => routeValues.Initializers
                    .FirstOrDefault(initializer => initializer.NameEquals?.Name.Identifier.Text == parameter.Name)?.Expression.ToString())
                .ToList();
            if (mapped.All(value => value is not null && value.StartsWith("result.", StringComparison.Ordinal)))
            {
                return mapped.Select(value => value!["result.".Length..]).ToList();
            }
        }

        var createdRoute = Regex.Matches(string.Join("\n", api.Parts.Select(part => part.Declaration.ToString())), @"\{result\.(?<property>[A-Za-z_][A-Za-z0-9_]*)\}");
        if (createdRoute.Count == keyParameters.Count && createdRoute.Count > 0)
        {
            return createdRoute.Select(match => match.Groups["property"].Value).ToList();
        }

        if (keyParameters.Count == 1)
        {
            var candidate = properties.Keys.FirstOrDefault(name =>
                name.Equals(keyParameters[0].Name, StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                name.Equals($"{entity}Id", StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
            {
                return [candidate];
            }
        }

        throw new CliInputException($"Could not determine the key properties of '{entity}'.");
    }

    private static string ResolveBaseRoute(TypeEntry? controller, TypeEntry? endpointModule, string entity)
    {
        if (controller is not null)
        {
            var route = controller.Declaration.AttributeLists.SelectMany(list => list.Attributes)
                .FirstOrDefault(attribute => ProjectCodeIndex.SimpleName(attribute.Name.ToString()) is "Route" or "RouteAttribute");
            var template = route?.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
                ? literal.Token.ValueText
                : "api/[controller]";
            var controllerSegment = controller.Name.EndsWith("Controller", StringComparison.Ordinal) ? controller.Name[..^"Controller".Length] : controller.Name;
            return "/" + template.Replace("[controller]", controllerSegment, StringComparison.OrdinalIgnoreCase).Trim('/');
        }

        var group = Regex.Match(endpointModule!.Declaration.ToString(), @"MapGroup\(\s*""(?<route>[^""]+)""");
        return group.Success ? "/" + group.Groups["route"].Value.Trim('/') : $"/api/{entity}";
    }
}

/// <summary>C# members for each layer of one recipe.</summary>
internal sealed class MemberBuilder
{
    private readonly RecipeContext context;
    private readonly RecipeKind kind;
    private readonly (string Name, string Type)? field;
    private readonly string method;
    private readonly string parameter;

    public MemberBuilder(RecipeContext context, RecipeKind kind, (string Name, string Type)? field, string method, string parameter)
    {
        this.context = context;
        this.kind = kind;
        this.field = field;
        this.method = method;
        this.parameter = parameter;
    }

    private string Entity => $"global::{context.EntityFullName}";
    private string Response => $"global::{context.ResponseFullName}";
    private string CreateRequest => $"global::{context.CreateRequestFullName}";
    private string FieldName => field!.Value.Name;
    private string FieldType => field!.Value.Type.TrimEnd('?');
    private string Relative => RecipeRules.RelativeRoute(kind, field?.Name, parameter);

    private string Parameters(bool forController = false) => kind switch
    {
        RecipeKind.GetByCode => $"{FieldType} {parameter}, ",
        RecipeKind.Search => forController ? "[FromQuery] string? term, " : "string term, ",
        RecipeKind.GetByDateRange => forController ? $"[FromQuery] {FieldType} from, [FromQuery] {FieldType} to, " : $"{FieldType} from, {FieldType} to, ",
        _ => string.Empty
    };

    private string Arguments => kind switch
    {
        RecipeKind.GetByCode => $"{parameter}, ",
        RecipeKind.Search => "term, ",
        RecipeKind.GetByDateRange => "from, to, ",
        _ => string.Empty
    };

    public string RepositoryInterfaceMembers() => kind == RecipeKind.GetByCode
        ? $"    Task<{Entity}?> {method}Async({Parameters()}CancellationToken cancellationToken = default);"
        : $"    Task<IReadOnlyList<{Entity}>> {method}Async({Parameters()}CancellationToken cancellationToken = default);";

    public string RepositoryImplementationUsings(TypeEntry implementation) =>
        FindDbContextField(implementation) is not null ? "using Microsoft.EntityFrameworkCore;" : string.Empty;

    public string RepositoryImplementationMembers(TypeEntry implementation)
    {
        var dbContext = FindDbContextField(implementation);
        if (dbContext is not null)
        {
            var query = $"{dbContext}.Set<{Entity}>().AsNoTracking()";
            return kind switch
            {
                RecipeKind.GetByCode =>
                    $"    public async Task<{Entity}?> {method}Async({Parameters()}CancellationToken cancellationToken = default) =>\n" +
                    $"        await {query}.FirstOrDefaultAsync(item => item.{FieldName} == {parameter}, cancellationToken);",
                _ =>
                    $"    public async Task<IReadOnlyList<{Entity}>> {method}Async({Parameters()}CancellationToken cancellationToken = default) =>\n" +
                    $"        await {query}.Where(item => {Predicate(efCore: true)}).ToListAsync(cancellationToken);"
            };
        }

        var items = implementation.Declaration.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(member => member.Declaration.Type.ToString().StartsWith("List<", StringComparison.Ordinal))
            ?.Declaration.Variables.First().Identifier.Text
            ?? throw new CliInputException($"'{implementation.Name}' is neither an EF Core repository nor a List-backed repository; add '{method}Async' to it by hand.");
        var sync = implementation.Declaration.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(member => member.Declaration.Type.ToString() == "object")
            ?.Declaration.Variables.First().Identifier.Text;
        var body = kind == RecipeKind.GetByCode
            ? $"return Task.FromResult<{Entity}?>({items}.FirstOrDefault(item => Equals(item.{FieldName}, {parameter})));"
            : $"return Task.FromResult<IReadOnlyList<{Entity}>>({items}.Where(item => {Predicate(efCore: false)}).ToList());";
        var returnType = kind == RecipeKind.GetByCode ? $"Task<{Entity}?>" : $"Task<IReadOnlyList<{Entity}>>";
        var guarded = sync is null ? $"        {body}" : $"        lock ({sync})\n        {{\n            {body}\n        }}";
        return $"    public {returnType} {method}Async({Parameters()}CancellationToken cancellationToken = default)\n    {{\n{guarded}\n    }}";
    }

    private string Predicate(bool efCore) => kind switch
    {
        RecipeKind.GetActiveList => $"item.{FieldName} == true",
        RecipeKind.Search => efCore
            ? $"item.{FieldName} != null && item.{FieldName}.Contains(term)"
            : $"item.{FieldName} != null && item.{FieldName}.Contains(term, StringComparison.OrdinalIgnoreCase)",
        RecipeKind.GetByDateRange => $"item.{FieldName} >= from && item.{FieldName} <= to",
        _ => "true"
    };

    private static string? FindDbContextField(TypeEntry implementation) =>
        implementation.Declaration.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(member => member.Declaration.Type.ToString().EndsWith("Context", StringComparison.Ordinal))
            ?.Declaration.Variables.First().Identifier.Text;

    public string ServiceInterfaceMembers() => kind switch
    {
        RecipeKind.GetByCode => $"    Task<{Response}?> {method}Async({Parameters()}CancellationToken cancellationToken = default);",
        RecipeKind.BulkInsert => $"    /// <summary>Creates every record; returns null without inserting when any key already exists.</summary>\n    Task<IReadOnlyList<{Response}>?> BulkInsertAsync(IReadOnlyList<{CreateRequest}> requests, CancellationToken cancellationToken = default);",
        _ => $"    Task<IReadOnlyList<{Response}>> {method}Async({Parameters()}CancellationToken cancellationToken = default);"
    };

    public string ServiceImplementationMembers(TypeEntry implementation)
    {
        var repository = FieldOfType(implementation, context.RepositoryInterface.Name);
        if (kind != RecipeKind.BulkInsert && implementation.Method("Map") is null)
        {
            throw new CliInputException($"'{implementation.Name}' has no Map method to convert entities to responses.");
        }

        return kind switch
        {
            RecipeKind.GetByCode =>
                $"    public async Task<{Response}?> {method}Async({Parameters()}CancellationToken cancellationToken = default)\n    {{\n" +
                (FieldType == "string" ? $"        ArgumentException.ThrowIfNullOrWhiteSpace({parameter});\n" : string.Empty) +
                $"        var entity = await {repository}.{method}Async({Arguments}cancellationToken);\n" +
                "        return entity is null ? null : Map(entity);\n    }",
            RecipeKind.BulkInsert => BulkInsertService(repository),
            _ =>
                $"    public async Task<IReadOnlyList<{Response}>> {method}Async({Parameters()}CancellationToken cancellationToken = default)\n    {{\n" +
                Validation(throwing: true) +
                $"        var entities = await {repository}.{method}Async({Arguments}cancellationToken);\n" +
                "        return entities.Select(Map).ToList();\n    }"
        };
    }

    private string BulkInsertService(string repository)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"    public async Task<IReadOnlyList<{Response}>?> BulkInsertAsync(IReadOnlyList<{CreateRequest}> requests, CancellationToken cancellationToken = default)");
        builder.AppendLine("    {");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(requests);");
        builder.AppendLine("        if (requests.Count == 0)");
        builder.AppendLine("        {");
        builder.AppendLine("            throw new ArgumentException(\"At least one record is required.\", nameof(requests));");
        builder.AppendLine("        }");
        builder.AppendLine();
        AppendKeyConflictChecks(builder, repository, "return null;");
        builder.AppendLine($"        var created = new List<{Response}>();");
        builder.AppendLine("        foreach (var request in requests)");
        builder.AppendLine("        {");
        builder.AppendLine("            var result = await CreateAsync(request, cancellationToken);");
        builder.AppendLine("            if (result is null)");
        builder.AppendLine("            {");
        builder.AppendLine("                return null;");
        builder.AppendLine("            }");
        builder.AppendLine();
        builder.AppendLine("            created.Add(result);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return created;");
        builder.Append("    }");
        return builder.ToString();
    }

    private void AppendKeyConflictChecks(StringBuilder builder, string repository, string onConflict)
    {
        if (context.HasGeneratedKey)
        {
            return;
        }

        var keyTuple = context.KeyProperties.Count == 1
            ? $"request.{context.KeyProperties[0]}"
            : $"({string.Join(", ", context.KeyProperties.Select(key => $"request.{key}"))})";
        var keyArguments = string.Join(", ", context.KeyProperties.Select(key => $"request.{key}"));
        builder.AppendLine($"        var keys = requests.Select(request => {keyTuple}).ToList();");
        builder.AppendLine("        if (keys.Distinct().Count() != keys.Count)");
        builder.AppendLine("        {");
        builder.AppendLine($"            {onConflict}");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        foreach (var request in requests)");
        builder.AppendLine("        {");
        builder.AppendLine($"            if (await {repository}.GetByIdAsync({keyArguments}, cancellationToken) is not null)");
        builder.AppendLine("            {");
        builder.AppendLine($"                {onConflict}");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine();
    }

    private string Validation(bool throwing) => kind switch
    {
        RecipeKind.Search => throwing
            ? "        ArgumentException.ThrowIfNullOrWhiteSpace(term);\n"
            : "        if (string.IsNullOrWhiteSpace(term))\n        {\n            return BadRequest(\"The 'term' query parameter is required.\");\n        }\n\n",
        RecipeKind.GetByDateRange => throwing
            ? "        if (from > to)\n        {\n            throw new ArgumentException(\"'from' must not be later than 'to'.\", nameof(from));\n        }\n\n"
            : "        if (from > to)\n        {\n            return BadRequest(\"'from' must not be later than 'to'.\");\n        }\n\n",
        _ => string.Empty
    };

    private static string FieldOfType(TypeEntry type, string fieldTypeName) =>
        type.Parts.SelectMany(part => part.Declaration.Members).OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(member => ProjectCodeIndex.SimpleName(member.Declaration.Type.ToString()) == fieldTypeName)
            ?.Declaration.Variables.First().Identifier.Text
        ?? throw new CliInputException($"'{type.Name}' has no field of type '{fieldTypeName}'.");

    public string ControllerMembers()
    {
        var controller = context.Controller!;
        var usesService = context.ServiceInterface is not null && controller.Parts.SelectMany(part => part.Declaration.Members).OfType<FieldDeclarationSyntax>()
            .Any(member => ProjectCodeIndex.SimpleName(member.Declaration.Type.ToString()) == context.ServiceInterface.Name);
        var dependency = usesService
            ? FieldOfType(controller, context.ServiceInterface!.Name)
            : FieldOfType(controller, context.RepositoryInterface.Name);
        var resultType = usesService ? Response : Entity;
        var builder = new StringBuilder();

        builder.AppendLine($"    /// <summary>{Summary()}</summary>");
        builder.AppendLine(kind == RecipeKind.BulkInsert ? $"    [HttpPost(\"{Relative}\")]" : $"    [HttpGet(\"{Relative}\")]");
        switch (kind)
        {
            case RecipeKind.GetByCode:
                builder.AppendLine($"    [ProducesResponseType(typeof({resultType}), StatusCodes.Status200OK)]");
                builder.AppendLine("    [ProducesResponseType(StatusCodes.Status404NotFound)]");
                builder.AppendLine($"    public async Task<IActionResult> {method}({Parameters(forController: true)}CancellationToken cancellationToken)");
                builder.AppendLine("    {");
                builder.AppendLine($"        var result = await {dependency}.{method}Async({Arguments}cancellationToken);");
                builder.AppendLine("        return result is null ? NotFound() : Ok(result);");
                builder.Append("    }");
                break;
            case RecipeKind.BulkInsert:
                var bodyType = usesService ? CreateRequest : Entity;
                builder.AppendLine($"    [ProducesResponseType(typeof(IReadOnlyList<{resultType}>), StatusCodes.Status201Created)]");
                builder.AppendLine("    [ProducesResponseType(StatusCodes.Status400BadRequest)]");
                builder.AppendLine("    [ProducesResponseType(StatusCodes.Status409Conflict)]");
                builder.AppendLine($"    public async Task<IActionResult> BulkInsert([FromBody] List<{bodyType}> requests, CancellationToken cancellationToken)");
                builder.AppendLine("    {");
                builder.AppendLine("        if (requests is null || requests.Count == 0)");
                builder.AppendLine("        {");
                builder.AppendLine("            return BadRequest(\"At least one record is required.\");");
                builder.AppendLine("        }");
                builder.AppendLine();
                if (usesService)
                {
                    builder.AppendLine($"        var result = await {dependency}.BulkInsertAsync(requests, cancellationToken);");
                    builder.AppendLine("        return result is null ? Conflict() : StatusCode(StatusCodes.Status201Created, result);");
                }
                else
                {
                    if (context.HasGeneratedKey)
                    {
                        builder.AppendLine("        foreach (var request in requests)");
                        builder.AppendLine("        {");
                        builder.AppendLine($"            request.{context.KeyProperties[0]} = default;");
                        builder.AppendLine("        }");
                        builder.AppendLine();
                    }

                    AppendKeyConflictChecks(builder, dependency, "return Conflict();");
                    builder.AppendLine($"        var created = new List<{Entity}>();");
                    builder.AppendLine("        foreach (var request in requests)");
                    builder.AppendLine("        {");
                    builder.AppendLine($"            created.Add(await {dependency}.AddAsync(request, cancellationToken));");
                    builder.AppendLine("        }");
                    builder.AppendLine();
                    builder.AppendLine("        return StatusCode(StatusCodes.Status201Created, created);");
                }

                builder.Append("    }");
                break;
            default:
                builder.AppendLine($"    [ProducesResponseType(typeof(IReadOnlyList<{resultType}>), StatusCodes.Status200OK)]");
                if (kind is RecipeKind.Search or RecipeKind.GetByDateRange)
                {
                    builder.AppendLine("    [ProducesResponseType(StatusCodes.Status400BadRequest)]");
                }

                builder.AppendLine($"    public async Task<IActionResult> {method}({Parameters(forController: true)}CancellationToken cancellationToken)");
                builder.AppendLine("    {");
                builder.Append(Validation(throwing: false));
                builder.AppendLine($"        var result = await {dependency}.{method}Async({Arguments}cancellationToken);");
                builder.AppendLine("        return Ok(result);");
                builder.Append("    }");
                break;
        }

        return builder.ToString();
    }

    public string EndpointModuleMembers()
    {
        var service = $"global::{context.ServiceInterface!.FullName}";
        var route = $"{context.BaseRoute}/{Relative}";
        var lambdaParameters = kind switch
        {
            RecipeKind.GetByCode => $"{FieldType} {parameter}, ",
            RecipeKind.Search => "string? term, ",
            RecipeKind.GetByDateRange => $"{FieldType} from, {FieldType} to, ",
            RecipeKind.BulkInsert => $"List<{CreateRequest}> requests, ",
            _ => string.Empty
        };
        var body = kind switch
        {
            RecipeKind.GetByCode =>
                $"            var result = await service.{method}Async({Arguments}cancellationToken);\n            return result is null ? Results.NotFound() : Results.Ok(result);",
            RecipeKind.Search =>
                "            if (string.IsNullOrWhiteSpace(term))\n            {\n                return Results.BadRequest(\"The 'term' query parameter is required.\");\n            }\n\n" +
                $"            return Results.Ok(await service.{method}Async(term, cancellationToken));",
            RecipeKind.GetByDateRange =>
                "            if (from > to)\n            {\n                return Results.BadRequest(\"'from' must not be later than 'to'.\");\n            }\n\n" +
                $"            return Results.Ok(await service.{method}Async(from, to, cancellationToken));",
            RecipeKind.BulkInsert =>
                "            if (requests is null || requests.Count == 0)\n            {\n                return Results.BadRequest(\"At least one record is required.\");\n            }\n\n" +
                "            var result = await service.BulkInsertAsync(requests, cancellationToken);\n" +
                "            return result is null ? Results.Conflict() : Results.Json(result, statusCode: StatusCodes.Status201Created);",
            _ => $"            return Results.Ok(await service.{method}Async(cancellationToken));"
        };
        var verb = kind == RecipeKind.BulkInsert ? "MapPost" : "MapGet";
        return $"    /// <summary>{Summary()}</summary>\n" +
               $"    public static RouteHandlerBuilder Map{context.EntityName}{method}Endpoint(this IEndpointRouteBuilder app) =>\n" +
               $"        app.{verb}(\"{route}\", async ({lambdaParameters}{service} service, CancellationToken cancellationToken) =>\n        {{\n{body}\n        }})\n" +
               $"        .WithTags(\"{context.EntityName}\");";
    }

    private string Summary() => kind switch
    {
        RecipeKind.GetByCode => $"Returns the {context.EntityName} record whose {FieldName} matches.",
        RecipeKind.GetActiveList => $"Returns the {context.EntityName} records whose {FieldName} is true.",
        RecipeKind.Search => $"Returns the {context.EntityName} records whose {FieldName} contains the search term.",
        RecipeKind.GetByDateRange => $"Returns the {context.EntityName} records whose {FieldName} is between from and to (inclusive).",
        _ => $"Creates several {context.EntityName} records at once; no record is created when a key already exists."
    };

    public (string Path, string Content)? TestFile(ProjectCodeIndex index, string root)
    {
        var testProject = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .FirstOrDefault(path => File.ReadAllText(path).Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal));
        if (testProject is null)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(testProject)!;
        var testNamespace = index.Files.FirstOrDefault(file => file.Path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal) && file.Namespace.Length > 0)?.Namespace
            ?? Path.GetFileNameWithoutExtension(testProject);
        var serviceImplementation = context.ServiceImplementations.FirstOrDefault();
        var usesServiceSubject = serviceImplementation is not null;
        var subject = usesServiceSubject ? serviceImplementation!.FullName : context.Controller!.FullName;
        var className = $"{context.EntityName}{method}Tests";
        var builder = new StringBuilder();
        builder.AppendLine("using System.Runtime.CompilerServices;");
        builder.AppendLine("using FluentAssertions;");
        if (!usesServiceSubject)
        {
            builder.AppendLine("using Microsoft.AspNetCore.Mvc;");
        }

        builder.AppendLine("using Moq;");
        builder.AppendLine("using Xunit;");
        builder.AppendLine();
        builder.AppendLine($"namespace {testNamespace};");
        builder.AppendLine();
        builder.AppendLine($"public sealed class {className}");
        builder.AppendLine("{");
        builder.AppendLine($"    private readonly Mock<global::{context.RepositoryInterface.FullName}> repository = new(MockBehavior.Strict);");
        builder.AppendLine();
        builder.AppendLine("    // Recipe tests only need an instance; required members are irrelevant to the mocked repository.");
        builder.AppendLine($"    private static T Sample<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));");
        builder.AppendLine();
        builder.AppendLine($"    private global::{subject} CreateSubject()");
        builder.AppendLine("    {");
        builder.AppendLine($"        var constructor = typeof(global::{subject}).GetConstructors().Single();");
        builder.AppendLine("        var arguments = constructor.GetParameters()");
        builder.AppendLine($"            .Select(parameter => parameter.ParameterType == typeof(global::{context.RepositoryInterface.FullName})");
        builder.AppendLine("                ? repository.Object");
        builder.AppendLine("                : ((Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(parameter.ParameterType))!).Object)");
        builder.AppendLine("            .ToArray();");
        builder.AppendLine($"        return (global::{subject})constructor.Invoke(arguments);");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.Append(usesServiceSubject ? ServiceTests() : ControllerTests());
        builder.AppendLine("}");

        return (Path.Combine(directory, $"{className}.cs"), builder.ToString());
    }

    private string RepositoryCall(bool missing = false) => kind switch
    {
        RecipeKind.GetByCode => $"{method}Async({RecipeRules.SampleLiteral(FieldType, missing ? 2 : 1)}, It.IsAny<CancellationToken>())",
        RecipeKind.Search => $"{method}Async(It.IsAny<string>(), It.IsAny<CancellationToken>())",
        RecipeKind.GetByDateRange => $"{method}Async(It.IsAny<{FieldType}>(), It.IsAny<{FieldType}>(), It.IsAny<CancellationToken>())",
        _ => $"{method}Async(It.IsAny<CancellationToken>())"
    };

    private string CallArguments(bool missing = false) => kind switch
    {
        RecipeKind.GetByCode => $"{RecipeRules.SampleLiteral(FieldType, missing ? 2 : 1)}, ",
        RecipeKind.Search => "\"sample\", ",
        RecipeKind.GetByDateRange => $"{RecipeRules.SampleLiteral(FieldType, 1)}, {RecipeRules.SampleLiteral(FieldType, 2)}, ",
        _ => string.Empty
    };

    private string InvalidCallArguments() => kind switch
    {
        RecipeKind.Search => "\" \", ",
        RecipeKind.GetByDateRange => $"{RecipeRules.SampleLiteral(FieldType, 2)}, {RecipeRules.SampleLiteral(FieldType, 1)}, ",
        RecipeKind.GetByCode => "\" \", ",
        _ => string.Empty
    };

    private bool HasValidationCase => kind is RecipeKind.Search or RecipeKind.GetByDateRange or RecipeKind.BulkInsert ||
                                      (kind == RecipeKind.GetByCode && FieldType == "string");

    private string AnyKeyArguments => string.Join(", ", context.KeyParameters.Select(key => $"It.IsAny<{key.Type}>()")) + ", It.IsAny<CancellationToken>()";

    private string KeyedRequests(string type)
    {
        if (context.HasGeneratedKey)
        {
            return $"        var requests = new List<{type}> {{ Sample<{type}>(), Sample<{type}>() }};\n";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"        var first = Sample<{type}>();");
        builder.AppendLine($"        var second = Sample<{type}>();");
        foreach (var key in context.KeyProperties)
        {
            var keyType = context.Properties[key].TrimEnd('?');
            builder.AppendLine($"        first.{key} = {RecipeRules.SampleLiteral(keyType, 1)};");
            builder.AppendLine($"        second.{key} = {RecipeRules.SampleLiteral(keyType, 2)};");
        }

        builder.AppendLine($"        var requests = new List<{type}> {{ first, second }};");
        return builder.ToString();
    }

    private string ServiceTests()
    {
        var builder = new StringBuilder();
        if (kind == RecipeKind.BulkInsert)
        {
            var noExisting = context.HasGeneratedKey ? string.Empty : $"        repository.Setup(item => item.GetByIdAsync({AnyKeyArguments})).ReturnsAsync(({Entity}?)null);\n";
            builder.Append(Fact("BulkInsertAsync_creates_every_record",
                KeyedRequests(CreateRequest) + noExisting +
                $"        repository.Setup(item => item.AddAsync(It.IsAny<{Entity}>(), It.IsAny<CancellationToken>())).ReturnsAsync(({Entity} entity, CancellationToken _) => entity);\n\n" +
                "        var result = await CreateSubject().BulkInsertAsync(requests);\n\n" +
                "        result.Should().NotBeNull().And.HaveCount(2);\n" +
                $"        repository.Verify(item => item.AddAsync(It.IsAny<{Entity}>(), It.IsAny<CancellationToken>()), Times.Exactly(2));\n"));
            builder.Append(Fact("BulkInsertAsync_rejects_an_empty_list",
                $"        var act = () => CreateSubject().BulkInsertAsync(new List<{CreateRequest}>());\n\n" +
                "        await act.Should().ThrowAsync<ArgumentException>();\n"));
            if (!context.HasGeneratedKey)
            {
                builder.Append(Fact("BulkInsertAsync_returns_null_when_a_key_exists",
                    KeyedRequests(CreateRequest) +
                    $"        repository.Setup(item => item.GetByIdAsync({AnyKeyArguments})).ReturnsAsync(Sample<{Entity}>());\n\n" +
                    "        var result = await CreateSubject().BulkInsertAsync(requests);\n\n" +
                    "        result.Should().BeNull();\n" +
                    $"        repository.Verify(item => item.AddAsync(It.IsAny<{Entity}>(), It.IsAny<CancellationToken>()), Times.Never);\n"));
            }

            builder.Append(Fact("BulkInsertAsync_does_not_swallow_repository_exceptions",
                KeyedRequests(CreateRequest) + noExisting +
                $"        repository.Setup(item => item.AddAsync(It.IsAny<{Entity}>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException(\"storage unavailable\"));\n\n" +
                "        var act = () => CreateSubject().BulkInsertAsync(requests);\n\n" +
                "        await act.Should().ThrowAsync<InvalidOperationException>();\n", last: true));
            return builder.ToString();
        }

        if (kind == RecipeKind.GetByCode)
        {
            builder.Append(Fact($"{method}Async_returns_the_record_when_found",
                $"        repository.Setup(item => item.{RepositoryCall()}).ReturnsAsync(Sample<{Entity}>());\n\n" +
                $"        var result = await CreateSubject().{method}Async({CallArguments().TrimEnd(',', ' ')});\n\n" +
                "        result.Should().NotBeNull();\n"));
            builder.Append(Fact($"{method}Async_returns_null_when_not_found",
                $"        repository.Setup(item => item.{RepositoryCall(missing: true)}).ReturnsAsync(({Entity}?)null);\n\n" +
                $"        var result = await CreateSubject().{method}Async({CallArguments(missing: true).TrimEnd(',', ' ')});\n\n" +
                "        result.Should().BeNull();\n"));
        }
        else
        {
            builder.Append(Fact($"{method}Async_returns_the_matching_records",
                $"        repository.Setup(item => item.{RepositoryCall()}).ReturnsAsync(new[] {{ Sample<{Entity}>() }});\n\n" +
                $"        var result = await CreateSubject().{method}Async({CallArguments().TrimEnd(',', ' ')});\n\n" +
                "        result.Should().ContainSingle();\n"));
            builder.Append(Fact($"{method}Async_returns_an_empty_list_when_nothing_matches",
                $"        repository.Setup(item => item.{RepositoryCall()}).ReturnsAsync(Array.Empty<{Entity}>());\n\n" +
                $"        var result = await CreateSubject().{method}Async({CallArguments().TrimEnd(',', ' ')});\n\n" +
                "        result.Should().BeEmpty();\n"));
        }

        if (HasValidationCase)
        {
            builder.Append(Fact($"{method}Async_rejects_invalid_input",
                $"        var act = () => CreateSubject().{method}Async({InvalidCallArguments().TrimEnd(',', ' ')});\n\n" +
                "        await act.Should().ThrowAsync<ArgumentException>();\n"));
        }

        builder.Append(Fact($"{method}Async_does_not_swallow_repository_exceptions",
            $"        repository.Setup(item => item.{RepositoryCall()}).ThrowsAsync(new InvalidOperationException(\"storage unavailable\"));\n\n" +
            $"        var act = () => CreateSubject().{method}Async({CallArguments().TrimEnd(',', ' ')});\n\n" +
            "        await act.Should().ThrowAsync<InvalidOperationException>();\n", last: true));
        return builder.ToString();
    }

    private string ControllerTests()
    {
        var builder = new StringBuilder();
        if (kind == RecipeKind.BulkInsert)
        {
            var noExisting = context.HasGeneratedKey ? string.Empty : $"        repository.Setup(item => item.GetByIdAsync({AnyKeyArguments})).ReturnsAsync(({Entity}?)null);\n";
            builder.Append(Fact("BulkInsert_returns_created",
                KeyedRequests(Entity) + noExisting +
                $"        repository.Setup(item => item.AddAsync(It.IsAny<{Entity}>(), It.IsAny<CancellationToken>())).ReturnsAsync(({Entity} entity, CancellationToken _) => entity);\n\n" +
                "        var result = await CreateSubject().BulkInsert(requests, CancellationToken.None);\n\n" +
                "        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);\n"));
            builder.Append(Fact("BulkInsert_rejects_an_empty_list",
                $"        var result = await CreateSubject().BulkInsert(new List<{Entity}>(), CancellationToken.None);\n\n" +
                "        result.Should().BeOfType<BadRequestObjectResult>();\n"));
            if (!context.HasGeneratedKey)
            {
                builder.Append(Fact("BulkInsert_returns_conflict_when_a_key_exists",
                    KeyedRequests(Entity) +
                    $"        repository.Setup(item => item.GetByIdAsync({AnyKeyArguments})).ReturnsAsync(Sample<{Entity}>());\n\n" +
                    "        var result = await CreateSubject().BulkInsert(requests, CancellationToken.None);\n\n" +
                    "        result.Should().BeOfType<ConflictResult>();\n"));
            }

            builder.Append(Fact("BulkInsert_does_not_swallow_repository_exceptions",
                KeyedRequests(Entity) + noExisting +
                $"        repository.Setup(item => item.AddAsync(It.IsAny<{Entity}>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException(\"storage unavailable\"));\n\n" +
                "        var act = () => CreateSubject().BulkInsert(requests, CancellationToken.None);\n\n" +
                "        await act.Should().ThrowAsync<InvalidOperationException>();\n", last: true));
            return builder.ToString();
        }

        if (kind == RecipeKind.GetByCode)
        {
            builder.Append(Fact($"{method}_returns_ok_when_found",
                $"        repository.Setup(item => item.{RepositoryCall()}).ReturnsAsync(Sample<{Entity}>());\n\n" +
                $"        var result = await CreateSubject().{method}({CallArguments()}CancellationToken.None);\n\n" +
                "        result.Should().BeOfType<OkObjectResult>();\n"));
            builder.Append(Fact($"{method}_returns_not_found_when_missing",
                $"        repository.Setup(item => item.{RepositoryCall(missing: true)}).ReturnsAsync(({Entity}?)null);\n\n" +
                $"        var result = await CreateSubject().{method}({CallArguments(missing: true)}CancellationToken.None);\n\n" +
                "        result.Should().BeOfType<NotFoundResult>();\n"));
        }
        else
        {
            builder.Append(Fact($"{method}_returns_the_matching_records",
                $"        repository.Setup(item => item.{RepositoryCall()}).ReturnsAsync(new[] {{ Sample<{Entity}>() }});\n\n" +
                $"        var result = await CreateSubject().{method}({CallArguments()}CancellationToken.None);\n\n" +
                $"        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<IEnumerable<{Entity}>>().Which.Should().ContainSingle();\n"));
            builder.Append(Fact($"{method}_returns_an_empty_list_when_nothing_matches",
                $"        repository.Setup(item => item.{RepositoryCall()}).ReturnsAsync(Array.Empty<{Entity}>());\n\n" +
                $"        var result = await CreateSubject().{method}({CallArguments()}CancellationToken.None);\n\n" +
                $"        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<IEnumerable<{Entity}>>().Which.Should().BeEmpty();\n"));
            if (kind is RecipeKind.Search or RecipeKind.GetByDateRange)
            {
                builder.Append(Fact($"{method}_rejects_invalid_input",
                    $"        var result = await CreateSubject().{method}({InvalidCallArguments()}CancellationToken.None);\n\n" +
                    "        result.Should().BeOfType<BadRequestObjectResult>();\n"));
            }
        }

        builder.Append(Fact($"{method}_does_not_swallow_repository_exceptions",
            $"        repository.Setup(item => item.{RepositoryCall()}).ThrowsAsync(new InvalidOperationException(\"storage unavailable\"));\n\n" +
            $"        var act = () => CreateSubject().{method}({CallArguments()}CancellationToken.None);\n\n" +
            "        await act.Should().ThrowAsync<InvalidOperationException>();\n", last: true));
        return builder.ToString();
    }

    private static string Fact(string name, string body, bool last = false) =>
        $"    [Fact]\n    public async Task {name}()\n    {{\n{body}    }}\n" + (last ? string.Empty : "\n");
}
