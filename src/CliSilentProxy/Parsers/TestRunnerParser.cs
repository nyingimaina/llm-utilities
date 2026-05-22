using System.Text.RegularExpressions;

namespace CliSilentProxy.Parsers;

sealed class TestRunnerParser : IOutputParser
{
    static readonly Regex JestSummaryRegex = new(
        @"Tests:\s*(?:(\d+)\s+failed,\s*)?(?:(\d+)\s+passed,\s*)?(\d+)\s+total",
        RegexOptions.Compiled);

    static readonly Regex JestFailedTestRegex = new(
        @"^\s*●\s+(.*?)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex PytestSummaryRegex = new(
        @"(?:failed|passed|skipped|error)",
        RegexOptions.Compiled);

    static readonly Regex PytestShortSummaryRegex = new(
        @"^=+\s+(.*?)\s+in\s+[\d.]+\w+\s*=\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex CargoTestSummaryRegex = new(
        @"test result:\s*(FAILED|ok)\.\s*(\d+)\s+passed;\s*(\d+)\s+failed;\s*(\d+)\s+ignored;\s*(\d+)\s+measured;\s*(\d+)\s+filtered",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex CargoFailedTestRegex = new(
        @"^\s+(\S+)\s+\.\.\.\s+FAILED\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex GoTestOkRegex = new(
        @"^ok\s+(\S+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex GoTestFailRegex = new(
        @"^---\s+FAIL:\s+(.*?)\s+\((.*?)\)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly Regex FlutterDartTestSummaryRegex = new(
        @"([\d.]+)\s*[+]\s*(\d+)\s+.*?[–-]\s*(\d+)",
        RegexOptions.Compiled);

    static readonly Regex FlutterDartTestFailedRegex = new(
        @"^\s*[✗××]\s+(.*?)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Detect which runner based on output patterns
    enum Runner { Jest, Pytest, CargoTest, GoTest, FlutterDartTest, Unknown }

    public string Label => "test";
    public bool CanHandle(string executable, string[] args)
    {
        var exe = executable.ToLowerInvariant();
        return exe is "jest" or "npx" or "pytest" or "python" or "python3" or "cargo" or "go" or "flutter" or "dart"
            && (exe != "npx" || (args.Length > 0 && args[0] is "jest" or "vitest"))
            && (exe != "python" && exe != "python3" || (args.Length > 0 && args[0] is "-m" && args.Length > 1 && args[1] == "pytest"))
            && (exe != "cargo" || (args.Length > 0 && args[0] == "test"))
            && (exe != "go" || (args.Length > 0 && args[0] == "test"))
            && (exe != "flutter" || (args.Length > 0 && args[0] == "test"))
            && (exe != "dart" || (args.Length > 0 && args[0] == "test"))
            // Also match vitest directly
            || exe is "vitest";
    }

    public ParsedResult? Parse(string[] lines, int exitCode, long durationMs, string cmd)
    {
        var text = string.Join("\n", lines);
        var runner = DetectRunner(lines);

        return runner switch
        {
            Runner.Jest => ParseJest(text, lines),
            Runner.Pytest => ParsePytest(text, lines),
            Runner.CargoTest => ParseCargo(text),
            Runner.GoTest => ParseGoTest(text),
            Runner.FlutterDartTest => ParseFlutterDartTest(text),
            _ => null
        };
    }

    Runner DetectRunner(string[] lines)
    {
        var text = string.Join("\n", lines);
        if (text.Contains("Tests:") && text.Contains("Snapshots:")) return Runner.Jest;
        if (text.Contains("test result:") && text.Contains("FAILED")) return Runner.CargoTest;
        if (text.Contains("test result:") && text.Contains("ok.")) return Runner.CargoTest;
        if (text.Contains("FAIL:") && Regex.IsMatch(text, @"^---\s+FAIL:", RegexOptions.Multiline)) return Runner.GoTest;
        if (text.Contains("ok  \t") || text.Contains("FAIL\t")) return Runner.GoTest;
        if (text.Contains("+") && (text.Contains("–") || text.Contains("-"))) return Runner.FlutterDartTest;
        if (text.Contains("passed") && text.Contains("failed") && text.Contains("warnings")) return Runner.Pytest;
        if (text.Contains("==") && text.Contains("passed") && text.Contains("in ")) return Runner.Pytest;
        if (text.Contains("short test summary")) return Runner.Pytest;
        return Runner.Unknown;
    }

    ParsedResult? ParseJest(string text, string[] lines)
    {
        var sum = JestSummaryRegex.Match(text);
        if (!sum.Success) return null;

        var failed = int.Parse(sum.Groups[1].Success ? sum.Groups[1].Value : "0");
        var passed = int.Parse(sum.Groups[2].Success ? sum.Groups[2].Value : "0");
        var total = int.Parse(sum.Groups[3].Value);

        var failedTests = new List<string>();
        foreach (Match m in JestFailedTestRegex.Matches(text))
        {
            var testName = m.Groups[1].Value.Trim();
            if (testName.Length > 0 && testName.Length < 200)
                failedTests.Add(testName);
        }

        var data = new Dictionary<string, object?>
        {
            ["outcome"] = failed > 0 ? "Failed" : "Passed",
            ["total"] = total,
            ["passed"] = passed,
            ["failed"] = failed,
            ["failed_tests"] = failedTests
        };

        var summary = $"{passed} passed, {failed} failed, {total} total";
        return new ParsedResult("jest", summary, data, ShouldSuppressTail: true);
    }

    ParsedResult? ParsePytest(string text, string[] lines)
    {
        // Parse "X passed, Y failed, Z skipped in Ns"
        var summaryLine = "";
        foreach (var line in lines)
        {
            if (line.Contains("passed") && line.Contains("failed"))
            {
                summaryLine = line;
                break;
            }
        }
        if (string.IsNullOrEmpty(summaryLine)) return null;

        var passed = 0; var failed = 0; var skipped = 0;
        var passedM = Regex.Match(summaryLine, @"(\d+)\s+passed");
        var failedM = Regex.Match(summaryLine, @"(\d+)\s+failed");
        var skippedM = Regex.Match(summaryLine, @"(\d+)\s+skipped");

        if (passedM.Success) passed = int.Parse(passedM.Groups[1].Value);
        if (failedM.Success) failed = int.Parse(failedM.Groups[1].Value);
        if (skippedM.Success) skipped = int.Parse(skippedM.Groups[1].Value);

        // Extract failed test names from short test summary
        var failedTests = new List<string>();
        var inSummary = false;
        foreach (var line in lines)
        {
            if (line.Contains("short test summary")) { inSummary = true; continue; }
            if (inSummary && line.TrimStart().StartsWith("FAILED "))
            {
                var name = line.TrimStart()["FAILED ".Length..].Trim();
                // name is typically "path::test_name"
                failedTests.Add(name);
            }
        }

        var data = new Dictionary<string, object?>
        {
            ["outcome"] = failed > 0 ? "Failed" : "Passed",
            ["total"] = passed + failed + skipped,
            ["passed"] = passed,
            ["failed"] = failed,
            ["skipped"] = skipped,
            ["failed_tests"] = failedTests
        };

        var summary = $"{passed} passed, {failed} failed{(skipped > 0 ? $", {skipped} skipped" : "")}";
        return new ParsedResult("pytest", summary, data, ShouldSuppressTail: true);
    }

    ParsedResult? ParseCargo(string text)
    {
        var summary = CargoTestSummaryRegex.Match(text);
        if (!summary.Success) return null;

        var outcome = summary.Groups[1].Value;
        var passed = int.Parse(summary.Groups[2].Value);
        var failed = int.Parse(summary.Groups[3].Value);
        var ignored = int.Parse(summary.Groups[4].Value);
        var measured = int.Parse(summary.Groups[5].Value);
        var filtered = int.Parse(summary.Groups[6].Value);

        var failedTests = new List<string>();
        foreach (Match m in CargoFailedTestRegex.Matches(text))
            failedTests.Add(m.Groups[1].Value);

        var data = new Dictionary<string, object?>
        {
            ["outcome"] = outcome == "FAILED" ? "Failed" : "Passed",
            ["total"] = passed + failed + ignored + measured,
            ["passed"] = passed,
            ["failed"] = failed,
            ["ignored"] = ignored,
            ["filtered_out"] = filtered,
            ["failed_tests"] = failedTests
        };

        var summaryStr = $"{passed} passed, {failed} failed" +
            (ignored > 0 ? $", {ignored} ignored" : "") +
            (measured > 0 ? $", {measured} measured" : "");

        return new ParsedResult("cargo test", summaryStr, data, ShouldSuppressTail: true);
    }

    ParsedResult? ParseGoTest(string text)
    {
        var failed = 0; var passed = 0;
        var failedTests = new List<string>();
        var packages = new List<string>();

        foreach (Match m in GoTestOkRegex.Matches(text))
        {
            passed++;
            packages.Add(m.Groups[1].Value);
        }

        foreach (Match m in GoTestFailRegex.Matches(text))
        {
            failed++;
            failedTests.Add(m.Groups[1].Value);
            var pkgLine = "";
            // Find the package from preceding lines
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("FAIL\t") || line.StartsWith("ok  \t"))
                {
                    var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) pkgLine = parts[1].Trim();
                }
            }
        }

        var data = new Dictionary<string, object?>
        {
            ["outcome"] = failed > 0 ? "Failed" : "Passed",
            ["total"] = passed + failed,
            ["passed"] = passed,
            ["failed"] = failed,
            ["failed_tests"] = failedTests,
            ["packages"] = packages
        };

        var summaryStr = $"{passed} packages passed" + (failed > 0 ? $", {failed} failures" : "");
        return new ParsedResult("go test", summaryStr, data, ShouldSuppressTail: true);
    }

    ParsedResult? ParseFlutterDartTest(string text)
    {
        // Look for: "+N -M: Some tests failed." or final line with counts
        var failed = 0; var passed = 0;

        // Parse final summary line
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains("test") && (line.Contains("+") || line.Contains("-")))
            {
                var plusM = Regex.Match(line, @"\+(\d+)");
                var minusM = Regex.Match(line, @"[–-](\d+)");
                if (plusM.Success) passed = int.Parse(plusM.Groups[1].Value);
                if (minusM.Success) failed = int.Parse(minusM.Groups[1].Value);
            }
        }

        var failedTests = new List<string>();
        foreach (Match m in FlutterDartTestFailedRegex.Matches(text))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length > 0 && name.Length < 200)
                failedTests.Add(name);
        }

        var data = new Dictionary<string, object?>
        {
            ["outcome"] = failed > 0 ? "Failed" : "Passed",
            ["total"] = passed + failed,
            ["passed"] = passed,
            ["failed"] = failed,
            ["failed_tests"] = failedTests
        };

        var summaryStr = $"{passed} passed, {failed} failed";
        return new ParsedResult("flutter/dart test", summaryStr, data, ShouldSuppressTail: true);
    }
}
