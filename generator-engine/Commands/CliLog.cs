using System.Text.Json;

namespace ApiGenerator.Cli.Commands;

public enum LogFormat
{
    Text,
    Json
}

/// <summary>
/// Console output for all commands. Text mode prints plain lines; JSON mode prints one JSON object per line
/// (<c>level</c>, <c>event</c>, <c>message</c> plus event data) so tools can parse progress and results.
/// </summary>
public static class CliLog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static LogFormat Format { get; set; } = LogFormat.Text;

    public static void Info(string eventName, string message, object? data = null) => Write(Console.Out, "info", eventName, message, data);

    public static void Warning(string eventName, string message, object? data = null) =>
        Write(Console.Out, "warning", eventName, Format == LogFormat.Text ? $"warning: {message}" : message, data);

    public static void Error(string eventName, string message, object? data = null) => Write(Console.Error, "error", eventName, message, data);

    private static void Write(TextWriter writer, string level, string eventName, string message, object? data)
    {
        if (Format == LogFormat.Text)
        {
            writer.WriteLine(message);
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["level"] = level,
            ["event"] = eventName,
            ["message"] = message
        };

        if (data is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(data, JsonOptions).EnumerateObject())
            {
                payload[property.Name] = property.Value;
            }
        }

        writer.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
