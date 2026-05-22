using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CliSilentProxy;

sealed class RunEngine
{
    readonly Dictionary<int, FailedRun> _failedRuns = new();
    readonly Queue<int> _insertOrder = new();
    readonly object _storeLock = new();
    int _nextId = 1;
    const int MaxStoredRuns = 10;

    public RunResult Run(string command, string[] args, string? workDir, int tailLines, int timeoutMs, string? filterPattern, int filterContext = 0, string? extractPattern = null, string? tailMode = null, bool surfaceWarnings = false, int? maxOutputTokens = null, string[]? extraPaths = null)
    {
        var resolvedCmd = ResolveExecutable(command);
        var lines = new List<string>();
        var linesLock = new object();

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedCmd,
            WorkingDirectory = workDir ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        if (extraPaths is { Length: > 0 })
        {
            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var extra = string.Join(Path.PathSeparator.ToString(), extraPaths);
            startInfo.EnvironmentVariables["PATH"] = extra + Path.PathSeparator + existingPath;
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (linesLock) lines.Add(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (linesLock) lines.Add(e.Data); };

        var sw = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exited = process.WaitForExit(timeoutMs);
        var timedOut = !exited;

        if (timedOut)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        else
        {
            // Flush async read buffers -- required after WaitForExit(timeout)
            process.WaitForExit();
        }

        sw.Stop();
        var exitCode = timedOut ? -1 : process.ExitCode;

        string[] rawLines = [];
        lock (linesLock) rawLines = lines.ToArray();

        var cmd = args.Length > 0
            ? $"{command} {string.Join(" ", args)}"
            : command;

        // Try output parser
        var parser = OutputParserRegistry.Find(resolvedCmd, args);
        ParsedResult? parsed = null;
        if (parser is not null)
        {
            try { parsed = parser.Parse(rawLines, exitCode, sw.ElapsedMilliseconds, cmd); }
            catch { /* parser failed, fall through to normal behavior */ }
        }

        // surfaceWarnings on success — extract warning lines without full failure tail
        string[]? surfaceWarningsDigest = null;
        if (surfaceWarnings && exitCode == 0 && !timedOut)
        {
            var warnCompressed = LogCompressor.Compress(rawLines, workDir);
            var warnLines = warnCompressed
                .Where(l => l.Contains("warning", StringComparison.OrdinalIgnoreCase)
                         || l.Contains("warn", StringComparison.OrdinalIgnoreCase)
                         || l.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .Take(20)
                .ToArray();
            surfaceWarningsDigest = warnLines.Length > 0 ? warnLines : null;
            if (parsed is null && string.IsNullOrEmpty(extractPattern))
                return new RunResult(exitCode, sw.ElapsedMilliseconds, false, cmd, null, null, null, null, false,
                    SurfaceWarnings: surfaceWarningsDigest);
        }

        // Fast path: success without extractPattern, no parser, no surfaceWarnings
        if (parsed is null && exitCode == 0 && !timedOut && string.IsNullOrEmpty(extractPattern))
            return new RunResult(exitCode, sw.ElapsedMilliseconds, false, cmd, null, null, null, null, false);

        // Slow path: compress for extraction or failure analysis
        var compressed = LogCompressor.Compress(rawLines, workDir);

        // Success with extractPattern
        if (exitCode == 0 && !timedOut && !string.IsNullOrEmpty(extractPattern))
        {
            var exRegex = new Regex(extractPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Dictionary<string, string>? extracted = null;
            foreach (var line in compressed)
            {
                var match = exRegex.Match(line);
                if (!match.Success) continue;
                extracted = [];
                foreach (var groupName in exRegex.GetGroupNames())
                {
                    if (groupName != "0" && match.Groups[groupName].Success)
                        extracted[groupName] = match.Groups[groupName].Value;
                }
                break;
            }
            extracted ??= [];
            return new RunResult(exitCode, sw.ElapsedMilliseconds, false, cmd, null, null, null, null, false,
                Extracted: extracted, Parsed: parsed);
        }

        var exitContext = compressed.Length >= 3 ? compressed[^3..] : compressed;

        var tailLinesList = new List<string>();
        int? patternMatches = null;
        bool isFiltered = false;
        string? hint = null;
        bool truncated = false;

        if (!string.IsNullOrEmpty(filterPattern))
        {
            isFiltered = true;
            var regex = new Regex(filterPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var matchIndices = Enumerable.Range(0, compressed.Length)
                .Where(i => regex.IsMatch(compressed[i])).ToList();
            patternMatches = matchIndices.Count;

            // Take up to tailLines matches (last ones for recency)
            var recentMatches = matchIndices.Count > tailLines
                ? matchIndices[^tailLines..] : matchIndices;
            truncated = matchIndices.Count > tailLines;

            if (filterContext > 0)
            {
                // Build context windows around each match, merging overlapping
                var windows = new List<(int Start, int End)>();
                foreach (var idx in recentMatches)
                {
                    var ws = Math.Max(0, idx - filterContext);
                    var we = Math.Min(compressed.Length - 1, idx + filterContext);
                    if (windows.Count > 0 && ws <= windows[^1].End + 1)
                        windows[^1] = (windows[^1].Start, Math.Max(windows[^1].End, we));
                    else
                        windows.Add((ws, we));
                }

                foreach (var (ws, we) in windows)
                {
                    var countInWindow = recentMatches.Count(idx => idx >= ws && idx <= we);
                    tailLinesList.Add($"// --- {countInWindow} match(es) ---");
                    for (int i = ws; i <= we; i++)
                        tailLinesList.Add(compressed[i]);
                }
            }
            else
            {
                foreach (var idx in recentMatches)
                    tailLinesList.Add(compressed[idx]);
            }

            tailLinesList.Add("// -- exit context (unfiltered) --");
            tailLinesList.AddRange(exitContext);
        }
        else if (tailMode == "first_error")
        {
            var firstErrorRegex = new Regex("(?i)(error|exception|fatal|panic)", RegexOptions.Compiled);
            int firstErrorIdx = -1;
            for (int i = 0; i < compressed.Length; i++)
            {
                if (firstErrorRegex.IsMatch(compressed[i]))
                {
                    firstErrorIdx = i;
                    break;
                }
            }

            if (firstErrorIdx < 0)
            {
                var take = Math.Min(compressed.Length, tailLines);
                if (take > 0) tailLinesList.AddRange(compressed[^take..]);
                truncated = compressed.Length > tailLines;
                hint = "first_error: no error line found, fell back to tail";
            }
            else
            {
                var contextHalf = tailLines / 2;
                var ws = Math.Max(0, firstErrorIdx - contextHalf);
                var we = Math.Min(compressed.Length - 1, firstErrorIdx + contextHalf);
                var summaryStart = Math.Max(0, compressed.Length - 5);

                if (we >= summaryStart - 1)
                {
                    for (int i = ws; i < compressed.Length; i++)
                        tailLinesList.Add(compressed[i]);
                }
                else
                {
                    for (int i = ws; i <= we; i++)
                        tailLinesList.Add(compressed[i]);
                    tailLinesList.Add("// -- closing summary --");
                    for (int i = summaryStart; i < compressed.Length; i++)
                        tailLinesList.Add(compressed[i]);
                }

                if (tailLinesList.Count > tailLines)
                {
                    tailLinesList = tailLinesList.Take(tailLines).ToList();
                    truncated = true;
                }
            }
        }
        else
        {
            var take = Math.Min(compressed.Length, tailLines);
            if (take > 0) tailLinesList.AddRange(compressed[^take..]);
            truncated = compressed.Length > tailLines;
        }

        if (timedOut)
            tailLinesList.Add("[no further output before timeout]");

        var tail = tailLinesList.ToArray();

        // maxOutputTokens: intelligently truncate tail to token budget
        if (maxOutputTokens.HasValue && tail.Length > maxOutputTokens.Value && exitCode != 0)
        {
            // Estimate: ~4 chars per token. Cap at maxOutputTokens * 4 chars.
            var charBudget = maxOutputTokens.Value * 4;
            var totalChars = tail.Sum(l => l.Length + 1); // +1 for newline
            if (totalChars > charBudget)
            {
                // Keep first error block + last N lines
                var firstErrIdx = Array.FindIndex(tail, l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                                                          || l.Contains("exception", StringComparison.OrdinalIgnoreCase)
                                                          || l.Contains("failed", StringComparison.OrdinalIgnoreCase));
                int takeFirst, takeLast;
                if (firstErrIdx >= 0 && firstErrIdx < tail.Length / 2)
                {
                    // Keep error context + closing lines
                    var errorBlock = Math.Min(charBudget / 6, tail.Length - firstErrIdx);
                    takeFirst = Math.Min(firstErrIdx + errorBlock, tail.Length);
                    takeLast = Math.Min(charBudget / 6, tail.Length);
                }
                else
                {
                    takeFirst = Math.Min(charBudget / 8, tail.Length);
                    takeLast = Math.Min(charBudget / 8, tail.Length);
                }

                var newTail = new List<string>();
                if (takeFirst > 0) newTail.AddRange(tail.Take(takeFirst));
                if (takeFirst + takeLast < tail.Length)
                {
                    newTail.Add($"// ... [{tail.Length - takeFirst - takeLast} lines omitted by maxOutputTokens={maxOutputTokens.Value}] ...");
                    newTail.AddRange(tail.Skip(tail.Length - takeLast).Take(takeLast));
                }
                tail = newTail.ToArray();
                truncated = true;
            }
        }

        int runId;
        lock (_storeLock)
        {
            runId = _nextId++;
            if (_failedRuns.Count >= MaxStoredRuns)
            {
                var oldest = _insertOrder.Dequeue();
                _failedRuns.Remove(oldest);
            }
            _failedRuns[runId] = new FailedRun(runId, cmd, rawLines, compressed);
            _insertOrder.Enqueue(runId);
        }

        return new RunResult(exitCode, sw.ElapsedMilliseconds, timedOut, cmd,
            runId, tail, tail.Length, compressed.Length, truncated,
            isFiltered, patternMatches, compressed.Length,
            Hint: hint, Parsed: parsed);
    }

    public FailedRun? GetLog(int id)
    {
        lock (_storeLock)
            return _failedRuns.TryGetValue(id, out var run) ? run : null;
    }

    static string ResolveExecutable(string command)
    {
        if (string.IsNullOrEmpty(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return command;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null) return command;

        var extensions = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? new[] { "", ".exe", ".cmd", ".bat", ".ps1" }
            : new[] { "" };

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var full = Path.Combine(dir.Trim(), command + ext);
                if (File.Exists(full))
                    return Path.GetFullPath(full);
            }
        }
        return command;
    }
}

record RunResult(
    int ExitCode,
    long DurationMs,
    bool TimedOut,
    string Cmd,
    int? RunId,
    string[]? Tail,
    int? TailLines,
    int? TotalLines,
    bool Truncated,
    bool IsFiltered = false,
    int? PatternMatches = null,
    int? PatternTotal = null,
    Dictionary<string, string>? Extracted = null,
    string? Hint = null,
    ParsedResult? Parsed = null,
    string[]? SurfaceWarnings = null
);

record FailedRun(int Id, string Cmd, string[] Raw, string[] Compressed);

static class LogCompressor
{
    // Matches ANSI/VT escape sequences (colors, cursor movement, etc.)
    static readonly Regex AnsiRegex = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-9;]*[ -/]*[@-~])",
        RegexOptions.Compiled);

    public static string[] Compress(string[] raw, string? workDir)
    {
        var result = new List<string>(raw.Length);

        foreach (var rawLine in raw)
        {
            // 1. Strip \r-terminated progress lines: keep text after the last \r
            var line = rawLine.Contains('\r')
                ? rawLine.Split('\r')[^1]
                : rawLine;

            // 2. Strip ANSI escape codes
            line = AnsiRegex.Replace(line, "");

            // 3. Skip blank lines
            if (string.IsNullOrWhiteSpace(line)) continue;

            // 4. Normalize absolute workDir paths to relative
            if (!string.IsNullOrEmpty(workDir))
                line = NormalizePath(line, workDir);

            result.Add(line);
        }

        // 5. Collapse consecutive identical lines -> "line (xN)"
        var deduped = CollapseConsecutiveDuplicates(result);

        // 6. Fuzzy collapse: runs of 3+ lines sharing ≥5 char prefix
        return FuzzyCollapse(deduped);
    }

    static string NormalizePath(string line, string workDir)
    {
        // Try both slash variants to handle mixed-separator output
        var fwd = workDir.Replace('\\', '/');
        var bwd = workDir.Replace('/', '\\');

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        line = ReplaceAll(line, fwd + "/",  "./",  comparison);
        line = ReplaceAll(line, fwd + "\\", ".\\", comparison);
        line = ReplaceAll(line, bwd + "\\", ".\\", comparison);
        line = ReplaceAll(line, bwd + "/",  "./",  comparison);
        line = ReplaceAll(line, fwd, ".", comparison);
        if (!string.Equals(fwd, bwd, comparison))
            line = ReplaceAll(line, bwd, ".", comparison);

        return line;
    }

    static string ReplaceAll(string input, string oldValue, string newValue, StringComparison comparison)
    {
        if (oldValue.Length == 0 || !input.Contains(oldValue, comparison))
            return input;

        var sb = new StringBuilder(input.Length);
        var pos = 0;
        while (true)
        {
            var idx = input.IndexOf(oldValue, pos, comparison);
            if (idx < 0) break;
            sb.Append(input, pos, idx - pos);
            sb.Append(newValue);
            pos = idx + oldValue.Length;
        }
        sb.Append(input, pos, input.Length - pos);
        return sb.ToString();
    }

    static string[] CollapseConsecutiveDuplicates(List<string> lines)
    {
        if (lines.Count == 0) return [];

        var result = new List<string>(lines.Count);
        var current = lines[0];
        var count = 1;

        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i] == current)
            {
                count++;
            }
            else
            {
                result.Add(count > 1 ? $"{current} (x{count})" : current);
                current = lines[i];
                count = 1;
            }
        }
        result.Add(count > 1 ? $"{current} (x{count})" : current);
        return result.ToArray();
    }

    static string[] FuzzyCollapse(string[] lines)
    {
        if (lines.Length < 3) return lines;

        var result = new List<string>(lines.Length);
        for (int i = 0; i < lines.Length;)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("at ") && trimmed.Length >= 5)
            {
                string? prefix = trimmed;
                int runEnd = i;

                for (int j = i + 1; j < lines.Length; j++)
                {
                    var t = lines[j].TrimStart();
                    if (t.StartsWith("at ") || t.Length < 5) break;

                    int minLen = Math.Min(prefix!.Length, t.Length);
                    int k = 0;
                    while (k < minLen && prefix[k] == t[k]) k++;
                    if (k < 5) break;
                    prefix = prefix[..k];
                    runEnd = j;
                }

                int runLen = runEnd - i + 1;
                if (runLen >= 3)
                {
                    result.Add($"{prefix}... ({runLen} lines)");
                    i = runEnd + 1;
                    continue;
                }
            }

            result.Add(lines[i]);
            i++;
        }

        return [.. result];
    }
}
