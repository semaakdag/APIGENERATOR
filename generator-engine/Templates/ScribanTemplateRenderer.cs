using ApiGenerator.Cli.Commands;
using Scriban;
using Scriban.Parsing;
using Scriban.Syntax;

namespace ApiGenerator.Cli.Templates;

public sealed class ScribanTemplateRenderer : ITemplateRenderer
{
    public const string WorkspaceTemplatesFolder = ".api-generator/templates";

    private static readonly string[] TemplateExtensions = [".sbncs", ".sbnxml", ".sbnjson"];
    private readonly string templatesRoot;

    public ScribanTemplateRenderer(string baseDirectory)
    {
        templatesRoot = Path.Combine(baseDirectory, "Templates");
    }

    /// <summary>Folder whose files replace built-in templates with the same name (workspace customization).</summary>
    public string? OverrideDirectory { get; private set; }

    public IReadOnlyList<string> UseOverrideDirectory(string? directory)
    {
        OverrideDirectory = null;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return [];
        }

        if (!Directory.Exists(directory))
        {
            throw new CliInputException($"Template override folder '{directory}' was not found.");
        }

        OverrideDirectory = Path.GetFullPath(directory);
        var builtInNames = BuiltInTemplateNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(OverrideDirectory)
            .Select(Path.GetFileName)
            .Where(name => !builtInNames.Contains(name!))
            .Select(name => $"Template override '{name}' does not match a built-in template and is ignored.")
            .ToList();
    }

    public async Task<string> RenderAsync(string templateName, object model)
    {
        var templateText = await File.ReadAllTextAsync(ResolveTemplatePath(templateName));
        var displayName = OverrideDirectory is not null && File.Exists(Path.Combine(OverrideDirectory, templateName))
            ? $"workspace template '{templateName}'"
            : $"template '{templateName}'";
        return await RenderInternalAsync(templateText, model, displayName);
    }

    public Task<string> RenderContentAsync(string templateContent, object model, string templateName = "inline template") =>
        RenderInternalAsync(templateContent, model, templateName);

    public IReadOnlyList<string> Validate(IEnumerable<(string Name, string Content)> inlineTemplates)
    {
        var errors = new List<string>();
        foreach (var name in BuiltInTemplateNames())
        {
            var path = ResolveTemplatePath(name);
            var label = path.StartsWith(templatesRoot, StringComparison.Ordinal) ? $"template '{name}'" : $"workspace template '{name}'";
            errors.AddRange(ParseErrors(File.ReadAllText(path), label));
        }

        foreach (var (name, content) in inlineTemplates)
        {
            errors.AddRange(ParseErrors(content, name));
        }

        return errors;
    }

    private IEnumerable<string> BuiltInTemplateNames() =>
        Directory.EnumerateFiles(templatesRoot)
            .Where(path => TemplateExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal);

    private string ResolveTemplatePath(string templateName)
    {
        if (OverrideDirectory is not null)
        {
            var overridePath = Path.Combine(OverrideDirectory, templateName);
            if (File.Exists(overridePath))
            {
                return overridePath;
            }
        }

        return Path.Combine(templatesRoot, templateName);
    }

    private static IEnumerable<string> ParseErrors(string content, string label)
    {
        var template = Template.Parse(content);
        return template.HasErrors
            ? template.Messages.Where(message => message.Type == ParserMessageType.Error).Select(message => FormatMessage(label, message.Span, message.Message))
            : [];
    }

    private static string FormatMessage(string label, SourceSpan span, string message) =>
        $"{label} ({span.Start.Line + 1}:{span.Start.Column + 1}): {message}";

    private static async Task<string> RenderInternalAsync(string templateContent, object model, string templateName)
    {
        var template = Template.Parse(templateContent);

        if (template.HasErrors)
        {
            var errors = string.Join("; ", template.Messages.Select(message => FormatMessage(templateName, message.Span, message.Message)));
            throw new CliInputException($"Template parse error in {errors}");
        }

        try
        {
            return await template.RenderAsync(model, member => member.Name);
        }
        catch (ScriptRuntimeException exception)
        {
            throw new CliInputException($"Template render error in {FormatMessage(templateName, exception.Span, exception.OriginalMessage)}");
        }
    }
}
