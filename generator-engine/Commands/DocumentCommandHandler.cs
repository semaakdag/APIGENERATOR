using ApiGenerator.Cli.Documentation;

namespace ApiGenerator.Cli.Commands;

public static class DocumentCommandHandler
{
    public static async Task HandleAsync(string outputPath, MarkdownDocumentationGenerator generator)
    {
        await generator.GenerateStandaloneAsync(outputPath);
        Console.WriteLine($"Documentation written to '{outputPath}'.");
    }
}
