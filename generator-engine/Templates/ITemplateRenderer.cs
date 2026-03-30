using ApiGenerator.Cli.Generators;

namespace ApiGenerator.Cli.Templates;

public interface ITemplateRenderer
{
    Task<string> RenderAsync(string templateName, object model);
    Task<string> RenderContentAsync(string templateContent, object model);
}
