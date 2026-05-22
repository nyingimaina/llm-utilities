using System.Text.RegularExpressions;

namespace CliSilentProxy.Parsers;

sealed class CargoParser : IOutputParser
{
    static readonly Regex ErrorRegex = new(
        @"^error\[(\S+)\]:\s*(.*?)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex WarningRegex = new(
        @"^warning:\s*(.*?)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex LocationRegex = new(
        @"^\s*-->\s+(.+?):(\d+):(\d+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex AbortDueRegex = new(
        @"error:\s+aborting due to (\d+) previous error",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex FinishedRegex = new(
        @"Finished\s+`\S+`\s+profile",
        RegexOptions.Compiled);

    public string Label => "cargo";
    public bool CanHandle(string executable, string[] args)
        => executable is "cargo" or "rustup"
        && args.Length > 0 && args[0] is "build" or "check" or "clippy";

    public ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd)
    {
        var text = string.Join("\n", lines);
        var issues = new List<object>();
        var errorCount = 0; var warningCount = 0;
        var currentCode = ""; var currentMsg = "";
        var items = new List<(string Code, string Message, string File, int Line, int Col, bool IsError)>();

        foreach (var line in lines)
        {
            var e = ErrorRegex.Match(line);
            if (e.Success)
            {
                currentCode = e.Groups[1].Value;
                currentMsg = e.Groups[2].Value.Trim();
                errorCount++;
                var loc = FindNextLocation(lines, Array.IndexOf(lines, e.Groups[0].Value));
                items.Add((currentCode, currentMsg, loc.File, loc.Line, loc.Col, true));
                continue;
            }
            var w = WarningRegex.Match(line);
            if (w.Success)
            {
                currentCode = "";
                currentMsg = w.Groups[1].Value.Trim();
                warningCount++;
                var loc = FindNextLocation(lines, Array.IndexOf(lines, w.Groups[0].Value));
                items.Add((currentCode, currentMsg, loc.File, loc.Line, loc.Col, false));
            }
        }

        // Deduplicate by message
        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            if (seen.Add(item.Message))
                issues.Add(new { severity = item.IsError ? "error" : "warning", code = item.Code, message = item.Message, file = item.File, line = item.Line, col = item.Col });
        }

        if (issues.Count == 0 && exitCode == 0) return null;

        var data = new Dictionary<string, object?>
        {
            ["issues"] = issues,
            ["error_count"] = errorCount,
            ["warning_count"] = warningCount,
            ["total"] = issues.Count,
            ["succeeded"] = exitCode == 0
        };

        var summary = exitCode != 0
            ? $"Build FAILED. {errorCount} errors, {warningCount} warnings"
            : warningCount > 0
                ? $"Build succeeded. {warningCount} warnings"
                : "Build succeeded.";

        return new ParsedResult(Label, summary, data);
    }

    static (string File, int Line, int Col) FindNextLocation(string[] lines, int startIndex)
    {
        for (int i = startIndex + 1; i < lines.Length && i < startIndex + 20; i++)
        {
            var m = LocationRegex.Match(lines[i]);
            if (m.Success)
                return (m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        }
        return ("", 0, 0);
    }
}
