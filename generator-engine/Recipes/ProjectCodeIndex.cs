using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ApiGenerator.Cli.Recipes;

public sealed class CodeFile
{
    public required string Path { get; init; }
    public required string Text { get; init; }
    public required CompilationUnitSyntax Root { get; init; }
    public string Namespace { get; init; } = string.Empty;

    /// <summary>The using directives of the file, as written, so partial files resolve aliases identically.</summary>
    public string UsingBlock => string.Join(Environment.NewLine, Root.Usings.Select(directive => directive.ToString()));

    public IReadOnlyDictionary<string, string> Aliases => Root.Usings
        .Where(directive => directive.Alias is not null)
        .GroupBy(directive => directive.Alias!.Name.Identifier.Text)
        .ToDictionary(group => group.Key, group => group.First().NamespaceOrType.ToString(), StringComparer.Ordinal);
}

public sealed class TypeEntry
{
    public required string Name { get; init; }
    public required TypeDeclarationSyntax Declaration { get; init; }
    public required CodeFile File { get; init; }

    /// <summary>Every partial declaration of the type (including this one).</summary>
    public IReadOnlyList<TypeEntry> Parts { get; internal set; } = [];

    private IEnumerable<MemberDeclarationSyntax> AllMembers => (Parts.Count == 0 ? [this] : Parts).SelectMany(part => part.Declaration.Members);

    public string FullName => string.IsNullOrEmpty(File.Namespace) ? Name : $"{File.Namespace}.{Name}";
    public bool IsInterface => Declaration is InterfaceDeclarationSyntax;
    public bool IsPartial => Declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword));

    public MethodDeclarationSyntax? Method(string name) =>
        AllMembers.OfType<MethodDeclarationSyntax>().FirstOrDefault(method => method.Identifier.Text == name);

    public bool HasMember(string name) =>
        AllMembers.Any(member => member switch
        {
            MethodDeclarationSyntax method => method.Identifier.Text == name,
            PropertyDeclarationSyntax property => property.Identifier.Text == name,
            _ => false
        });
}

/// <summary>Syntax-only index of the C# sources of a generated solution (bin/obj excluded).</summary>
public sealed class ProjectCodeIndex
{
    private readonly List<CodeFile> files;
    private readonly List<TypeEntry> types;

    private ProjectCodeIndex(List<CodeFile> files)
    {
        this.files = files;
        var declarations = files
            .SelectMany(file => file.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Select(declaration => new TypeEntry { Name = declaration.Identifier.Text, Declaration = declaration, File = file }))
            .ToList();

        // Partial declarations form one type; the primary part is the one in "<Name>.cs" (else the largest).
        types = declarations
            .GroupBy(type => (type.FullName, type.IsInterface))
            .Select(group =>
            {
                var parts = group.ToList();
                var primary = parts.FirstOrDefault(part => System.IO.Path.GetFileName(part.File.Path) == $"{part.Name}.cs")
                    ?? parts.OrderByDescending(part => part.Declaration.Members.Count).First();
                foreach (var part in parts)
                {
                    part.Parts = parts;
                }

                return primary;
            })
            .ToList();
    }

    public IReadOnlyList<CodeFile> Files => files;

    public static ProjectCodeIndex Load(string root)
    {
        var separator = System.IO.Path.DirectorySeparatorChar;
        var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}bin{separator}") &&
                           !path.Contains($"{separator}obj{separator}") &&
                           !path.Contains($"{separator}.git{separator}"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var text = File.ReadAllText(path);
                var tree = CSharpSyntaxTree.ParseText(text, path: path);
                var root = tree.GetCompilationUnitRoot();
                var namespaceName = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? string.Empty;
                return new CodeFile { Path = path, Text = text, Root = root, Namespace = namespaceName };
            })
            .ToList();

        return new ProjectCodeIndex(sources);
    }

    public TypeEntry? FindInterface(string name) =>
        types.FirstOrDefault(type => type.IsInterface && type.Name == name);

    public TypeEntry? FindClass(string name) =>
        types.FirstOrDefault(type => !type.IsInterface && type.Name == name);

    public IReadOnlyList<TypeEntry> ClassesImplementing(string interfaceName) =>
        types.Where(type => !type.IsInterface &&
                            type.Parts.Any(part => part.Declaration.BaseList?.Types.Any(baseType => SimpleName(baseType.Type.ToString()) == interfaceName) == true))
            .ToList();

    /// <summary>Resolves a type as written in <paramref name="context"/> (alias or simple name) to a full name.</summary>
    public string ResolveFullName(string typeText, CodeFile context)
    {
        var trimmed = typeText.Trim().TrimEnd('?');
        if (context.Aliases.TryGetValue(trimmed, out var aliasTarget))
        {
            return aliasTarget.StartsWith("global::", StringComparison.Ordinal) ? aliasTarget["global::".Length..] : aliasTarget;
        }

        if (trimmed.Contains('.'))
        {
            return trimmed;
        }

        var declared = types.Where(type => type.Name == trimmed).ToList();
        var sameNamespace = declared.FirstOrDefault(type => type.File.Namespace == context.Namespace);
        return (sameNamespace ?? declared.FirstOrDefault())?.FullName ?? trimmed;
    }

    public TypeEntry? FindByFullName(string fullName) =>
        types.FirstOrDefault(type => !type.IsInterface && type.FullName == fullName);

    public static string SimpleName(string typeText)
    {
        var withoutGenerics = typeText.Split('<')[0];
        return withoutGenerics.Split('.').Last().Trim();
    }
}
