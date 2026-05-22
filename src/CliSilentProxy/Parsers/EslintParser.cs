using System.Text.RegularExpressions;

namespace CliSilentProxy.Parsers;

sealed class EslintParser : IOutputParser
{
    static readonly Regex FileHeaderRegex = new(
        @"^(.+?\.(?:ts|tsx|js|jsx|mjs|cjs|vue|svelte))\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex IssueRegex = new(
        @"^\s+(\d+):(\d+)\s+(error|warning)\s+(\S+)\s+(.*?)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex CountRegex = new(
        @"(\d+)\s+problems?(?:\s+\((\d+)\s+errors?,\s+(\d+)\s+warnings?\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Label => "eslint";
    public bool CanHandle(string executable, string[] args)
        => executable is "eslint" || executable.Contains("eslint") || executable is "npx" && args is ["eslint", ..];

    public ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd)
    {
        var text = string.Join("\n", lines);
        var issues = new List<object>();
        var errorCount = 0; var warningCount = 0;
        var currentFile = "";

        // Track file changes (lines ending in .ts/.tsx/.js/.jsx etc.)
        foreach (var line in lines)
        {
            var fileMatch = FileHeaderRegex.Match(line);
            if (fileMatch.Success && !line.StartsWith(" "))
            {
                currentFile = fileMatch.Groups[1].Value;
                continue;
            }

            var issueMatch = IssueRegex.Match(line);
            if (issueMatch.Success)
            {
                var lineNum = int.Parse(issueMatch.Groups[1].Value);
                var col = int.Parse(issueMatch.Groups[2].Value);
                var severity = issueMatch.Groups[3].Value.ToLowerInvariant();
                var rule = issueMatch.Groups[4].Value;
                var message = issueMatch.Groups[5].Value.Trim();

                if (severity == "error") errorCount++;
                else warningCount++;

                issues.Add(new { file = currentFile, line = lineNum, col, severity, rule, message });
            }
        }

        if (issues.Count == 0) return null;

        var data = new Dictionary<string, object?>
        {
            ["issues"] = issues,
            ["error_count"] = errorCount,
            ["warning_count"] = warningCount,
            ["total"] = issues.Count
        };

        var summary = $"{errorCount} errors, {warningCount} warnings";
        return new ParsedResult(Label, summary, data);
    }
}
