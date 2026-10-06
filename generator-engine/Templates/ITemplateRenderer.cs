namespace ApiGenerator.Cli.Templates;

public interface ITemplateRenderer
{
    Task<string> RenderAsync(string templateName, object model);
    Task<string> RenderContentAsync(string templateContent, object model, string templateName = "inline template");

    /// <summary>Parses every built-in template plus the given inline templates and returns readable errors.</summary>
    IReadOnlyList<string> Validate(IEnumerable<(string Name, string Content)> inlineTemplates);
}
