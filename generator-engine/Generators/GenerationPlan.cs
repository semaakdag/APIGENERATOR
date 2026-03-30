using ApiGenerator.Cli.Llm;
using ApiGenerator.Cli.Analyzers;

namespace ApiGenerator.Cli.Generators;

public sealed class GenerationPlan
{
    public required string SolutionName { get; init; }
    public required string OutputPath { get; init; }
    public string? ProfilePath { get; init; }
    public string? FrameworkPath { get; init; }
    public LlmExecutionSummary? Llm { get; init; }
    public int EntityCount { get; init; }
    public required IReadOnlyList<PlannedFileEntry> Files { get; init; }
}

public sealed class PlannedFileEntry
{
    public required string RelativePath { get; init; }
    public required string Category { get; init; }
    public string ArtifactKey { get; init; } = string.Empty;
    public required string Content { get; init; }
    public byte[]? BinaryContent { get; init; }
}

public sealed class GenerationRequest
{
    public required string OutputPath { get; init; }
    public string? ProfilePath { get; init; }
    public string? FrameworkPath { get; init; }
    public string? ReferenceProjectPath { get; init; }
    public StandardProfile? LearnedProfile { get; init; }
    public string? ConnectionString { get; init; }
    public GenerationFeatureSelection? Features { get; init; }
    public LlmExecutionSettings? Llm { get; init; }
    public OverwriteMode OverwriteMode { get; init; } = OverwriteMode.Skip;
    public bool DryRun { get; init; }
}

public sealed class GenerationFeatureSelection
{
    public FeatureSelectionMode WindowsAuthentication { get; init; } = FeatureSelectionMode.Inherit;
    public FeatureSelectionMode UnitTests { get; init; } = FeatureSelectionMode.Inherit;
    public FeatureSelectionMode PostmanCollection { get; init; } = FeatureSelectionMode.Inherit;

    public bool HasOverrides =>
        WindowsAuthentication != FeatureSelectionMode.Inherit ||
        UnitTests != FeatureSelectionMode.Inherit ||
        PostmanCollection != FeatureSelectionMode.Inherit;
}

public enum FeatureSelectionMode
{
    Inherit,
    Enable,
    Disable
}

public enum OverwriteMode
{
    Skip,
    Overwrite,
    Fail
}
