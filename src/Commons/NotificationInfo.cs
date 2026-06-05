using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMUtilities.Commons;

public record NotificationInfo
{
    public string Server { get; init; } = "";
    public string? LlmClient { get; init; }
    public string Tool { get; init; } = "";
    public string? ArgsJson { get; init; }
    public string? ArgsDisplay { get; init; }
    public string? Project { get; init; }
    public string? Cwd { get; init; }
    public long ElapsedMs { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public int? GroupCount { get; init; }
    public List<NotificationInfo>? Grouped { get; init; }
    public string? Summary { get; init; }
    public string? ResultHint { get; init; }

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
}
