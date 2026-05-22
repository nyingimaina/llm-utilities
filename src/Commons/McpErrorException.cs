namespace LLMUtilities.Commons;

public class McpErrorException : Exception
{
    public McpErrorException(string errorJson) : base(errorJson) { }
}
