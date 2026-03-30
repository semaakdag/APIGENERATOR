using ApiGenerator.Cli.Llm;

namespace ApiGenerator.Cli.Generators;

public sealed class GenerationManifest
{
    public required string SolutionName { get; init; }
    public required string OutputPath { get; init; }
    public required DateTime GeneratedAtUtc { get; init; }
    public string? ProfilePath { get; init; }
    public string? FrameworkPath { get; init; }
    public LlmExecutionSummary? Llm { get; init; }
    public int EntityCount { get; init; }
    public bool DryRun { get; init; }
    public required string OverwriteMode { get; init; }
    public required GenerationSummary Summary { get; init; }
    public required IReadOnlyList<GeneratedFileEntry> GeneratedFiles { get; init; }
}

public sealed class GeneratedFileEntry
{
    public required string RelativePath { get; init; }
    public required string Category { get; init; }
    public required string Status { get; init; }
}

public sealed class GenerationSummary
{
    public int TotalFiles { get; init; }
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int Conflicts { get; init; }
}
