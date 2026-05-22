namespace CliSilentProxy;

interface IOutputParser
{
    string Label { get; }
    bool CanHandle(string executable, string[] args);
    ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd);
}

record ParsedResult(
    string Label,
    string? Summary,
    Dictionary<string, object?>? Data,
    bool ShouldSuppressTail = false
);
