using System.Text.RegularExpressions;

namespace CliSilentProxy.Parsers;

sealed class FlutterDartAnalyzeParser : IOutputParser
{
    static readonly Regex IssueRegex = new(
        @"^\s*(error|warning|info)\s*[•\-]\s*(.*?)\s*[•\-]\s*(.*?)(?:\s*[•\-]\s*(\S+))?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    static readonly Regex CountRegex = new(
        @"(\d+)\s+issues?\s+found|No issues found",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Label => "analyze";
    public bool CanHandle(string executable, string[] args)
        => executable is "flutter" or "dart" && args is ["analyze", ..];

    public ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd)
    {
        var text = string.Join("\n", lines);
        var issues = new List<object>();
        var errorCount = 0; var warningCount = 0; var infoCount = 0;

        foreach (Match m in IssueRegex.Matches(text))
        {
            var severity = m.Groups[1].Value.ToLowerInvariant();
            var message = m.Groups[2].Value.Trim();
            var location = m.Groups[3].Value.Trim();
            var code = m.Groups[4].Success ? m.Groups[4].Value.Trim() : null;

            if (severity == "error") errorCount++;
            else if (severity == "warning") warningCount++;
            else infoCount++;

            // Extract line from location (file:line[:col])
            int line = 0;
            var locParts = location.Split(':');
            if (locParts.Length >= 2) int.TryParse(locParts[1], out line);

            issues.Add(new { severity, message, code, location, line });
        }

        var data = new Dictionary<string, object?>
        {
            ["issues"] = issues,
            ["error_count"] = errorCount,
            ["warning_count"] = warningCount,
            ["info_count"] = infoCount,
            ["total"] = issues.Count
        };

        var summary = issues.Count == 0
            ? "No issues found"
            : $"{errorCount} errors, {warningCount} warnings, {infoCount} info";

        return new ParsedResult(Label, summary, data);
    }
}
