using System.Text.RegularExpressions;

namespace CliSilentProxy.Parsers;

sealed class MypyParser : IOutputParser
{
    static readonly Regex IssueRegex = new(
        @"^(.+?):(\d+):\s*(error|note|warning):\s*(.*?)(?:\s+\[(\S+)\])?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex FoundRegex = new(
        @"Found\s+(\d+)\s+error",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Label => "mypy";
    public bool CanHandle(string executable, string[] args)
        => executable is "mypy" || executable is "python" or "python3" && args.Length > 1 && args[0] == "-m" && args[1] == "mypy";

    public ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd)
    {
        var text = string.Join("\n", lines);
        var issues = new List<object>();
        var errorCount = 0; var noteCount = 0;

        foreach (Match m in IssueRegex.Matches(text))
        {
            var file = m.Groups[1].Value.Trim();
            var line = int.Parse(m.Groups[2].Value);
            var severity = m.Groups[3].Value.ToLowerInvariant();
            var message = m.Groups[4].Value.Trim();
            var code = m.Groups[5].Success ? m.Groups[5].Value.Trim() : null;

            if (severity == "error") errorCount++;
            else noteCount++;

            issues.Add(new { file, line, severity, message, code });
        }

        if (issues.Count == 0) return null;

        var data = new Dictionary<string, object?>
        {
            ["issues"] = issues,
            ["error_count"] = errorCount,
            ["note_count"] = noteCount,
            ["total"] = issues.Count
        };

        var summary = $"{errorCount} error(s), {noteCount} note(s)";
        return new ParsedResult(Label, summary, data);
    }
}
