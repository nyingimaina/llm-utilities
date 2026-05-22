using System.Text.Json.Serialization;

namespace LLMUtilities.Commons;

public sealed record JsonRpcError
{
    [JsonPropertyName("code")]    public int Code { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("data")]    public object? Data { get; init; }
}
