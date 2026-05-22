using System.Text.RegularExpressions;

namespace CliSilentProxy.Parsers;

sealed class NpmAuditParser : IOutputParser
{
    static readonly Regex PackageHeaderRegex = new(
        @"^(\S+)\s+(<|<=|=|>|>=)\s+[\d.]+",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex SeverityRegex = new(
        @"Severity:\s*(critical|high|moderate|low)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex VulnCountRegex = new(
        @"(\d+)\s+vulnerabilit",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Label => "npm audit";
    public bool CanHandle(string executable, string[] args)
        => executable is "npm" && args.Length > 0 && args[0] == "audit";

    public ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd)
    {
        var text = string.Join("\n", lines);
        if (!text.Contains("vulnerabilit") && !text.Contains("Vulnerabilities"))
            return exitCode == 0
                ? new ParsedResult(Label, "No vulnerabilities found", new Dictionary<string, object?> { ["vulnerabilities"] = Array.Empty<object>(), ["total"] = 0 })
                : null;

        var vulns = new List<object>();
        string currentPackage = "";
        string currentSeverity = "";

        foreach (var line in lines)
        {
            var pkg = PackageHeaderRegex.Match(line);
            if (pkg.Success)
            {
                if (!string.IsNullOrEmpty(currentPackage) && !string.IsNullOrEmpty(currentSeverity))
                    vulns.Add(new { package = currentPackage, severity = currentSeverity });
                currentPackage = pkg.Groups[1].Value;
                currentSeverity = "";
                continue;
            }
            var sev = SeverityRegex.Match(line);
            if (sev.Success)
                currentSeverity = sev.Groups[1].Value.ToLowerInvariant();
        }

        if (!string.IsNullOrEmpty(currentPackage) && !string.IsNullOrEmpty(currentSeverity))
            vulns.Add(new { package = currentPackage, severity = currentSeverity });

        var total = 0;
        var countM = VulnCountRegex.Match(text);
        if (countM.Success) total = int.Parse(countM.Groups[1].Value);

        var data = new Dictionary<string, object?>
        {
            ["vulnerabilities"] = vulns,
            ["total"] = total
        };

        var summary = total == 0 ? "No vulnerabilities found" : $"{total} vulnerabilities: {string.Join(", ", vulns.Select(v => ((dynamic)v).severity).Distinct())}";
        return new ParsedResult(Label, summary, data);
    }
}
