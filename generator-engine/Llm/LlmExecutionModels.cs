namespace ApiGenerator.Cli.Llm;

public sealed class LlmExecutionSettings
{
    public bool Enabled { get; init; }
    public string? Url { get; init; }
    public string? Model { get; init; }
    public string? Token { get; init; }
    public int? MaxConcurrency { get; init; }

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(Url) &&
        !string.IsNullOrWhiteSpace(Model) &&
        !string.IsNullOrWhiteSpace(Token);
}

public sealed class LlmExecutionSummary
{
    public bool Enabled { get; init; }
    public bool Applied { get; init; }
    public string? Url { get; init; }
    public string? Model { get; init; }
    public int? ConfiguredMaxConcurrency { get; init; }
    public int EffectiveMaxConcurrency { get; init; }
    public int TargetFiles { get; init; }
    public int RefinedFiles { get; init; }
    public int SkippedFiles { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed class LlmRefinementResult
{
    public required IReadOnlyList<Generators.PlannedFileEntry> Files { get; init; }
    public required LlmExecutionSummary Summary { get; init; }
}
