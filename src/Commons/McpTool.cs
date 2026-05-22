using System.Text.Json.Serialization;

namespace LLMUtilities.Commons;

public sealed record McpTool
{
    [JsonPropertyName("name")]        public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("inputSchema")] public object InputSchema { get; init; } = new { };
}
