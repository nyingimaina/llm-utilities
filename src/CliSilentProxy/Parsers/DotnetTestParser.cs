using System.Text.RegularExpressions;

namespace CliSilentProxy.Parsers;

sealed class DotnetTestParser : IOutputParser
{
    static readonly Regex AssemblyHeaderRegex = new(
        @"^Test run for\s+(.*?)\.dll",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex IndividualTestRegex = new(
        @"^\s*(Passed|Failed|Skipped)\s+(.*?)\s+\[",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex SummaryRegex = new(
        @"(Passed|Failed)!?\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)",
        RegexOptions.Compiled);

    static readonly Regex SimpleSummaryRegex = new(
        @"^\s*(?:Passed|Failed)!?.*?(\d+)\s+(?:passed|failed).*?(\d+)\s+total",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public string Label => "dotnet test";
    public bool CanHandle(string executable, string[] args)
        => executable is "dotnet" && args is ["test", ..];

    public ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd)
    {
        var text = string.Join("\n", lines);
        var passed = 0; var failed = 0; var skipped = 0; var total = 0;
        var failedTests = new List<string>();
        var passedTests = new List<string>();

        foreach (Match m in IndividualTestRegex.Matches(text))
        {
            var status = m.Groups[1].Value;
            var testName = m.Groups[2].Value.Trim();
            if (status == "Passed") { passedTests.Add(testName); passed++; }
            else if (status == "Failed") { failedTests.Add(testName); failed++; }
            else if (status == "Skipped") skipped++;
            total++;
        }

        var summaryMatch = SummaryRegex.Match(text);
        if (summaryMatch.Success)
        {
            failed = int.Parse(summaryMatch.Groups[2].Value);
            passed = int.Parse(summaryMatch.Groups[3].Value);
            skipped = int.Parse(summaryMatch.Groups[4].Value);
            total = int.Parse(summaryMatch.Groups[5].Value);
        }

        var assemblies = new List<object>();
        foreach (Match m in AssemblyHeaderRegex.Matches(text))
            assemblies.Add(m.Groups[1].Value);

        var data = new Dictionary<string, object?>
        {
            ["outcome"] = failed > 0 ? "Failed" : "Passed",
            ["total"] = total,
            ["passed"] = passed,
            ["failed"] = failed,
            ["skipped"] = skipped,
            ["failed_tests"] = failedTests,
            ["passed_tests"] = passedTests,
            ["assemblies"] = assemblies
        };

        var summary = $"{passed} passed, {failed} failed{(skipped > 0 ? $", {skipped} skipped" : "")}, {total} total";
        return new ParsedResult(Label, summary, data, ShouldSuppressTail: true);
    }
}
