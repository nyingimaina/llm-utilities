using System.Text.RegularExpressions;

namespace CliSilentProxy.Parsers;

sealed class DotnetBuildParser : IOutputParser
{
    static readonly Regex WarningErrorRegex = new(
        @":\s*(?:(warning|error)\s+(\S+))?\s*:\s*(.*?)$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    static readonly Regex SummaryRegex = new(
        @"^\s*(\d+)\s*(?:Warning\(s\)|Error\(s\)|warning\(s\)|error\(s\))\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex BuiltProjectRegex = new(
        @"^\s*(.+?)\s*->\s*(.+?)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public string Label => "dotnet build";
    public bool CanHandle(string executable, string[] args)
        => executable is "dotnet" && args is ["build", ..];

    public ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd)
    {
        var text = string.Join("\n", lines);
        var issues = new List<object>();
        var warningCount = 0;
        var errorCount = 0;

        foreach (Match m in WarningErrorRegex.Matches(text))
        {
            var severity = m.Groups[1].Value.ToLowerInvariant();
            var code = m.Groups[2].Value;
            var message = m.Groups[3].Value.Trim();
            if (severity == "warning")
            {
                warningCount++;
                issues.Add(new { severity = "warning", code, message, line = ExtractLineFromContext(text, m.Index) });
            }
            else if (severity == "error")
            {
                errorCount++;
                issues.Add(new { severity = "error", code, message, line = ExtractLineFromContext(text, m.Index) });
            }
        }

        // Try to get exact counts from summary line
        var summaryMatches = SummaryRegex.Matches(text);
        if (summaryMatches.Count >= 2)
        {
            // "N Warning(s)" then "N Error(s)" (in either order)
            foreach (Match m in summaryMatches)
            {
                var label = m.Value.ToLowerInvariant();
                var val = int.Parse(m.Groups[1].Value);
                if (label.Contains("warning")) warningCount = val;
                if (label.Contains("error")) errorCount = val;
            }
        }

        // Get built projects
        var builtProjects = new List<string>();
        foreach (Match m in BuiltProjectRegex.Matches(text))
            builtProjects.Add(m.Groups[2].Value.Trim());

        var data = new Dictionary<string, object?>
        {
            ["issues"] = issues,
            ["warning_count"] = warningCount,
            ["error_count"] = errorCount,
            ["built_projects"] = builtProjects,
            ["succeeded"] = exitCode == 0
        };

        var summary = errorCount > 0
            ? $"Build FAILED. {errorCount} error(s), {warningCount} warning(s)"
            : warningCount > 0
                ? $"Build succeeded. {warningCount} warning(s)"
                : "Build succeeded.";

        return new ParsedResult(Label, summary, data);
    }

    static int ExtractLineFromContext(string text, int index)
    {
        // Find the line number from preceding context (file:line:col format)
        var before = text[..index];
        var lastLine = before.LastIndexOf('\n');
        var linePart = lastLine >= 0 ? before[(lastLine + 1)..] : before;
        var match = Regex.Match(linePart, @"\((\d+),");
        if (match.Success) return int.Parse(match.Groups[1].Value);
        match = Regex.Match(linePart, @":(\d+):");
        if (match.Success) return int.Parse(match.Groups[1].Value);
        return 0;
    }
}
