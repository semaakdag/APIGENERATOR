using System.Text.Json;

namespace ApiGenerator.Cli.Generators;

public sealed class GenerationFileWriter
{
    public async Task<GenerationManifest> ExecuteAsync(GenerationPlan plan, OverwriteMode overwriteMode, bool dryRun)
    {
        var outputRoot = Path.GetFullPath(plan.OutputPath);
        Directory.CreateDirectory(outputRoot);

        var evaluatedEntries = new List<GeneratedFileEntry>();

        foreach (var file in plan.Files)
        {
            var entry = await EvaluateFileAsync(outputRoot, file, overwriteMode, dryRun);
            evaluatedEntries.Add(entry);
        }

        if (overwriteMode == OverwriteMode.Fail)
        {
            var conflicts = evaluatedEntries.Where(entry => entry.Status == "conflict").ToList();
            if (conflicts.Count > 0)
            {
                var conflictList = string.Join(Environment.NewLine, conflicts.Select(conflict => $"- {conflict.RelativePath}"));
                throw new InvalidOperationException("Conflicting files detected:" + Environment.NewLine + conflictList);
            }
        }

        var manifestStatus = File.Exists(Path.Combine(outputRoot, "generation-manifest.json")) ? "updated" : "created";
        var manifestEntry = new GeneratedFileEntry
        {
            RelativePath = "generation-manifest.json",
            Category = "manifest",
            Status = dryRun ? $"would-{manifestStatus}" : manifestStatus
        };

        var allEntries = evaluatedEntries.Concat(new[] { manifestEntry }).ToList();
        var manifest = new GenerationManifest
        {
            SolutionName = plan.SolutionName,
            OutputPath = outputRoot,
            GeneratedAtUtc = DateTime.UtcNow,
            ProfilePath = plan.ProfilePath,
            FrameworkPath = plan.FrameworkPath,
            Llm = plan.Llm,
            EntityCount = plan.EntityCount,
            DryRun = dryRun,
            OverwriteMode = overwriteMode.ToString().ToLowerInvariant(),
            Summary = BuildSummary(allEntries),
            GeneratedFiles = allEntries
        };

        if (!dryRun)
        {
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(Path.Combine(outputRoot, "generation-manifest.json"), json);
        }

        return manifest;
    }

    private static async Task<GeneratedFileEntry> EvaluateFileAsync(
        string outputRoot,
        PlannedFileEntry file,
        OverwriteMode overwriteMode,
        bool dryRun)
    {
        var normalizedPath = NormalizeRelativePath(file.RelativePath);
        var fullPath = Path.Combine(outputRoot, normalizedPath);
        var relativePath = normalizedPath.Replace(Path.DirectorySeparatorChar, '/');
        var usesBinaryContent = file.BinaryContent is not null;

        if (!File.Exists(fullPath))
        {
            if (!dryRun)
            {
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (usesBinaryContent)
                {
                    await File.WriteAllBytesAsync(fullPath, file.BinaryContent!);
                }
                else
                {
                    await File.WriteAllTextAsync(fullPath, file.Content);
                }
            }

            return new GeneratedFileEntry
            {
                RelativePath = relativePath,
                Category = file.Category,
                Status = dryRun ? "would-create" : "created"
            };
        }

        if (usesBinaryContent)
        {
            var existingBytes = await File.ReadAllBytesAsync(fullPath);
            if (existingBytes.AsSpan().SequenceEqual(file.BinaryContent))
            {
                return new GeneratedFileEntry
                {
                    RelativePath = relativePath,
                    Category = file.Category,
                    Status = "unchanged"
                };
            }
        }
        else
        {
            var existingContent = await File.ReadAllTextAsync(fullPath);
            if (string.Equals(existingContent, file.Content, StringComparison.Ordinal))
            {
                return new GeneratedFileEntry
                {
                    RelativePath = relativePath,
                    Category = file.Category,
                    Status = "unchanged"
                };
            }
        }

        if (overwriteMode == OverwriteMode.Overwrite)
        {
            if (!dryRun)
            {
                if (usesBinaryContent)
                {
                    await File.WriteAllBytesAsync(fullPath, file.BinaryContent!);
                }
                else
                {
                    await File.WriteAllTextAsync(fullPath, file.Content);
                }
            }

            return new GeneratedFileEntry
            {
                RelativePath = relativePath,
                Category = file.Category,
                Status = dryRun ? "would-update" : "updated"
            };
        }

        return new GeneratedFileEntry
        {
            RelativePath = relativePath,
            Category = file.Category,
            Status = "conflict"
        };
    }

    private static GenerationSummary BuildSummary(IReadOnlyList<GeneratedFileEntry> entries) =>
        new()
        {
            TotalFiles = entries.Count,
            Created = entries.Count(entry => entry.Status is "created" or "would-create"),
            Updated = entries.Count(entry => entry.Status is "updated" or "would-update"),
            Unchanged = entries.Count(entry => entry.Status == "unchanged"),
            Conflicts = entries.Count(entry => entry.Status == "conflict")
        };

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
}
