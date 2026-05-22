using System.Text.Json.Serialization;

namespace LLMUtilities.Commons;

public sealed record JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; } = "2.0";
    [JsonPropertyName("id")]      public object? Id { get; }
    [JsonPropertyName("result")]  public object? Result { get; }
    [JsonPropertyName("error")]   public JsonRpcError? Error { get; }

    public JsonRpcResponse(object? id, object? result) => (Id, Result) = (id, result);
    public JsonRpcResponse(object? id, JsonRpcError error) => (Id, Error) = (id, error);
}
