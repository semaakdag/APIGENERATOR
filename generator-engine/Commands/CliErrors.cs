using System.Text.Json;

namespace ApiGenerator.Cli.Commands;

/// <summary>Invalid user input (paths, options, schema, profile, templates). Exit code 2.</summary>
public class CliInputException : Exception
{
    public CliInputException(string message)
        : base(message)
    {
    }
}

/// <summary>Generation stopped because existing files conflict and overwrite mode is Fail. Exit code 3.</summary>
public sealed class GenerationConflictException : Exception
{
    public GenerationConflictException(string message)
        : base(message)
    {
    }
}

public static class CliExitCodes
{
    public const int Success = 0;
    public const int Unexpected = 1;
    public const int InvalidInput = 2;
    public const int Conflict = 3;

    public static int FromException(Exception exception) => exception switch
    {
        GenerationConflictException => Conflict,
        CliInputException or FileNotFoundException or DirectoryNotFoundException or JsonException => InvalidInput,
        _ => Unexpected
    };

    public static string Category(int exitCode) => exitCode switch
    {
        InvalidInput => "invalid-input",
        Conflict => "conflict",
        _ => "unexpected"
    };
}
