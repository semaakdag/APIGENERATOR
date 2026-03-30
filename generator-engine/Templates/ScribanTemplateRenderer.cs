using ApiGenerator.Cli.Generators;
using Scriban;

namespace ApiGenerator.Cli.Templates;

public sealed class ScribanTemplateRenderer : ITemplateRenderer
{
    private readonly string templatesRoot;

    public ScribanTemplateRenderer(string baseDirectory)
    {
        templatesRoot = Path.Combine(baseDirectory, "Templates");
    }

    public async Task<string> RenderAsync(string templateName, object model)
    {
        var templatePath = Path.Combine(templatesRoot, templateName);
        var templateText = await File.ReadAllTextAsync(templatePath);
        return await RenderInternalAsync(templateText, model, templateName);
    }

    public async Task<string> RenderContentAsync(string templateContent, object model)
    {
        return await RenderInternalAsync(templateContent, model, "inline-content");
    }

    private static async Task<string> RenderInternalAsync(string templateContent, object model, string templateName)
    {
        var template = Template.Parse(templateContent);

        if (template.HasErrors)
        {
            var errors = string.Join("; ", template.Messages.Select(message => message.Message));
            throw new InvalidOperationException($"Template '{templateName}' contains parse errors: {errors}");
        }

        return await template.RenderAsync(model, member => member.Name);
    }
}
