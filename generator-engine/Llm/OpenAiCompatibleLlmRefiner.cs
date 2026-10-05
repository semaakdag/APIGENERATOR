using ApiGenerator.Cli.Analyzers;
using ApiGenerator.Cli.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ApiGenerator.Cli.Llm;

public sealed class OpenAiCompatibleLlmRefiner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly HttpClient httpClient;

    public OpenAiCompatibleLlmRefiner(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
    }

    public async Task<LlmRefinementResult> RefineAsync(
        IReadOnlyList<PlannedFileEntry> files,
        StandardProfile profile,
        string promptPackage,
        LlmExecutionSettings settings)
    {
        if (!settings.IsConfigured)
        {
            return new LlmRefinementResult
            {
                Files = files,
                Summary = new LlmExecutionSummary
                {
                    Enabled = settings.Enabled,
                    Applied = false,
                    Url = settings.Url,
                    Model = settings.Model,
                    ConfiguredMaxConcurrency = settings.MaxConcurrency,
                    EffectiveMaxConcurrency = 0,
                    TargetFiles = 0,
                    RefinedFiles = 0,
                    SkippedFiles = files.Count
                }
            };
        }

        var targetArtifacts = ResolveTargetArtifacts(profile);
        var errors = new List<string>();
        var initialRefinement = await RefineTargetFilesAsync(files, targetArtifacts, profile, promptPackage, settings);
        var refinedFiles = initialRefinement.Files.ToList();
        errors.AddRange(initialRefinement.Errors);
        var targetedCount = initialRefinement.TargetedCount;

        var buildRepairResult = await RepairBuildErrorsAsync(
            refinedFiles,
            files,
            targetArtifacts,
            profile,
            promptPackage,
            settings);
        refinedFiles = buildRepairResult.Files.ToList();
        errors.AddRange(buildRepairResult.Errors);

        var originalFileLookup = files.ToDictionary(
            file => NormalizePathKey(file.RelativePath),
            file => file,
            StringComparer.OrdinalIgnoreCase);
        var refinedCount = refinedFiles.Count(file =>
            ShouldRefine(file, targetArtifacts) &&
            originalFileLookup.TryGetValue(NormalizePathKey(file.RelativePath), out var originalFile) &&
            !string.Equals(originalFile.Content, file.Content, StringComparison.Ordinal));

        return new LlmRefinementResult
        {
            Files = refinedFiles,
            Summary = new LlmExecutionSummary
            {
                Enabled = true,
                Applied = targetedCount > 0,
                Url = settings.Url,
                Model = settings.Model,
                ConfiguredMaxConcurrency = settings.MaxConcurrency,
                EffectiveMaxConcurrency = initialRefinement.EffectiveMaxConcurrency,
                TargetFiles = targetedCount,
                RefinedFiles = refinedCount,
                SkippedFiles = files.Count - targetedCount,
                Errors = errors
            }
        };
    }

    private async Task<InitialRefinementResult> RefineTargetFilesAsync(
        IReadOnlyList<PlannedFileEntry> files,
        HashSet<string> targetArtifacts,
        StandardProfile profile,
        string promptPackage,
        LlmExecutionSettings settings)
    {
        var refinedFiles = files.ToArray();
        var targetIndexes = Enumerable.Range(0, files.Count)
            .Where(index => ShouldRefine(files[index], targetArtifacts))
            .ToArray();
        if (targetIndexes.Length == 0)
        {
            return new InitialRefinementResult
            {
                Files = refinedFiles,
                Errors = [],
                TargetedCount = 0,
                EffectiveMaxConcurrency = 0
            };
        }

        var workItems = targetIndexes
            .Select(index => BuildRefinementWorkItem(index, files[index], files, profile, promptPackage))
            .ToArray();
        var errorSlots = new string?[files.Count];
        var maxConcurrency = ResolveMaxConcurrency(settings, targetIndexes.Length, workItems.Select(item => item.EstimatedRequestBytes));

        if (maxConcurrency <= 1 || workItems.Length == 1)
        {
            foreach (var workItem in workItems)
            {
                await RefineTargetFileAsync(workItem, refinedFiles, errorSlots, profile, settings);
            }
        }
        else
        {
            await Parallel.ForEachAsync(
                workItems,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxConcurrency
                },
                async (workItem, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RefineTargetFileAsync(workItem, refinedFiles, errorSlots, profile, settings);
                });
        }

        return new InitialRefinementResult
        {
            Files = refinedFiles,
            Errors = errorSlots.Where(error => !string.IsNullOrWhiteSpace(error)).Cast<string>().ToList(),
            TargetedCount = targetIndexes.Length,
            EffectiveMaxConcurrency = maxConcurrency
        };
    }

    private async Task RefineTargetFileAsync(
        RefinementWorkItem workItem,
        PlannedFileEntry[] refinedFiles,
        string?[] errorSlots,
        StandardProfile profile,
        LlmExecutionSettings settings)
    {
        var file = workItem.File;

        try
        {
            var refinedContent = await RefineFileAsync(file, profile, workItem.PromptPayload, workItem.ArtifactContext, settings);
            refinedFiles[workItem.Index] = new PlannedFileEntry
            {
                RelativePath = file.RelativePath,
                Category = file.Category,
                ArtifactKey = file.ArtifactKey,
                Content = refinedContent
            };
        }
        catch (Exception exception)
        {
            errorSlots[workItem.Index] = $"{file.RelativePath}: {exception.Message}";
            refinedFiles[workItem.Index] = file;
        }
    }

    private async Task<BuildRepairResult> RepairBuildErrorsAsync(
        IReadOnlyList<PlannedFileEntry> refinedFiles,
        IReadOnlyList<PlannedFileEntry> originalFiles,
        HashSet<string> targetArtifacts,
        StandardProfile profile,
        string promptPackage,
        LlmExecutionSettings settings)
    {
        var buildResult = await ValidateBuildAsync(refinedFiles);
        if (buildResult.Succeeded || buildResult.Errors.Count == 0)
        {
            return new BuildRepairResult
            {
                Files = refinedFiles,
                Errors = []
            };
        }

        var retryCandidates = SelectBuildRepairCandidates(refinedFiles, buildResult.Errors, targetArtifacts);

        if (retryCandidates.Count == 0)
        {
            return new BuildRepairResult
            {
                Files = refinedFiles,
                Errors =
                [
                    "Build validation failed after LLM refinement, but no direct or fallback retry targets were identified.",
                    .. buildResult.Errors.Select(FormatBuildError)
                ]
            };
        }

        var updatedFiles = refinedFiles.ToDictionary(
            file => NormalizePathKey(file.RelativePath),
            file => file,
            StringComparer.OrdinalIgnoreCase);
        var additionalErrors = new List<string>();
        var repairContextFiles = refinedFiles.ToList();
        var repairAttempts = await RepairBuildCandidatesAsync(
            retryCandidates,
            repairContextFiles,
            profile,
            promptPackage,
            settings);

        foreach (var attempt in repairAttempts)
        {
            if (!string.IsNullOrWhiteSpace(attempt.Error))
            {
                additionalErrors.Add(attempt.Error);
                continue;
            }

            if (attempt.File is not null)
            {
                updatedFiles[NormalizePathKey(attempt.File.RelativePath)] = attempt.File;
            }
        }

        var finalFiles = updatedFiles.Values
            .OrderBy(file => GetOriginalIndex(file.RelativePath, originalFiles))
            .ToList();
        var finalBuildResult = await ValidateBuildAsync(finalFiles);
        if (!finalBuildResult.Succeeded)
        {
            additionalErrors.AddRange(finalBuildResult.Errors.Select(FormatBuildError));
        }

        return new BuildRepairResult
        {
            Files = finalFiles,
            Errors = additionalErrors
        };
    }

    private async Task<IReadOnlyList<BuildRepairAttemptResult>> RepairBuildCandidatesAsync(
        IReadOnlyList<BuildRepairCandidate> retryCandidates,
        IReadOnlyList<PlannedFileEntry> repairContextFiles,
        StandardProfile profile,
        string promptPackage,
        LlmExecutionSettings settings)
    {
        if (retryCandidates.Count == 0)
        {
            return [];
        }

        var workItems = retryCandidates
            .Select(candidate => BuildRepairWorkItem(candidate, repairContextFiles, profile, promptPackage))
            .ToArray();
        var results = new BuildRepairAttemptResult[workItems.Length];
        var maxConcurrency = ResolveMaxConcurrency(settings, workItems.Length, workItems.Select(item => item.EstimatedRequestBytes));

        if (maxConcurrency <= 1 || workItems.Length == 1)
        {
            for (var index = 0; index < workItems.Length; index++)
            {
                results[index] = await TryRepairBuildCandidateAsync(workItems[index], profile, settings);
            }

            return results;
        }

        await Parallel.ForEachAsync(
            Enumerable.Range(0, workItems.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency
            },
            async (index, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[index] = await TryRepairBuildCandidateAsync(workItems[index], profile, settings);
            });

        return results;
    }

    private async Task<BuildRepairAttemptResult> TryRepairBuildCandidateAsync(
        RepairWorkItem workItem,
        StandardProfile profile,
        LlmExecutionSettings settings)
    {
        var candidate = workItem.Candidate;
        var file = candidate.File;

        try
        {
            var repairedContent = await RequestRefinementAsync(
                BuildSystemPrompt(file, profile, isRepair: true),
                BuildUserPrompt(
                    file,
                    profile,
                    workItem.PromptPayload,
                    workItem.ArtifactContext,
                    file.Content,
                    BuildRepairValidationFeedback(candidate),
                    isRepair: true),
                settings);

            var validationError = ValidateRefinedContent(file, repairedContent);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return new BuildRepairAttemptResult
                {
                    Error = $"{file.RelativePath}: build-repair response failed local validation ({candidate.Strategy}): {validationError}"
                };
            }

            return new BuildRepairAttemptResult
            {
                File = new PlannedFileEntry
                {
                    RelativePath = file.RelativePath,
                    Category = file.Category,
                    ArtifactKey = file.ArtifactKey,
                    Content = repairedContent
                }
            };
        }
        catch (Exception exception)
        {
            return new BuildRepairAttemptResult
            {
                Error = $"{file.RelativePath}: build-repair retry failed ({candidate.Strategy}): {exception.Message}"
            };
        }
    }

    private static IReadOnlyList<BuildRepairCandidate> SelectBuildRepairCandidates(
        IReadOnlyList<PlannedFileEntry> refinedFiles,
        IReadOnlyList<BuildValidationError> buildErrors,
        HashSet<string> targetArtifacts)
    {
        var targetFiles = refinedFiles
            .Where(file => ShouldRefine(file, targetArtifacts))
            .ToList();
        if (targetFiles.Count == 0 || buildErrors.Count == 0)
        {
            return [];
        }

        var candidates = new List<BuildRepairCandidate>();
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in targetFiles)
        {
            var directErrors = buildErrors
                .Where(error => error.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (directErrors.Count == 0)
            {
                continue;
            }

            candidates.Add(new BuildRepairCandidate
            {
                File = file,
                Errors = directErrors,
                Strategy = "direct file match"
            });
            selectedPaths.Add(NormalizePathKey(file.RelativePath));
        }

        var unmatchedErrors = buildErrors
            .Where(error => !selectedPaths.Contains(NormalizePathKey(error.RelativePath)))
            .ToList();

        candidates.AddRange(SelectProjectScopeFallbackCandidates(targetFiles, unmatchedErrors, selectedPaths));

        if (candidates.Count == 0)
        {
            candidates.AddRange(SelectSolutionFallbackCandidates(targetFiles, unmatchedErrors, selectedPaths));
        }

        return candidates;
    }

    private static IReadOnlyList<BuildRepairCandidate> SelectProjectScopeFallbackCandidates(
        IReadOnlyList<PlannedFileEntry> targetFiles,
        IReadOnlyList<BuildValidationError> buildErrors,
        HashSet<string> selectedPaths)
    {
        if (buildErrors.Count == 0)
        {
            return [];
        }

        var errorScopes = buildErrors
            .Select(error => GetProjectScope(error.RelativePath))
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (errorScopes.Count == 0)
        {
            return [];
        }

        var maxCandidates = Math.Clamp(errorScopes.Count * 2, 1, 4);

        return targetFiles
            .Where(file => !selectedPaths.Contains(NormalizePathKey(file.RelativePath)))
            .Select(file =>
            {
                var relevantErrors = SelectRelevantBuildErrorsForFile(file, buildErrors);
                return new
                {
                    File = file,
                    Errors = relevantErrors,
                    Score = ScoreProjectScopeFallbackCandidate(file, relevantErrors, errorScopes)
                };
            })
            .Where(entry => entry.Errors.Count > 0 && entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(maxCandidates)
            .Select(entry => new BuildRepairCandidate
            {
                File = entry.File,
                Errors = entry.Errors,
                Strategy = "project-scope fallback"
            })
            .ToList();
    }

    private static IReadOnlyList<BuildRepairCandidate> SelectSolutionFallbackCandidates(
        IReadOnlyList<PlannedFileEntry> targetFiles,
        IReadOnlyList<BuildValidationError> buildErrors,
        HashSet<string> selectedPaths)
    {
        if (buildErrors.Count == 0)
        {
            return [];
        }

        return targetFiles
            .Where(file => !selectedPaths.Contains(NormalizePathKey(file.RelativePath)))
            .Select(file =>
            {
                var relevantErrors = SelectRelevantBuildErrorsForFile(file, buildErrors);
                if (relevantErrors.Count == 0)
                {
                    relevantErrors = buildErrors.Take(4).ToList();
                }

                return new
                {
                    File = file,
                    Errors = relevantErrors,
                    Score = ScoreSolutionFallbackCandidate(file, relevantErrors)
                };
            })
            .Where(entry => entry.Errors.Count > 0 && entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Select(entry => new BuildRepairCandidate
            {
                File = entry.File,
                Errors = entry.Errors,
                Strategy = "solution-level fallback"
            })
            .ToList();
    }

    private static List<BuildValidationError> SelectRelevantBuildErrorsForFile(
        PlannedFileEntry file,
        IReadOnlyList<BuildValidationError> buildErrors)
    {
        var candidateScope = GetProjectScope(file.RelativePath);
        var matchTokens = GetBuildErrorMatchTokens(file);

        return buildErrors
            .Where(error =>
                error.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(candidateScope) &&
                 candidateScope.Equals(GetProjectScope(error.RelativePath), StringComparison.OrdinalIgnoreCase)) ||
                matchTokens.Any(token => BuildErrorContains(error, token)))
            .Take(6)
            .ToList();
    }

    private static int ScoreProjectScopeFallbackCandidate(
        PlannedFileEntry file,
        IReadOnlyList<BuildValidationError> relevantErrors,
        IReadOnlySet<string> errorScopes)
    {
        if (relevantErrors.Count == 0)
        {
            return 0;
        }

        var score = ScoreBuildRepairCandidateFromErrors(file, relevantErrors);
        var candidateScope = GetProjectScope(file.RelativePath);
        if (!string.IsNullOrWhiteSpace(candidateScope) && errorScopes.Contains(candidateScope))
        {
            score += 10;
        }

        return score;
    }

    private static int ScoreSolutionFallbackCandidate(
        PlannedFileEntry file,
        IReadOnlyList<BuildValidationError> relevantErrors)
    {
        if (relevantErrors.Count == 0)
        {
            return 0;
        }

        return ScoreBuildRepairCandidateFromErrors(file, relevantErrors) + GetArtifactRepairPriority(file.ArtifactKey);
    }

    private static int ScoreBuildRepairCandidateFromErrors(
        PlannedFileEntry file,
        IReadOnlyList<BuildValidationError> relevantErrors)
    {
        if (relevantErrors.Count == 0)
        {
            return 0;
        }

        var fileName = Path.GetFileNameWithoutExtension(file.RelativePath);
        var entityStem = GetEntityStem(file);
        var matchTokens = GetBuildErrorMatchTokens(file);
        var score = Math.Min(relevantErrors.Count, 3);

        foreach (var error in relevantErrors)
        {
            if (!string.IsNullOrWhiteSpace(fileName) && BuildErrorContains(error, fileName))
            {
                score += 3;
            }

            if (!string.IsNullOrWhiteSpace(entityStem) && BuildErrorContains(error, entityStem))
            {
                score += 4;
            }

            score += Math.Min(matchTokens.Count(token => BuildErrorContains(error, token)), 3);
        }

        return score;
    }

    private static IReadOnlySet<string> GetBuildErrorMatchTokens(PlannedFileEntry file)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddKeywords(tokens, file.ArtifactKey, file.Category, Path.GetFileNameWithoutExtension(file.RelativePath));

        var entityStem = GetEntityStem(file);
        if (!string.IsNullOrWhiteSpace(entityStem))
        {
            tokens.Add(entityStem.ToLowerInvariant());
        }

        return tokens;
    }

    private static bool BuildErrorContains(BuildValidationError error, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return $"{error.RelativePath} {error.Message}".Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRepairValidationFeedback(BuildRepairCandidate candidate)
    {
        var builder = new StringBuilder();
        if (!candidate.Strategy.Equals("direct file match", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine($"Retry Strategy: {candidate.Strategy}");
            builder.AppendLine("This file was chosen because the build errors point to the same project scope or to solution-level conventions, not because the compiler named this file directly.");
            builder.AppendLine();
        }

        foreach (var error in candidate.Errors)
        {
            builder.AppendLine(FormatBuildError(error));
        }

        return builder.ToString().Trim();
    }

    private async Task<string> RefineFileAsync(
        PlannedFileEntry file,
        StandardProfile profile,
        PromptPayloadContext promptPayload,
        string artifactContext,
        LlmExecutionSettings settings)
    {
        var systemPrompt = BuildSystemPrompt(file, profile, isRepair: false);
        var refinedContent = await RequestRefinementAsync(
            systemPrompt,
            BuildUserPrompt(file, profile, promptPayload, artifactContext, file.Content, null, isRepair: false),
            settings);

        var validationError = ValidateRefinedContent(file, refinedContent);
        if (string.IsNullOrWhiteSpace(validationError))
        {
            return refinedContent;
        }

        var repairedContent = await RequestRefinementAsync(
            systemPrompt,
            BuildUserPrompt(file, profile, promptPayload, artifactContext, refinedContent, validationError, isRepair: true),
            settings);

        var repairedValidationError = ValidateRefinedContent(file, repairedContent);
        if (!string.IsNullOrWhiteSpace(repairedValidationError))
        {
            throw new InvalidOperationException(
                $"LLM produced invalid content after retry. First validation error: {validationError} Retry validation error: {repairedValidationError}");
        }

        return repairedContent;
    }

    private async Task<string> RequestRefinementAsync(
        string systemPrompt,
        string userPrompt,
        LlmExecutionSettings settings)
    {
        var estimatedRequestBytes = EstimateRequestBytes(systemPrompt, userPrompt, settings.Model);
        var requestTimeout = ResolveRequestTimeout(estimatedRequestBytes);
        using var request = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint(settings.Url!));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (request.RequestUri?.Host.Contains("azure", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Headers.Add("api-key", settings.Token);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        }

        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            temperature = 0.1,
            stream = false,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = systemPrompt
                },
                new
                {
                    role = "user",
                    content = userPrompt
                }
            }
        });

        using var timeoutCts = new CancellationTokenSource(requestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, timeoutCts.Token);
        }
        catch (TaskCanceledException exception) when (timeoutCts.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"LLM request timed out after {requestTimeout.TotalSeconds:F0}s (estimated request {estimatedRequestBytes / 1024d:F1} KB).",
                exception);
        }

        using var _ = response;
        var responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"LLM request failed with {(int)response.StatusCode}: {Truncate(responseText, 240)}");
        }

        using var document = JsonDocument.Parse(responseText);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("LLM response content was empty.");
        }

        return StripCodeFences(content);
    }

    private static async Task<BuildValidationResult> ValidateBuildAsync(IReadOnlyList<PlannedFileEntry> files)
    {
        var solutionTargets = files
            .Where(file => file.BinaryContent is null)
            .Where(file => file.RelativePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projectTargets = files
            .Where(file => file.BinaryContent is null)
            .Where(file => file.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var buildTargets = solutionTargets.Count > 0
            ? solutionTargets
            : projectTargets;

        if (buildTargets.Count == 0)
        {
            return new BuildValidationResult
            {
                Succeeded = true,
                Errors = []
            };
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "ApiGeneratorLlmBuild", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            foreach (var file in files)
            {
                var fullPath = Path.Combine(tempRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (file.BinaryContent is not null)
                {
                    await File.WriteAllBytesAsync(fullPath, file.BinaryContent);
                }
                else
                {
                    await File.WriteAllTextAsync(fullPath, file.Content);
                }
            }

            var errors = new List<BuildValidationError>();
            foreach (var target in buildTargets)
            {
                var result = await ExecuteBuildValidationTargetAsync(tempRoot, target);
                errors.AddRange(result.Errors);
            }

            if (solutionTargets.Count > 0 && errors.Count > 0 && projectTargets.Count > 0)
            {
                foreach (var projectTarget in projectTargets)
                {
                    var result = await ExecuteBuildValidationTargetAsync(tempRoot, projectTarget);
                    errors.AddRange(result.Errors);
                }
            }

            return new BuildValidationResult
            {
                Succeeded = errors.Count == 0,
                Errors = errors
                    .GroupBy(error => $"{NormalizePathKey(error.RelativePath)}::{error.Message}", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList()
            };
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Temporary validation output is best-effort cleanup.
            }
        }
    }

    private static async Task<BuildTargetExecutionResult> ExecuteBuildValidationTargetAsync(
        string tempRoot,
        PlannedFileEntry target)
    {
        var targetPath = Path.Combine(tempRoot, target.RelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{targetPath}\" --nologo --verbosity:minimal",
                WorkingDirectory = tempRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var errors = new List<BuildValidationError>();
        errors.AddRange(ParseBuildErrors(stdout, tempRoot));
        errors.AddRange(ParseBuildErrors(stderr, tempRoot));

        if (process.ExitCode != 0 && errors.Count == 0)
        {
            errors.Add(new BuildValidationError
            {
                RelativePath = target.RelativePath,
                Message = $"dotnet build failed for '{target.RelativePath}' without a file-mapped error. Output: {Truncate((stdout + Environment.NewLine + stderr).Trim(), 400)}"
            });
        }

        return new BuildTargetExecutionResult
        {
            Errors = errors
        };
    }

    private static IReadOnlyList<BuildValidationError> ParseBuildErrors(string output, string tempRoot)
    {
        var errors = new List<BuildValidationError>();
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var errorMarkerIndex = line.LastIndexOf(": error ", StringComparison.OrdinalIgnoreCase);
            if (errorMarkerIndex <= 2)
            {
                continue;
            }

            var pathPart = line[..errorMarkerIndex].Trim();
            var messagePart = line[(errorMarkerIndex + 2)..].Trim();
            var parenthesisIndex = pathPart.IndexOf('(');
            var fullPath = parenthesisIndex > 0 ? pathPart[..parenthesisIndex] : pathPart;
            if (!Path.IsPathRooted(fullPath))
            {
                continue;
            }

            string relativePath;
            try
            {
                relativePath = Path.GetRelativePath(tempRoot, fullPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
            }
            catch
            {
                continue;
            }

            errors.Add(new BuildValidationError
            {
                RelativePath = relativePath,
                Message = messagePart
            });
        }

        return errors;
    }

    private static bool ShouldRefine(PlannedFileEntry file, HashSet<string> targetArtifacts)
    {
        if (string.IsNullOrWhiteSpace(file.ArtifactKey))
        {
            return false;
        }

        return targetArtifacts.Contains(file.ArtifactKey);
    }

    private static HashSet<string> ResolveTargetArtifacts(StandardProfile profile)
    {
        var configured = profile.Llm.ApplyToArtifacts
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (configured.Count > 0)
        {
            return configured;
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "apiProgram",
            "controller",
            "endpointModule",
            "entity",
            "dto",
            "createRequest",
            "updateRequest",
            "response",
            "serviceInterface",
            "serviceImplementation",
            "repositoryInterface",
            "repositoryImplementation",
            "dbContext",
            "launchSettings",
            "unitTests",
            "sharedFiles"
        };
    }

    private static string BuildUserPrompt(
        PlannedFileEntry file,
        StandardProfile profile,
        PromptPayloadContext promptPayload,
        string artifactContext,
        string currentContent,
        string? validationFeedback,
        bool isRepair)
    {
        var contextSection = string.IsNullOrWhiteSpace(artifactContext)
            ? string.Empty
            : $"Additional Stateless Context:{Environment.NewLine}{artifactContext}{Environment.NewLine}{Environment.NewLine}";
        var validationSection = string.IsNullOrWhiteSpace(validationFeedback)
            ? string.Empty
            : $"Validation Feedback From Previous Attempt:{Environment.NewLine}{validationFeedback}{Environment.NewLine}{Environment.NewLine}";
        var instructionSection = BuildPromptInstructionSection(profile, file, isRepair);
        var artifactProfileSection = BuildArtifactPromptProfile(file.ArtifactKey);
        var promptPackageSection = string.IsNullOrWhiteSpace(promptPayload.ScopedPromptPackage)
            ? string.Empty
            : $"Scoped Prompt Package:{Environment.NewLine}{promptPayload.ScopedPromptPackage}{Environment.NewLine}{Environment.NewLine}";

        return $$"""
        Refine this generated file using a completely stateless process.

        Execution Rules:
        - Use only the scoped profile payload, scoped prompt package, target file path, current file content, and stateless context included here.
        - Treat any supplied learned template, related generated file, and support asset as the closest source of truth for local conventions.
        - Preserve project architecture, naming, DI style, logging shape, swagger wiring, and framework pack conventions.
        - Return only the revised file content.

        Prompt Instructions:
        {{instructionSection}}

        Artifact Prompt Profile:
        {{artifactProfileSection}}

        Scoped Profile Payload:
        {{promptPayload.CompactProfilePayload}}

        {{promptPackageSection}}Target Artifact:
        - Relative path: {{file.RelativePath}}
        - Artifact key: {{file.ArtifactKey}}
        - Category: {{file.Category}}

        {{contextSection}}{{validationSection}}Current File Content:
        ```text
        {{currentContent}}
        ```
        """;
    }

    private static PromptPayloadContext CreatePromptPayloadContext(
        PlannedFileEntry file,
        StandardProfile profile,
        string promptPackage) =>
        new(
            BuildCompactProfilePayload(file, profile),
            BuildPromptPackageExcerpt(file, promptPackage));

    private static string BuildCompactProfilePayload(PlannedFileEntry file, StandardProfile profile)
    {
        var relevantArtifacts = GetRelevantArtifacts(file.ArtifactKey);
        var relevantContextAssets = SelectRelevantSharedFiles(file, profile)
            .Select(sharedFile => sharedFile.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var payload = new
        {
            profile.ProfileKind,
            profile.SchemaVersion,
            profile.ProfileName,
            profile.Description,
            profile.ArchitectureStyle,
            profile.Framework,
            profile.ProjectBasePaths,
            profile.Folders,
            profile.NamingRules,
            profile.Patterns,
            profile.Controller,
            profile.Logging,
            Llm = new
            {
                profile.Llm.Mode,
                RelevantArtifacts = relevantArtifacts,
                profile.Llm.Instructions,
                ContextAssets = relevantContextAssets
            },
            CurrentArtifact = new
            {
                file.RelativePath,
                file.ArtifactKey,
                file.Category
            }
        };

        return JsonSerializer.Serialize(payload, CompactJsonOptions);
    }

    private static string BuildPromptPackageExcerpt(PlannedFileEntry file, string promptPackage)
    {
        if (string.IsNullOrWhiteSpace(promptPackage))
        {
            return string.Empty;
        }

        var sections = new List<string>();

        AddMarkdownSection(sections, promptPackage, "## Non-Negotiable Rules");
        AddMarkdownSection(sections, promptPackage, "## Current Profile");
        AddMarkdownSection(sections, promptPackage, "## Naming Rules");
        AddMarkdownSection(sections, promptPackage, "## Folder Rules");
        AddMarkdownSubsection(sections, promptPackage, "## Artifact Prompt Profiles", $"### {file.ArtifactKey}");

        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static RefinementWorkItem BuildRefinementWorkItem(
        int index,
        PlannedFileEntry file,
        IReadOnlyList<PlannedFileEntry> files,
        StandardProfile profile,
        string promptPackage)
    {
        var artifactContext = BuildArtifactContext(file, files, profile);
        var promptPayload = CreatePromptPayloadContext(file, profile, promptPackage);
        var systemPrompt = BuildSystemPrompt(file, profile, isRepair: false);
        var userPrompt = BuildUserPrompt(file, profile, promptPayload, artifactContext, file.Content, null, isRepair: false);

        return new RefinementWorkItem
        {
            Index = index,
            File = file,
            ArtifactContext = artifactContext,
            PromptPayload = promptPayload,
            EstimatedRequestBytes = EstimateRequestBytes(systemPrompt, userPrompt, null)
        };
    }

    private static RepairWorkItem BuildRepairWorkItem(
        BuildRepairCandidate candidate,
        IReadOnlyList<PlannedFileEntry> repairContextFiles,
        StandardProfile profile,
        string promptPackage)
    {
        var file = candidate.File;
        var artifactContext = BuildArtifactContext(file, repairContextFiles, profile);
        var promptPayload = CreatePromptPayloadContext(file, profile, promptPackage);
        var systemPrompt = BuildSystemPrompt(file, profile, isRepair: true);
        var userPrompt = BuildUserPrompt(
            file,
            profile,
            promptPayload,
            artifactContext,
            file.Content,
            BuildRepairValidationFeedback(candidate),
            isRepair: true);

        return new RepairWorkItem
        {
            Candidate = candidate,
            ArtifactContext = artifactContext,
            PromptPayload = promptPayload,
            EstimatedRequestBytes = EstimateRequestBytes(systemPrompt, userPrompt, null)
        };
    }

    private static int EstimateRequestBytes(string systemPrompt, string userPrompt, string? model)
    {
        var contentBytes = Encoding.UTF8.GetByteCount(systemPrompt) +
                           Encoding.UTF8.GetByteCount(userPrompt) +
                           Encoding.UTF8.GetByteCount(model ?? string.Empty);
        return contentBytes + 1024;
    }

    private static TimeSpan ResolveRequestTimeout(int estimatedRequestBytes)
    {
        var seconds = 45;

        if (estimatedRequestBytes > 10_000)
        {
            seconds += 15;
        }

        if (estimatedRequestBytes > 18_000)
        {
            seconds += 20;
        }

        if (estimatedRequestBytes > 28_000)
        {
            seconds += 30;
        }

        if (estimatedRequestBytes > 40_000)
        {
            seconds += 30;
        }

        return TimeSpan.FromSeconds(Math.Clamp(seconds, 45, 180));
    }

    private static IReadOnlyList<string> GetRelevantArtifacts(string? artifactKey)
    {
        var artifacts = new List<string>();
        if (!string.IsNullOrWhiteSpace(artifactKey))
        {
            artifacts.Add(artifactKey);
        }

        artifacts.AddRange(GetCompanionArtifacts(artifactKey));
        return artifacts
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddMarkdownSection(ICollection<string> sections, string markdown, string heading)
    {
        var section = ExtractMarkdownSection(markdown, heading);
        if (!string.IsNullOrWhiteSpace(section))
        {
            sections.Add(section);
        }
    }

    private static void AddMarkdownSubsection(ICollection<string> sections, string markdown, string parentHeading, string subHeading)
    {
        var section = ExtractMarkdownSubsection(markdown, parentHeading, subHeading);
        if (!string.IsNullOrWhiteSpace(section))
        {
            sections.Add(section);
        }
    }

    private static string ExtractMarkdownSection(string markdown, string heading)
    {
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        var headingMarker = heading + "\n";
        var startIndex = normalized.IndexOf(headingMarker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            startIndex = normalized.IndexOf(heading, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                return string.Empty;
            }
        }

        var nextHeadingIndex = normalized.IndexOf("\n## ", startIndex + heading.Length, StringComparison.Ordinal);
        var length = nextHeadingIndex >= 0
            ? nextHeadingIndex - startIndex
            : normalized.Length - startIndex;
        return normalized.Substring(startIndex, length).Trim();
    }

    private static string ExtractMarkdownSubsection(string markdown, string parentHeading, string subHeading)
    {
        var parentSection = ExtractMarkdownSection(markdown, parentHeading);
        if (string.IsNullOrWhiteSpace(parentSection))
        {
            return string.Empty;
        }

        var normalized = parentSection.Replace("\r\n", "\n", StringComparison.Ordinal);
        var subHeadingMarker = subHeading + "\n";
        var startIndex = normalized.IndexOf(subHeadingMarker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            startIndex = normalized.IndexOf(subHeading, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                return string.Empty;
            }
        }

        var nextSubHeadingIndex = normalized.IndexOf("\n### ", startIndex + subHeading.Length, StringComparison.Ordinal);
        var length = nextSubHeadingIndex >= 0
            ? nextSubHeadingIndex - startIndex
            : normalized.Length - startIndex;
        return normalized.Substring(startIndex, length).Trim();
    }

    private static string BuildSystemPrompt(
        PlannedFileEntry file,
        StandardProfile profile,
        bool isRepair)
    {
        var lines = new List<string>
        {
            isRepair
                ? "You repair generated .NET project files in a fully stateless workflow."
                : "You refine generated .NET project files in a fully stateless workflow.",
            "Treat the supplied profile JSON, learned templates, related generated files, and support assets as authoritative local conventions.",
            "Keep edits proportional to the target artifact and avoid redesigning unrelated parts of the project.",
            BuildArtifactSystemDirective(file.ArtifactKey),
            isRepair
                ? "Resolve compiler or validation feedback first, then preserve the surrounding architecture and style."
                : "Improve convention fidelity and correctness without inventing new architectural patterns.",
            "Return only the full revised file content. Do not include markdown fences."
        };

        if (profile.Logging.Enabled)
        {
            lines.Add("Preserve the project's logging helper and injection style whenever logging appears in this artifact.");
        }

        return string.Join(" ", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string BuildPromptInstructionSection(
        StandardProfile profile,
        PlannedFileEntry file,
        bool isRepair)
    {
        var instructions = profile.Llm.Instructions
            .Where(instruction => !string.IsNullOrWhiteSpace(instruction))
            .ToList();

        instructions.AddRange(GetArtifactPromptInstructions(file.ArtifactKey));

        if (isRepair)
        {
            instructions.Add("Use the validation or build feedback as a bounded repair target; do not rewrite unrelated sections just to satisfy the error.");
        }

        instructions.Add("Keep the result compile-safe for the target file type and consistent with its project scope.");

        return string.Join(
            Environment.NewLine,
            instructions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(instruction => $"- {instruction}"));
    }

    private static string BuildArtifactPromptProfile(string? artifactKey) =>
        artifactKey?.ToLowerInvariant() switch
        {
            "apiprogram" => """
                - Primary role: composition root, hosting, middleware order, service registration.
                - Prioritize: DI registrations, auth, swagger, configuration, and startup sequencing.
                - Avoid: adding new bootstrap abstractions or packages not evidenced by the supplied assets.
                """,
            "controller" or "endpointmodule" => """
                - Primary role: transport layer and HTTP contract surface.
                - Prioritize: routes, attributes, response shape, auth, and delegation to services.
                - Avoid: embedding business rules or persistence details directly in the endpoint layer.
                """,
            "serviceimplementation" or "serviceinterface" => """
                - Primary role: application orchestration and use-case flow.
                - Prioritize: async signatures, validation flow, mapping, logging, and repository/service collaboration.
                - Avoid: controller-specific transport code or direct infrastructure drift beyond current conventions.
                """,
            "repositoryimplementation" or "repositoryinterface" => """
                - Primary role: persistence contract and data access behavior.
                - Prioritize: query patterns, DbContext usage, async data access, and persistence naming conventions.
                - Avoid: leaking business orchestration or HTTP semantics into repository code.
                """,
            "dbcontext" => """
                - Primary role: persistence model registration and provider configuration.
                - Prioritize: entity sets, model configuration, provider alignment, and migration-safe structure.
                - Avoid: introducing configuration styles not supported by the supplied profile or example assets.
                """,
            "entity" => """
                - Primary role: domain model or persistence entity shape.
                - Prioritize: property naming, key conventions, nullability, and lightweight domain structure.
                - Avoid: adding transport annotations or service-layer behavior to entities.
                """,
            "dto" or "createrequest" or "updaterequest" or "response" => """
                - Primary role: API contract model for inbound or outbound transport.
                - Prioritize: serialization-friendly shape, naming consistency, and clear separation from entities.
                - Avoid: persistence-only concerns, DbContext attributes, or business orchestration logic.
                """,
            "unittests" => """
                - Primary role: verify behavior and guard regressions.
                - Prioritize: meaningful assertions, mock setup consistency, and realistic service expectations.
                - Avoid: placeholder assertions, over-mocking, or tests that only validate trivial implementation details.
                """,
            "launchsettings" or "sharedfiles" => """
                - Primary role: support project execution and shared conventions.
                - Prioritize: environment consistency, readable defaults, and compatibility with surrounding generated assets.
                - Avoid: speculative settings or support files unrelated to the current solution template.
                """,
            _ => """
                - Primary role: preserve the artifact's existing responsibility in the generated architecture.
                - Prioritize: local conventions, compile safety, and consistency with supplied examples.
                - Avoid: unrelated structural rewrites.
                """
        };

    private static IReadOnlyList<string> GetArtifactPromptInstructions(string? artifactKey) =>
        artifactKey?.ToLowerInvariant() switch
        {
            "apiprogram" =>
            [
                "Keep middleware order, DI registration patterns, and hosting setup aligned with the profile.",
                "If auth, swagger, or configuration already exist in the prompt context, preserve their existing wiring style."
            ],
            "controller" or "endpointmodule" =>
            [
                "Keep the transport layer thin and delegate business behavior to services.",
                "Preserve the project's route, attribute, and response conventions."
            ],
            "serviceimplementation" or "serviceinterface" =>
            [
                "Preserve async method shapes, dependency injection patterns, and mapping boundaries.",
                "Keep application orchestration here without leaking HTTP or persistence details into the wrong layer."
            ],
            "repositoryimplementation" or "repositoryinterface" =>
            [
                "Follow the existing data-access style from the profile and support assets.",
                "Keep repository code focused on persistence behavior and query semantics."
            ],
            "dbcontext" =>
            [
                "Preserve provider-specific setup and entity registration patterns already shown in the prompt.",
                "Avoid novel model-configuration styles unless the supplied assets clearly use them."
            ],
            "entity" =>
            [
                "Keep property names and nullability aligned with surrounding DTOs, repositories, and schema conventions.",
                "Do not introduce transport-only annotations unless the learned templates already use them."
            ],
            "dto" or "createrequest" or "updaterequest" or "response" =>
            [
                "Preserve contract naming and serializer-friendly structure.",
                "Keep contract models separate from persistence and service implementation concerns."
            ],
            "unittests" =>
            [
                "Prefer behavior-focused assertions over placeholder assertions.",
                "Stay consistent with the project's testing stack and naming style."
            ],
            _ => []
        };

    private static string BuildArtifactSystemDirective(string? artifactKey) =>
        artifactKey?.ToLowerInvariant() switch
        {
            "apiprogram" => "This artifact is the solution's composition root; preserve startup conventions and service wiring.",
            "controller" or "endpointmodule" => "This artifact defines HTTP-facing behavior; preserve endpoint conventions and keep business logic delegated.",
            "serviceimplementation" or "serviceinterface" => "This artifact defines application orchestration; preserve service boundaries and collaboration patterns.",
            "repositoryimplementation" or "repositoryinterface" or "dbcontext" => "This artifact belongs to the persistence layer; preserve data-access conventions and provider alignment.",
            "entity" => "This artifact defines domain or persistence shape; preserve modeling conventions and keep it lightweight.",
            "dto" or "createrequest" or "updaterequest" or "response" => "This artifact defines transport contracts; preserve request/response conventions and serialization-friendly shape.",
            "unittests" => "This artifact defines verification behavior; preserve the project's testing idioms and useful assertions.",
            _ => "Preserve the artifact's established responsibility and the surrounding profile conventions."
        };

    private static string? ValidateRefinedContent(PlannedFileEntry file, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "Refined content was empty.";
        }

        var extension = Path.GetExtension(file.RelativePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        try
        {
            return extension.ToLowerInvariant() switch
            {
                ".cs" => ValidateCSharp(content),
                ".json" => ValidateJson(content),
                ".csproj" or ".props" or ".targets" or ".xml" => ValidateXml(content),
                _ => null
            };
        }
        catch (Exception exception)
        {
            return $"Validation crashed for '{file.RelativePath}': {exception.Message}";
        }
    }

    private static string? ValidateCSharp(string content)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(content);
        var diagnostics = syntaxTree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Take(8)
            .Select(diagnostic =>
            {
                var lineSpan = diagnostic.Location.GetLineSpan();
                var line = lineSpan.StartLinePosition.Line + 1;
                var character = lineSpan.StartLinePosition.Character + 1;
                return $"line {line}, col {character}: {diagnostic.GetMessage()}";
            })
            .ToList();

        return diagnostics.Count == 0
            ? null
            : "C# syntax validation failed: " + string.Join("; ", diagnostics);
    }

    private static string? ValidateJson(string content)
    {
        using var _ = JsonDocument.Parse(content);
        return null;
    }

    private static string? ValidateXml(string content)
    {
        _ = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        return null;
    }

    private static string BuildArtifactContext(
        PlannedFileEntry file,
        IReadOnlyList<PlannedFileEntry> files,
        StandardProfile profile)
    {
        var sections = new List<string>();
        var checklist = BuildArtifactChecklist(file.ArtifactKey);
        if (!string.IsNullOrWhiteSpace(checklist))
        {
            sections.Add($"""
                Artifact Checklist:
                {checklist}
                """);
        }

        if (profile.TemplateOverrides.TryGetValue(file.ArtifactKey, out var templateOverride) &&
            !string.IsNullOrWhiteSpace(templateOverride))
        {
            sections.Add($"""
                Learned Example Template For This Artifact:
                ```text
                {TruncateBlock(templateOverride.Trim(), 4000)}
                ```
                """);
        }

        var relatedFiles = SelectRelatedFiles(file, files);
        if (relatedFiles.Count > 0)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Related Generated Files:");

            foreach (var relatedFile in relatedFiles)
            {
                builder.AppendLine($"- Path: {relatedFile.RelativePath}");
                builder.AppendLine($"  Artifact: {relatedFile.ArtifactKey}");
                builder.AppendLine("  Content:");
                builder.AppendLine("  ```text");
                builder.AppendLine(IndentMultiline(TruncateBlock(relatedFile.Content.Trim(), 1500), "  "));
                builder.AppendLine("  ```");
            }

            sections.Add(builder.ToString().Trim());
        }

        var relevantSharedFiles = SelectRelevantSharedFiles(file, profile);
        if (relevantSharedFiles.Count > 0)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Relevant Support Assets:");

            foreach (var sharedFile in relevantSharedFiles)
            {
                builder.AppendLine($"- Path: {sharedFile.RelativePath}");
                if (!string.IsNullOrWhiteSpace(sharedFile.Description))
                {
                    builder.AppendLine($"  Description: {sharedFile.Description}");
                }

                builder.AppendLine("  Content:");
                builder.AppendLine("  ```text");
                builder.AppendLine(IndentMultiline(TruncateBlock(sharedFile.Template.Trim(), 2000), "  "));
                builder.AppendLine("  ```");
            }

            sections.Add(builder.ToString().Trim());
        }

        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static IReadOnlyList<PlannedFileEntry> SelectRelatedFiles(
        PlannedFileEntry file,
        IReadOnlyList<PlannedFileEntry> files)
    {
        if (files.Count <= 1)
        {
            return [];
        }

        var currentKeywords = ExtractArtifactKeywords(file);
        var currentProjectScope = GetProjectScope(file.RelativePath);
        var currentEntityStem = GetEntityStem(file);

        return files
            .Where(candidate => !ReferenceEquals(candidate, file))
            .Where(candidate => candidate.BinaryContent is null)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Content))
            .Where(candidate => !IsPromptMetaArtifact(candidate.ArtifactKey))
            .Select(candidate => new
            {
                File = candidate,
                Score = ScoreRelatedFile(file, candidate, currentKeywords, currentProjectScope, currentEntityStem)
            })
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .Select(entry => entry.File)
            .ToList();
    }

    private static IReadOnlyList<SharedFileTemplate> SelectRelevantSharedFiles(PlannedFileEntry file, StandardProfile profile)
    {
        if (profile.SharedFiles.Count == 0)
        {
            return [];
        }

        var keywords = ExtractArtifactKeywords(file);
        var contextAssets = profile.Llm.ContextAssets.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return profile.SharedFiles
            .Select(sharedFile => new
            {
                File = sharedFile,
                Score = ScoreSharedFile(sharedFile, keywords, contextAssets)
            })
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(entry => entry.File)
            .ToList();
    }

    private static int ScoreSharedFile(
        SharedFileTemplate sharedFile,
        IReadOnlySet<string> keywords,
        IReadOnlySet<string> contextAssets)
    {
        var score = 0;
        var fileName = Path.GetFileName(sharedFile.RelativePath);
        var searchable = $"{sharedFile.RelativePath} {sharedFile.Description}".ToLowerInvariant();

        if (contextAssets.Contains(fileName))
        {
            score += 3;
        }

        foreach (var keyword in keywords)
        {
            if (searchable.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        return score;
    }

    private static int ScoreRelatedFile(
        PlannedFileEntry currentFile,
        PlannedFileEntry candidateFile,
        IReadOnlySet<string> currentKeywords,
        string currentProjectScope,
        string currentEntityStem)
    {
        var score = 0;
        var candidateKeywords = ExtractArtifactKeywords(candidateFile);
        score += currentKeywords.Intersect(candidateKeywords, StringComparer.OrdinalIgnoreCase).Count();

        if (!string.IsNullOrWhiteSpace(currentProjectScope) &&
            currentProjectScope.Equals(GetProjectScope(candidateFile.RelativePath), StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        var currentDirectory = NormalizeDirectory(Path.GetDirectoryName(currentFile.RelativePath));
        var candidateDirectory = NormalizeDirectory(Path.GetDirectoryName(candidateFile.RelativePath));
        if (!string.IsNullOrWhiteSpace(currentDirectory) &&
            currentDirectory.Equals(candidateDirectory, StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }

        var candidateEntityStem = GetEntityStem(candidateFile);
        if (!string.IsNullOrWhiteSpace(currentEntityStem) &&
            currentEntityStem.Equals(candidateEntityStem, StringComparison.OrdinalIgnoreCase))
        {
            score += 8;
        }

        if (string.Equals(currentFile.Category, candidateFile.Category, StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        if (GetCompanionArtifacts(currentFile.ArtifactKey).Contains(candidateFile.ArtifactKey, StringComparer.OrdinalIgnoreCase))
        {
            score += 5;
        }

        return score;
    }

    private static IReadOnlySet<string> ExtractArtifactKeywords(PlannedFileEntry file)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddKeywords(keywords, file.ArtifactKey);
        AddKeywords(keywords, file.Category);
        AddKeywords(keywords, Path.GetFileNameWithoutExtension(file.RelativePath));

        foreach (var segment in file.RelativePath.Split(new[] { '/', '\\', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.Length >= 3)
            {
                keywords.Add(segment.ToLowerInvariant());
            }
        }

        switch (file.ArtifactKey?.ToLowerInvariant())
        {
            case "apiprogram":
                AddKeywords(keywords, "program", "startup", "dependency", "authentication", "authorization", "swagger", "logging");
                break;
            case "controller":
            case "endpointmodule":
                AddKeywords(keywords, "route", "api", "swagger", "authorize", "logging");
                break;
            case "serviceimplementation":
            case "serviceinterface":
                AddKeywords(keywords, "service", "handler", "logging", "validation");
                break;
            case "repositoryimplementation":
            case "repositoryinterface":
            case "dbcontext":
                AddKeywords(keywords, "repository", "data", "dbcontext", "entityframework", "sql");
                break;
            case "unittests":
                AddKeywords(keywords, "test", "moq", "fluentassertions", "xunit");
                break;
        }

        return keywords;
    }

    private static string GetEntityStem(PlannedFileEntry file)
    {
        var fileName = Path.GetFileNameWithoutExtension(file.RelativePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var normalized = fileName;
        if (normalized.StartsWith("I", StringComparison.Ordinal) &&
            normalized.Length > 1 &&
            char.IsUpper(normalized[1]))
        {
            normalized = normalized[1..];
        }

        foreach (var prefix in new[] { "Create", "Update", "Get", "Set" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && normalized.Length > prefix.Length + 2)
            {
                normalized = normalized[prefix.Length..];
                break;
            }
        }

        foreach (var suffix in new[]
        {
            "Controller",
            "Endpoints",
            "Endpoint",
            "ServiceTests",
            "Tests",
            "Service",
            "Repository",
            "Response",
            "Request",
            "Dto",
            "DbContext",
            "Module"
        })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && normalized.Length > suffix.Length + 1)
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        return normalized.Length >= 3 &&
               normalized is not "Program" and not "appsettings" and not "launchSettings"
            ? normalized
            : string.Empty;
    }

    private static string GetProjectScope(string relativePath)
    {
        var segments = relativePath
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments.Length >= 2 &&
               (segments[0].Equals("src", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals("tests", StringComparison.OrdinalIgnoreCase))
            ? Path.Combine(segments[0], segments[1])
            : segments.FirstOrDefault() ?? string.Empty;
    }

    private static string NormalizeDirectory(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace('\\', '/');

    private static bool IsPromptMetaArtifact(string artifactKey) =>
        artifactKey is "llmPrompt" or "standardProfile";

    private static void AddKeywords(ISet<string> keywords, params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var segment in value.Split(new[] { '/', '\\', '.', '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (segment.Length >= 3)
                {
                    keywords.Add(segment.ToLowerInvariant());
                }
            }
        }
    }

    private static IReadOnlyList<string> GetCompanionArtifacts(string? artifactKey) =>
        artifactKey?.ToLowerInvariant() switch
        {
            "apiprogram" => ["apiProject", "launchSettings", "appSettings", "appSettingsDevelopment", "sharedFiles"],
            "controller" => ["serviceInterface", "serviceImplementation", "dto", "createRequest", "updateRequest", "response", "entity", "repositoryInterface"],
            "endpointmodule" => ["serviceInterface", "serviceImplementation", "dto", "createRequest", "updateRequest", "response", "entity", "repositoryInterface"],
            "serviceimplementation" => ["serviceInterface", "repositoryInterface", "repositoryImplementation", "dto", "createRequest", "updateRequest", "response", "entity", "unitTests", "controller", "endpointModule"],
            "serviceinterface" => ["serviceImplementation", "dto", "createRequest", "updateRequest", "response", "entity", "unitTests"],
            "repositoryimplementation" => ["repositoryInterface", "entity", "dbContext", "serviceImplementation"],
            "repositoryinterface" => ["repositoryImplementation", "entity", "serviceImplementation"],
            "entity" => ["dto", "createRequest", "updateRequest", "response", "repositoryInterface", "repositoryImplementation", "serviceImplementation", "controller", "endpointModule", "unitTests"],
            "dto" => ["entity", "response", "createRequest", "updateRequest", "serviceImplementation", "controller", "endpointModule"],
            "createrequest" => ["updaterequest", "dto", "response", "entity", "serviceImplementation", "controller", "endpointModule"],
            "updaterequest" => ["createrequest", "dto", "response", "entity", "serviceImplementation", "controller", "endpointModule"],
            "response" => ["dto", "entity", "serviceImplementation", "controller", "endpointModule"],
            "unittests" => ["serviceInterface", "serviceImplementation", "repositoryInterface", "dto", "createRequest", "updateRequest", "response", "entity"],
            "dbcontext" => ["entity", "repositoryImplementation", "sharedFiles", "apiProgram"],
            _ => []
        };

    private static string BuildArtifactChecklist(string? artifactKey) =>
        artifactKey?.ToLowerInvariant() switch
        {
            "apiprogram" => """
                - Keep DI registrations, middleware order, swagger wiring, and auth setup aligned with the profile.
                - Do not invent new packages or bootstrap patterns unless the supplied assets clearly show them.
                """,
            "controller" or "endpointmodule" => """
                - Preserve route style, API attributes, response conventions, and auth/logging patterns.
                - Keep transport concerns here and avoid moving business logic into controllers or endpoints.
                """,
            "serviceimplementation" or "serviceinterface" => """
                - Preserve async signatures, dependency injection shape, and validation/logging conventions.
                - Keep orchestration in the service layer and do not leak controller or persistence concerns upward.
                """,
            "repositoryimplementation" or "repositoryinterface" => """
                - Preserve persistence patterns, naming, and data-access conventions from the profile and support assets.
                - Avoid introducing business logic into repository code.
                """,
            "dbcontext" => """
                - Preserve provider-specific setup, entity registrations, and naming conventions.
                - Do not add model configuration styles that are absent from the provided assets.
                """,
            "unittests" => """
                - Use the project's existing xUnit, Moq, and FluentAssertions style when examples are provided.
                - Prefer meaningful assertions over placeholder assertions.
                """,
            _ => string.Empty
        };

    private static string IndentMultiline(string value, string prefix)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => prefix + line));
    }

    private static string TruncateBlock(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + Environment.NewLine + "... [truncated for prompt size]";
    }

    private static Uri ResolveEndpoint(string url)
    {
        var normalized = url.Trim();
        if (!normalized.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.TrimEnd('/') + "/chat/completions";
        }

        return new Uri(normalized, UriKind.Absolute);
    }

    private static string StripCodeFences(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var lines = trimmed.Split('\n').ToList();
        if (lines.Count >= 2 && lines[0].StartsWith("```", StringComparison.Ordinal))
        {
            lines.RemoveAt(0);
        }

        if (lines.Count >= 1 && lines[^1].Trim().Equals("```", StringComparison.Ordinal))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines).Trim();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private static int ResolveMaxConcurrency(
        LlmExecutionSettings settings,
        int targetedCount,
        IEnumerable<int>? estimatedRequestBytes)
    {
        var upperBound = Math.Min(8, Math.Max(1, targetedCount));
        if (settings.MaxConcurrency is int configured)
        {
            return Math.Clamp(configured, 1, upperBound);
        }

        var promptSizes = estimatedRequestBytes?
            .Where(value => value > 0)
            .ToArray() ?? [];
        if (promptSizes.Length == 0)
        {
            return Math.Clamp(4, 1, upperBound);
        }

        var averageBytes = promptSizes.Average();
        var maxBytes = promptSizes.Max();
        var adaptiveDefault = averageBytes switch
        {
            > 36_000 => 2,
            > 24_000 => 3,
            > 14_000 => 4,
            > 9_000 => 5,
            _ => 6
        };

        if (targetedCount >= 10 && averageBytes > 12_000)
        {
            adaptiveDefault = Math.Min(adaptiveDefault, 4);
        }

        if (maxBytes > 48_000)
        {
            adaptiveDefault -= 1;
        }

        return Math.Clamp(adaptiveDefault, 1, upperBound);
    }

    private static int GetArtifactRepairPriority(string? artifactKey) =>
        artifactKey?.ToLowerInvariant() switch
        {
            "apiprogram" => 8,
            "serviceimplementation" => 7,
            "controller" or "endpointmodule" => 6,
            "repositoryimplementation" => 5,
            "dbcontext" => 4,
            "entity" => 4,
            "serviceinterface" or "repositoryinterface" => 3,
            "dto" or "createrequest" or "updaterequest" or "response" => 2,
            "unittests" => 1,
            _ => 2
        };

    private static string FormatBuildError(BuildValidationError error) =>
        $"{error.RelativePath}: {error.Message}";

    private static string NormalizePathKey(string relativePath) =>
        relativePath.Replace('\\', '/');

    private static int GetOriginalIndex(string relativePath, IReadOnlyList<PlannedFileEntry> files)
    {
        for (var index = 0; index < files.Count; index++)
        {
            if (files[index].RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private sealed class BuildRepairResult
    {
        public required IReadOnlyList<PlannedFileEntry> Files { get; init; }
        public required IReadOnlyList<string> Errors { get; init; }
    }

    private sealed class InitialRefinementResult
    {
        public required IReadOnlyList<PlannedFileEntry> Files { get; init; }
        public required IReadOnlyList<string> Errors { get; init; }
        public required int TargetedCount { get; init; }
        public required int EffectiveMaxConcurrency { get; init; }
    }

    private sealed class BuildRepairCandidate
    {
        public required PlannedFileEntry File { get; init; }
        public required IReadOnlyList<BuildValidationError> Errors { get; init; }
        public required string Strategy { get; init; }
    }

    private sealed class BuildRepairAttemptResult
    {
        public PlannedFileEntry? File { get; init; }
        public string? Error { get; init; }
    }

    private sealed class RefinementWorkItem
    {
        public required int Index { get; init; }
        public required PlannedFileEntry File { get; init; }
        public required string ArtifactContext { get; init; }
        public required PromptPayloadContext PromptPayload { get; init; }
        public required int EstimatedRequestBytes { get; init; }
    }

    private sealed class RepairWorkItem
    {
        public required BuildRepairCandidate Candidate { get; init; }
        public required string ArtifactContext { get; init; }
        public required PromptPayloadContext PromptPayload { get; init; }
        public required int EstimatedRequestBytes { get; init; }
    }

    private sealed record PromptPayloadContext(string CompactProfilePayload, string ScopedPromptPackage);

    private sealed class BuildValidationResult
    {
        public required bool Succeeded { get; init; }
        public required IReadOnlyList<BuildValidationError> Errors { get; init; }
    }

    private sealed class BuildTargetExecutionResult
    {
        public required IReadOnlyList<BuildValidationError> Errors { get; init; }
    }

    private sealed class BuildValidationError
    {
        public required string RelativePath { get; init; }
        public required string Message { get; init; }
    }
}
