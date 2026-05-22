using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMUtilities.Commons;

public sealed record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
    [JsonPropertyName("id")]      public object? Id { get; init; }
    [JsonPropertyName("method")]  public string Method { get; init; } = "";
    [JsonPropertyName("params")]  public JsonElement? Params { get; init; }
}
