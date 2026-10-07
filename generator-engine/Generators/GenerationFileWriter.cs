using System.Text.Json;
using ApiGenerator.Cli.Commands;
using ApiGenerator.Cli.Recipes;

namespace ApiGenerator.Cli.Generators;

public sealed class GenerationFileWriter
{
    public async Task<GenerationManifest> ExecuteAsync(GenerationPlan plan, OverwriteMode overwriteMode, bool dryRun)
    {
        var outputRoot = Path.GetFullPath(plan.OutputPath);
        EnsureFilesStayInsideOutput(outputRoot, plan.Files);
        if (!dryRun)
        {
            Directory.CreateDirectory(outputRoot);
        }

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
                throw new GenerationConflictException("Conflicting files detected:" + Environment.NewLine + conflictList);
            }
        }

        if (overwriteMode == OverwriteMode.Overwrite)
        {
            evaluatedEntries.AddRange(RemoveStaleFiles(outputRoot, plan.Files, dryRun));
        }

        var manifestExists = File.Exists(Path.Combine(outputRoot, "generation-manifest.json"));
        var manifestStatus = manifestExists ? "updated" : "created";
        var manifestEntry = new GeneratedFileEntry
        {
            RelativePath = "generation-manifest.json",
            Category = "manifest",
            Status = dryRun ? (manifestExists ? "would-update" : "would-create") : manifestStatus
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
            GeneratedFiles = allEntries,
            Warnings = plan.Warnings
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

    // Overwrite rebuilds the solution from scratch: files written by an earlier generation or by add-endpoint that are
    // not part of the new plan are removed. Files the generator never wrote are left alone.
    private static IReadOnlyList<GeneratedFileEntry> RemoveStaleFiles(string outputRoot, IReadOnlyList<PlannedFileEntry> plannedFiles, bool dryRun)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var planned = new HashSet<string>(plannedFiles.Select(file => NormalizeRelativePath(file.RelativePath).Replace(Path.DirectorySeparatorChar, '/')), comparer)
        {
            "generation-manifest.json"
        };

        var previous = new List<(string Path, string Category)>();
        var manifestPath = Path.Combine(outputRoot, "generation-manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<PreviousManifest>(File.ReadAllText(manifestPath));
                previous.AddRange((manifest?.GeneratedFiles ?? [])
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.RelativePath) && entry.Status is not ("would-create" or "would-update" or "deleted" or "would-delete"))
                    .Select(entry => (entry.RelativePath!, entry.Category ?? "source")));
            }
            catch (JsonException)
            {
                // An unreadable manifest only means there is nothing known to clean up.
            }
        }

        try
        {
            foreach (var record in EndpointStore.Load(outputRoot))
            {
                previous.AddRange(record.Files.Select(file => (file, "endpoint")));
            }
        }
        catch (CliInputException)
        {
        }

        if (File.Exists(Path.Combine(outputRoot, EndpointStore.FileName)))
        {
            previous.Add((EndpointStore.FileName, "endpoint"));
        }

        var rootWithSeparator = outputRoot.EndsWith(Path.DirectorySeparatorChar) ? outputRoot : outputRoot + Path.DirectorySeparatorChar;
        var removed = new List<GeneratedFileEntry>();
        var seen = new HashSet<string>(comparer);
        foreach (var (path, category) in previous)
        {
            var relativePath = NormalizeRelativePath(path).Replace(Path.DirectorySeparatorChar, '/');
            if (planned.Contains(relativePath) || !seen.Add(relativePath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(outputRoot, NormalizeRelativePath(path)));
            if (Path.IsPathRooted(NormalizeRelativePath(path)) || !fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                continue;
            }

            if (!dryRun)
            {
                File.Delete(fullPath);
                RemoveEmptyDirectories(Path.GetDirectoryName(fullPath), outputRoot);
            }

            removed.Add(new GeneratedFileEntry
            {
                RelativePath = relativePath,
                Category = category,
                Status = dryRun ? "would-delete" : "deleted"
            });
        }

        return removed;
    }

    private static void RemoveEmptyDirectories(string? directory, string outputRoot)
    {
        var root = outputRoot.TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(directory) &&
               directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               Directory.Exists(directory) &&
               !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private sealed class PreviousManifest
    {
        public List<PreviousEntry>? GeneratedFiles { get; init; }
    }

    private sealed class PreviousEntry
    {
        public string? RelativePath { get; init; }
        public string? Category { get; init; }
        public string? Status { get; init; }
    }

    private static GenerationSummary BuildSummary(IReadOnlyList<GeneratedFileEntry> entries) =>
        new()
        {
            TotalFiles = entries.Count(entry => entry.Status is not ("deleted" or "would-delete")),
            Created = entries.Count(entry => entry.Status is "created" or "would-create"),
            Updated = entries.Count(entry => entry.Status is "updated" or "would-update"),
            Unchanged = entries.Count(entry => entry.Status == "unchanged"),
            Conflicts = entries.Count(entry => entry.Status == "conflict"),
            Deleted = entries.Count(entry => entry.Status is "deleted" or "would-delete")
        };

    // Profiles and templates are user supplied, so every planned path is checked before anything is written.
    private static void EnsureFilesStayInsideOutput(string outputRoot, IEnumerable<PlannedFileEntry> files)
    {
        var rootWithSeparator = outputRoot.EndsWith(Path.DirectorySeparatorChar) ? outputRoot : outputRoot + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            var normalizedPath = NormalizeRelativePath(file.RelativePath);
            var fullPath = Path.GetFullPath(Path.Combine(outputRoot, normalizedPath));
            if (string.IsNullOrWhiteSpace(file.RelativePath) ||
                Path.IsPathRooted(normalizedPath) ||
                !fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                throw new CliInputException(
                    $"Planned file '{file.RelativePath}' resolves outside the output folder '{outputRoot}'. Check SharedFiles and template paths in the selected profile.");
            }
        }
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
}
