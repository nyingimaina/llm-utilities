using System.Text.RegularExpressions;

namespace CliSilentProxy.Parsers;

sealed class RuffParser : IOutputParser
{
    static readonly Regex RuffIssueRegex = new(
        @"^(.+?):(\d+):(\d+):\s+(\S+)\s+(.*?)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex CountRegex = new(
        @"Found\s+(\d+)\s+error",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Label => "ruff";
    public bool CanHandle(string executable, string[] args)
        => executable is "ruff" || executable is "uvx" && args.Length > 0 && args[0].Contains("ruff");

    public ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd)
    {
        var text = string.Join("\n", lines);
        var issues = new List<object>();

        foreach (Match m in RuffIssueRegex.Matches(text))
        {
            var file = m.Groups[1].Value.Trim();
            var line = int.Parse(m.Groups[2].Value);
            var col = int.Parse(m.Groups[3].Value);
            var code = m.Groups[4].Value;
            var message = m.Groups[5].Value.Trim();

            issues.Add(new { file, line, col, code, message });
        }

        if (issues.Count == 0) return null;

        var data = new Dictionary<string, object?>
        {
            ["issues"] = issues,
            ["total"] = issues.Count,
            ["error_count"] = issues.Count
        };

        var summary = $"{issues.Count} issue(s) found";
        return new ParsedResult(Label, summary, data);
    }
}
