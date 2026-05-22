using System.Text.RegularExpressions;

namespace CliSilentProxy.Parsers;

sealed class GoVetParser : IOutputParser
{
    static readonly Regex IssueRegex = new(
        @"^\./(.+?):(\d+):(\d+):\s*(.*?)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex PackageHeaderRegex = new(
        @"^#\s+(\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public string Label => "go vet";
    public bool CanHandle(string executable, string[] args)
        => executable is "go" && args.Length > 0 && args[0] == "vet";

    public ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd)
    {
        var text = string.Join("\n", lines);
        var issues = new List<object>();
        string currentPackage = "";

        foreach (var line in lines)
        {
            var pkg = PackageHeaderRegex.Match(line);
            if (pkg.Success) { currentPackage = pkg.Groups[1].Value; continue; }

            var m = IssueRegex.Match(line);
            if (m.Success)
            {
                var file = m.Groups[1].Value;
                var lineNum = int.Parse(m.Groups[2].Value);
                var col = int.Parse(m.Groups[3].Value);
                var message = m.Groups[4].Value.Trim();
                issues.Add(new { file = "./" + file, line = lineNum, col, package = currentPackage, message });
            }
        }

        if (issues.Count == 0 && exitCode == 0) return null;

        var data = new Dictionary<string, object?>
        {
            ["issues"] = issues,
            ["total"] = issues.Count,
            ["error_count"] = issues.Count
        };

        var summary = issues.Count == 0 ? "No issues found" : $"{issues.Count} issue(s) found";
        return new ParsedResult(Label, summary, data);
    }
}
