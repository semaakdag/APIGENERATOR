using ApiGenerator.Cli.Analyzers;
using ApiGenerator.Cli.Generators;
using ApiGenerator.Cli.Llm;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

var options = BenchOptions.Parse(args);
var solutionRoot = Path.GetFullPath(options.SolutionRoot);
var manifestPath = options.ManifestPath is null
    ? Path.Combine(solutionRoot, "generation-manifest.json")
    : Path.GetFullPath(options.ManifestPath);
var profilePath = options.ProfilePath is null
    ? Path.Combine(solutionRoot, "docs", "STANDARD-PROFILE.json")
    : Path.GetFullPath(options.ProfilePath);
var promptPath = Path.Combine(solutionRoot, "docs", "LLM-STANDARD-PROMPT.md");

if (!Directory.Exists(solutionRoot))
{
    throw new DirectoryNotFoundException($"Solution root was not found: '{solutionRoot}'.");
}

if (!File.Exists(manifestPath))
{
    throw new FileNotFoundException("Generation manifest was not found.", manifestPath);
}

if (!File.Exists(profilePath))
{
    throw new FileNotFoundException("Standard profile was not found.", profilePath);
}

var serializer = new StandardProfileSerializer();
var profile = await serializer.LoadAsync(profilePath);
var promptPackage = File.Exists(promptPath)
    ? await File.ReadAllTextAsync(promptPath)
    : string.Empty;
var files = await ManifestBackedSolutionLoader.LoadAsync(solutionRoot, manifestPath);
var targetArtifacts = profile.Llm.ApplyToArtifacts.Count > 0
    ? profile.Llm.ApplyToArtifacts.ToHashSet(StringComparer.OrdinalIgnoreCase)
    : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
var targetedFiles = files.Count(file => targetArtifacts.Contains(file.ArtifactKey));
var comparisonValues = options.GetComparisonValues();
var repairTargetCount = options.MockRepairCount > 0
    ? files.Count(IsRepairEligible)
    : 0;

Console.WriteLine($"Solution root: {solutionRoot}");
Console.WriteLine($"Mode: {options.Mode}");
Console.WriteLine($"Generated files loaded: {files.Count}");
Console.WriteLine($"Refine target files: {targetedFiles}");
Console.WriteLine($"Comparison set: {string.Join(", ", comparisonValues.Select(GetConcurrencyLabel))}");
if (options.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase) && options.MockRepairCount > 0)
{
    Console.WriteLine($"Mock build-repair targets requested: {options.MockRepairCount} (eligible: {repairTargetCount})");
}

if (targetedFiles == 0)
{
    Console.WriteLine("No target files matched the current profile. Nothing to benchmark.");
    return;
}

var results = new List<BenchRunResult>();
foreach (var concurrency in comparisonValues)
{
    var runResult = await RunBenchAsync(files, profile, promptPackage, options, concurrency);
    results.Add(runResult);

    Console.WriteLine();
    var concurrencyLabel = runResult.EffectiveConcurrency > 0 && runResult.ConfiguredConcurrency != runResult.EffectiveConcurrency
        ? $"{runResult.ConfiguredConcurrencyLabel} (effective {runResult.EffectiveConcurrency})"
        : runResult.ConfiguredConcurrencyLabel;
    Console.WriteLine($"Concurrency {concurrencyLabel}: elapsed={runResult.Elapsed.TotalSeconds:F2}s, requests={runResult.RequestCount}, refined={runResult.RefinedFiles}/{runResult.TargetFiles}, errors={runResult.ErrorCount}");
    Console.WriteLine($"Concurrency {concurrencyLabel}: per-target={runResult.Elapsed.TotalMilliseconds / Math.Max(1, runResult.TargetFiles):F0} ms");
    Console.WriteLine($"Concurrency {concurrencyLabel}: avg-request={runResult.AverageRequestBytes / 1024d:F1} KB, max-request={runResult.MaxRequestBytes / 1024d:F1} KB");
    if (runResult.Errors.Count > 0)
    {
        foreach (var error in runResult.Errors.Take(5))
        {
            Console.WriteLine($"  error: {error}");
        }
    }
}

if (results.Count >= 2)
{
    var baseline = results[0];
    foreach (var current in results.Skip(1))
    {
        var speedup = baseline.Elapsed.TotalMilliseconds / current.Elapsed.TotalMilliseconds;
        Console.WriteLine($"Speedup {baseline.ConfiguredConcurrencyLabel} -> {current.ConfiguredConcurrencyLabel}: {speedup:F2}x");
    }
}

static async Task<BenchRunResult> RunBenchAsync(
    IReadOnlyList<PlannedFileEntry> files,
    StandardProfile profile,
    string promptPackage,
    BenchOptions options,
    int concurrency)
{
    var mockRepairTargets = options.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase)
        ? SelectMockRepairTargets(files, options.MockRepairCount)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using var handler = options.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase)
        ? new StatefulMockLlmHandler(options.MockLatencyMs, mockRepairTargets)
        : null;
    using var httpClient = handler is null
        ? new HttpClient { Timeout = TimeSpan.FromMinutes(5) }
        : new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
    var refiner = new OpenAiCompatibleLlmRefiner(httpClient);
    var settings = new LlmExecutionSettings
    {
        Enabled = true,
        Url = ResolveUrl(options),
        Model = ResolveModel(options),
        Token = ResolveToken(options),
        MaxConcurrency = concurrency > 0 ? concurrency : null
    };

    var stopwatch = Stopwatch.StartNew();
    var result = await refiner.RefineAsync(files, profile, promptPackage, settings);
    stopwatch.Stop();

    return new BenchRunResult
    {
        ConfiguredConcurrency = concurrency,
        ConfiguredConcurrencyLabel = GetConcurrencyLabel(concurrency),
        EffectiveConcurrency = result.Summary.EffectiveMaxConcurrency,
        Elapsed = stopwatch.Elapsed,
        RequestCount = handler?.RequestCount ?? result.Summary.TargetFiles,
        TargetFiles = result.Summary.TargetFiles,
        RefinedFiles = result.Summary.RefinedFiles,
        ErrorCount = result.Summary.Errors.Count,
        Errors = result.Summary.Errors,
        TotalRequestBytes = handler?.TotalRequestBytes ?? 0,
        MaxRequestBytes = handler?.MaxRequestBytes ?? 0
    };
}

static string GetConcurrencyLabel(int concurrency) =>
    concurrency <= 0 ? "auto" : concurrency.ToString();

static string ResolveUrl(BenchOptions options) =>
    options.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase)
        ? "https://mock-llm.local/v1"
        : options.LlmUrl ?? throw new InvalidOperationException("Live mode requires --llm-url.");

static string ResolveModel(BenchOptions options) =>
    options.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase)
        ? "mock-stateless-refiner"
        : options.LlmModel ?? throw new InvalidOperationException("Live mode requires --llm-model.");

static string ResolveToken(BenchOptions options)
{
    if (options.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase))
    {
        return "mock-token";
    }

    var token = !string.IsNullOrWhiteSpace(options.LlmToken)
        ? options.LlmToken
        : Environment.GetEnvironmentVariable(options.TokenEnvVar);
    if (string.IsNullOrWhiteSpace(token))
    {
        throw new InvalidOperationException($"Live mode requires --llm-token or the '{options.TokenEnvVar}' environment variable.");
    }

    return token;
}

static IReadOnlySet<string> SelectMockRepairTargets(
    IReadOnlyList<PlannedFileEntry> files,
    int mockRepairCount)
{
    if (mockRepairCount <= 0)
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    return files
        .Where(IsRepairEligible)
        .Select(file => NormalizeBenchRelativePath(file.RelativePath))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(mockRepairCount)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

static bool IsRepairEligible(PlannedFileEntry file) =>
    file.BinaryContent is null &&
    file.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
    !string.IsNullOrWhiteSpace(file.Content) &&
    !string.IsNullOrWhiteSpace(file.ArtifactKey);

static string NormalizeBenchRelativePath(string path) =>
    path.Replace('\\', '/');

internal sealed class BenchOptions
{
    public string SolutionRoot { get; init; } = Path.Combine("artifacts", "GeneratedApiWithProjectDocFresh");
    public string? ManifestPath { get; init; }
    public string? ProfilePath { get; init; }
    public string Mode { get; init; } = "mock";
    public int MockLatencyMs { get; init; } = 700;
    public string? LlmUrl { get; init; }
    public string? LlmModel { get; init; }
    public string? LlmToken { get; init; }
    public string TokenEnvVar { get; init; } = "API_GENERATOR_LLM_TOKEN";
    public string Compare { get; init; } = "1,4";
    public int MockRepairCount { get; init; }

    public IReadOnlyList<int> GetComparisonValues() =>
        Compare.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => int.TryParse(value, out var parsed) ? parsed : 0)
            .Where(static value => value >= 0)
            .Distinct()
            .ToArray();

    public static BenchOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = current[2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            values[key] = value;
        }

        return new BenchOptions
        {
            SolutionRoot = values.TryGetValue("solution-root", out var solutionRoot) ? solutionRoot : Path.Combine("artifacts", "GeneratedApiWithProjectDocFresh"),
            ManifestPath = values.TryGetValue("manifest", out var manifestPath) ? manifestPath : null,
            ProfilePath = values.TryGetValue("profile", out var profilePath) ? profilePath : null,
            Mode = values.TryGetValue("mode", out var mode) ? mode : "mock",
            MockLatencyMs = values.TryGetValue("mock-latency-ms", out var latencyText) && int.TryParse(latencyText, out var latency) ? latency : 700,
            LlmUrl = values.TryGetValue("llm-url", out var llmUrl) ? llmUrl : null,
            LlmModel = values.TryGetValue("llm-model", out var llmModel) ? llmModel : null,
            LlmToken = values.TryGetValue("llm-token", out var llmToken) ? llmToken : null,
            TokenEnvVar = values.TryGetValue("token-env", out var tokenEnv) ? tokenEnv : "API_GENERATOR_LLM_TOKEN",
            Compare = values.TryGetValue("compare", out var compare) ? compare : "1,4",
            MockRepairCount = values.TryGetValue("mock-repair-count", out var repairCountText) && int.TryParse(repairCountText, out var repairCount)
                ? Math.Max(repairCount, 0)
                : 0
        };
    }
}

internal static class ManifestBackedSolutionLoader
{
    public static async Task<IReadOnlyList<PlannedFileEntry>> LoadAsync(string solutionRoot, string manifestPath)
    {
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<GenerationManifestModel>(manifestJson, JsonOptions())
            ?? throw new InvalidOperationException("Generation manifest could not be parsed.");
        var files = new List<PlannedFileEntry>(manifest.GeneratedFiles.Count);

        foreach (var generatedFile in manifest.GeneratedFiles)
        {
            var absolutePath = Path.Combine(solutionRoot, generatedFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath))
            {
                continue;
            }

            if (IsBinaryFile(absolutePath))
            {
                files.Add(new PlannedFileEntry
                {
                    RelativePath = NormalizeRelativePath(generatedFile.RelativePath),
                    Category = generatedFile.Category,
                    ArtifactKey = InferArtifactKey(generatedFile.RelativePath, generatedFile.Category),
                    Content = string.Empty,
                    BinaryContent = await File.ReadAllBytesAsync(absolutePath)
                });
                continue;
            }

            files.Add(new PlannedFileEntry
            {
                RelativePath = NormalizeRelativePath(generatedFile.RelativePath),
                Category = generatedFile.Category,
                ArtifactKey = InferArtifactKey(generatedFile.RelativePath, generatedFile.Category),
                Content = await File.ReadAllTextAsync(absolutePath)
            });
        }

        return files;
    }

    private static string InferArtifactKey(string relativePath, string category)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        var fileName = Path.GetFileName(normalizedPath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalizedPath);

        if (normalizedPath.Equals("docs/STANDARD-PROFILE.json", StringComparison.OrdinalIgnoreCase))
        {
            return "standardProfile";
        }

        if (normalizedPath.Equals("docs/LLM-STANDARD-PROMPT.md", StringComparison.OrdinalIgnoreCase))
        {
            return "llmPrompt";
        }

        if (fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return "solution";
        }

        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return InferProjectArtifactKey(normalizedPath);
        }

        if (fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
        {
            return "apiProgram";
        }

        if (fileName.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase))
        {
            return "launchSettings";
        }

        if (fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
        {
            return "appSettings";
        }

        if (fileName.Equals("appsettings.Development.json", StringComparison.OrdinalIgnoreCase))
        {
            return "appSettingsDevelopment";
        }

        if (fileName.Equals("AppDbContext.cs", StringComparison.OrdinalIgnoreCase))
        {
            return "dbContext";
        }

        if (fileNameWithoutExtension.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
        {
            return "controller";
        }

        if (fileNameWithoutExtension.EndsWith("Endpoints", StringComparison.OrdinalIgnoreCase) ||
            fileNameWithoutExtension.EndsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
        {
            return "endpointModule";
        }

        if (fileNameWithoutExtension.StartsWith("Create", StringComparison.OrdinalIgnoreCase) &&
            fileNameWithoutExtension.EndsWith("Request", StringComparison.OrdinalIgnoreCase))
        {
            return "createRequest";
        }

        if (fileNameWithoutExtension.StartsWith("Update", StringComparison.OrdinalIgnoreCase) &&
            fileNameWithoutExtension.EndsWith("Request", StringComparison.OrdinalIgnoreCase))
        {
            return "updateRequest";
        }

        if (fileNameWithoutExtension.EndsWith("Response", StringComparison.OrdinalIgnoreCase))
        {
            return "response";
        }

        if (fileNameWithoutExtension.EndsWith("Dto", StringComparison.OrdinalIgnoreCase))
        {
            return "dto";
        }

        if (fileNameWithoutExtension.EndsWith("ServiceTests", StringComparison.OrdinalIgnoreCase) ||
            fileNameWithoutExtension.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
        {
            return "unitTests";
        }

        if (normalizedPath.Contains("/Services/", StringComparison.OrdinalIgnoreCase))
        {
            return fileNameWithoutExtension.StartsWith("I", StringComparison.Ordinal) && fileNameWithoutExtension.Length > 1 && char.IsUpper(fileNameWithoutExtension[1])
                ? "serviceInterface"
                : "serviceImplementation";
        }

        if (normalizedPath.Contains("/Repositories/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/Persistence/", StringComparison.OrdinalIgnoreCase))
        {
            return fileNameWithoutExtension.StartsWith("I", StringComparison.Ordinal) && fileNameWithoutExtension.Length > 1 && char.IsUpper(fileNameWithoutExtension[1])
                ? "repositoryInterface"
                : "repositoryImplementation";
        }

        if (normalizedPath.Contains("/Entities/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/Models/", StringComparison.OrdinalIgnoreCase))
        {
            return "entity";
        }

        if (category.Equals("documentation", StringComparison.OrdinalIgnoreCase))
        {
            return "documentation";
        }

        return category;
    }

    private static string InferProjectArtifactKey(string relativePath)
    {
        if (relativePath.Contains(".Api/", StringComparison.OrdinalIgnoreCase))
        {
            return "apiProject";
        }

        if (relativePath.Contains(".Application/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains(".App/", StringComparison.OrdinalIgnoreCase))
        {
            return "applicationProject";
        }

        if (relativePath.Contains(".Infrastructure/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains(".DataAccess/", StringComparison.OrdinalIgnoreCase))
        {
            return "infrastructureProject";
        }

        if (relativePath.Contains(".Domain/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains(".Entities/", StringComparison.OrdinalIgnoreCase))
        {
            return "domainProject";
        }

        if (relativePath.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains(".UnitTests/", StringComparison.OrdinalIgnoreCase))
        {
            return "testProject";
        }

        return "project";
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/');

    private static bool IsBinaryFile(string absolutePath)
    {
        var extension = Path.GetExtension(absolutePath);
        return extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true
    };
}

internal sealed class StatefulMockLlmHandler(int latencyMs, IReadOnlySet<string> repairTargets) : HttpMessageHandler
{
    private readonly int latencyMs = Math.Max(latencyMs, 0);
    private readonly IReadOnlySet<string> repairTargets = repairTargets;
    private readonly HashSet<string> brokenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> originalContentByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object brokenPathsLock = new();
    private int requestCount;
    private long totalRequestBytes;
    private int maxRequestBytes;

    public int RequestCount => requestCount;
    public long TotalRequestBytes => Interlocked.Read(ref totalRequestBytes);
    public int MaxRequestBytes => Volatile.Read(ref maxRequestBytes);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requestCount);
        if (latencyMs > 0)
        {
            await Task.Delay(latencyMs, cancellationToken);
        }

        var requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
        var requestBytes = Encoding.UTF8.GetByteCount(requestJson);
        Interlocked.Add(ref totalRequestBytes, requestBytes);
        UpdateMaxRequestBytes(requestBytes);
        using var document = JsonDocument.Parse(requestJson);
        var userPrompt = document.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString() ?? string.Empty;
        var relativePath = ExtractRelativePath(userPrompt);
        var currentFileContent = ExtractCurrentFileContent(userPrompt);
        var responseContent = currentFileContent;

        if (!string.IsNullOrWhiteSpace(relativePath) && repairTargets.Contains(relativePath))
        {
            var isRepairRequest = userPrompt.Contains("Validation Feedback From Previous Attempt:", StringComparison.Ordinal);
            if (isRepairRequest)
            {
                lock (brokenPathsLock)
                {
                    responseContent = originalContentByPath.TryGetValue(relativePath, out var originalContent)
                        ? originalContent
                        : currentFileContent;
                }
            }
            else
            {
                lock (brokenPathsLock)
                {
                    if (brokenPaths.Add(relativePath))
                    {
                        originalContentByPath[relativePath] = currentFileContent;
                        responseContent = InjectBuildFailure(currentFileContent);
                    }
                }
            }
        }

        var response = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = responseContent
                    }
                }
            }
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        };
    }

    private static string ExtractRelativePath(string userPrompt)
    {
        const string marker = "- Relative path:";
        var markerIndex = userPrompt.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var lineStart = markerIndex + marker.Length;
        var lineEnd = userPrompt.IndexOf('\n', lineStart);
        var path = lineEnd >= 0
            ? userPrompt[lineStart..lineEnd]
            : userPrompt[lineStart..];
        return NormalizePath(path.Trim());
    }

    private static string ExtractCurrentFileContent(string userPrompt)
    {
        const string marker = "Current File Content:";
        var markerIndex = userPrompt.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var contentSlice = userPrompt[(markerIndex + marker.Length)..];
        var fenceStart = contentSlice.IndexOf("```text", StringComparison.Ordinal);
        if (fenceStart < 0)
        {
            return string.Empty;
        }

        var blockStart = fenceStart + "```text".Length;
        var fenceEnd = contentSlice.IndexOf("```", blockStart, StringComparison.Ordinal);
        if (fenceEnd < 0)
        {
            return string.Empty;
        }

        return contentSlice[blockStart..fenceEnd].Trim('\r', '\n');
    }

    private static string InjectBuildFailure(string currentFileContent)
    {
        if (string.IsNullOrWhiteSpace(currentFileContent))
        {
            return currentFileContent;
        }

        const string marker = "using __CodexBenchBroken = Missing.Benchmark.Dependency;";
        if (currentFileContent.Contains(marker, StringComparison.Ordinal))
        {
            return currentFileContent;
        }

        return marker + Environment.NewLine + currentFileContent;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private void UpdateMaxRequestBytes(int requestBytes)
    {
        var snapshot = Volatile.Read(ref maxRequestBytes);
        while (requestBytes > snapshot)
        {
            var original = Interlocked.CompareExchange(ref maxRequestBytes, requestBytes, snapshot);
            if (original == snapshot)
            {
                return;
            }

            snapshot = original;
        }
    }
}

internal sealed class BenchRunResult
{
    public required int ConfiguredConcurrency { get; init; }
    public required string ConfiguredConcurrencyLabel { get; init; }
    public required int EffectiveConcurrency { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required int RequestCount { get; init; }
    public required int TargetFiles { get; init; }
    public required int RefinedFiles { get; init; }
    public required int ErrorCount { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required long TotalRequestBytes { get; init; }
    public required int MaxRequestBytes { get; init; }
    public long AverageRequestBytes => RequestCount == 0 ? 0 : TotalRequestBytes / RequestCount;
}

internal sealed class GenerationManifestModel
{
    public IReadOnlyList<ManifestGeneratedFile> GeneratedFiles { get; init; } = [];
}

internal sealed class ManifestGeneratedFile
{
    public string RelativePath { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
}
