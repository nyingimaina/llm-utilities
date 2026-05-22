using System.Text.RegularExpressions;

namespace CliSilentProxy.Parsers;

sealed class TscParser : IOutputParser
{
    static readonly Regex TscIssueRegex = new(
        @"^(.+?)\((\d+),(\d+)\)\s*:\s*(error|warning)\s+(\S+)\s*:\s*(.*?)$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    static readonly Regex FoundCountRegex = new(
        @"Found\s+(\d+)\s+error",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Label => "tsc";
    public bool CanHandle(string executable, string[] args)
        => executable is "tsc" or "tsc" || executable.Contains("tsc");

    public ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd)
    {
        var text = string.Join("\n", lines);
        var errors = new List<object>();
        var warnings = new List<object>();

        foreach (Match m in TscIssueRegex.Matches(text))
        {
            var file = m.Groups[1].Value.Trim();
            var line = int.Parse(m.Groups[2].Value);
            var col = int.Parse(m.Groups[3].Value);
            var severity = m.Groups[4].Value.ToLowerInvariant();
            var code = m.Groups[5].Value;
            var message = m.Groups[6].Value.Trim();

            var entry = new { file, line, col, code, message };
            if (severity == "error") errors.Add(entry);
            else warnings.Add(entry);
        }

        var data = new Dictionary<string, object?>
        {
            ["errors"] = errors,
            ["warnings"] = warnings,
            ["error_count"] = errors.Count,
            ["warning_count"] = warnings.Count
        };

        if (errors.Count == 0 && warnings.Count == 0)
            return null; // not actually a tsc run or no output

        var summary = errors.Count > 0
            ? $"{errors.Count} error(s), {warnings.Count} warning(s)"
            : $"{warnings.Count} warning(s)";

        return new ParsedResult(Label, summary, data);
    }
}
