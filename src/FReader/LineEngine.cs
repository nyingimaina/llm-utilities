using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FReader;

public static class LineEngine
{
    static readonly Regex UsingPattern = new(@"^\s*using\s+", RegexOptions.Compiled);

    public static LineResult ReadLines(
        string path,
        int? lineStart,
        int? lineEnd,
        int? truncate,
        bool stripImports = false,
        bool normalizeIndent = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return Error("File not found", "FILE_NOT_FOUND", ("_path", path));

        try
        {
            // Binary detection: check first 4KB for null bytes
            using (var detectStream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                ct.ThrowIfCancellationRequested();
                var buffer = new byte[4096];
                var read = detectStream.Read(buffer, 0, buffer.Length);
                for (int i = 0; i < read; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (buffer[i] == 0)
                        return Error("File is binary and cannot be read as text", "BINARY_FILE");
                }
            }

            ct.ThrowIfCancellationRequested();
            var allLines = File.ReadAllLines(path, Encoding.UTF8);
            var totalLines = allLines.Length;

            if (totalLines == 0)
                return new LineResult(ResultDict(new string[0], totalLines, false), null);

            int start = lineStart ?? 1;
            int end = lineEnd ?? totalLines;

            if (lineStart.HasValue && !lineEnd.HasValue)
                end = totalLines;

            if (!lineStart.HasValue && lineEnd.HasValue)
                start = 1;

            if (start > end)
                return Error($"lineStart ({start}) is greater than lineEnd ({end})", "INVALID_RANGE");

            if (start > totalLines)
                return Error($"lineStart ({start}) exceeds file length ({totalLines} lines)", "INVALID_RANGE");

            if (end > totalLines)
                end = totalLines;

            var maxLines = truncate.GetValueOrDefault(200);
            if (maxLines == 0) maxLines = int.MaxValue;
            var sliceStart = start - 1;
            var sliceLen = Math.Min(end - sliceStart, maxLines);
            var truncated = sliceLen < end - sliceStart;

            // Collect original indices after filtering
            var includedIndices = new List<int>();
            for (int i = sliceStart; i < sliceStart + sliceLen && i < totalLines; i++)
            {
                var content = allLines[i];
                if (stripImports && UsingPattern.IsMatch(content))
                {
                    if (!content.Contains('='))
                        continue;
                }
                includedIndices.Add(i);
            }

            // Build output lines
            var resultLines = new string[includedIndices.Count];
            for (int j = 0; j < includedIndices.Count; j++)
            {
                var idx = includedIndices[j];
                var content = allLines[idx];
                if (normalizeIndent)
                {
                    // Apply after collecting all lines for common prefix computation
                }
                resultLines[j] = $"{idx + 1}\t{content}";
            }

            if (normalizeIndent && resultLines.Length > 0)
            {
                var contentOnly = includedIndices.Select(i => allLines[i]).ToList();
                var commonPrefix = FindCommonIndent(contentOnly);
                if (commonPrefix > 0)
                {
                    for (int j = 0; j < resultLines.Length; j++)
                    {
                        var idx = includedIndices[j];
                        var content = allLines[idx];
                        var tabIdx = resultLines[j].IndexOf('\t');
                        var lineNum = resultLines[j].Substring(0, tabIdx);
                        var stripped = content.Length > commonPrefix
                            ? content.Substring(commonPrefix)
                            : "";
                        resultLines[j] = $"{lineNum}\t{stripped}";
                    }
                }
            }

            var nextStart = truncated ? sliceStart + 1 + sliceLen : (int?)null;
            var data = ResultDict(resultLines, totalLines, truncated, nextStart, end);
            return new LineResult(data, null);
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "READ_ERROR", ("_path", path));
        }
    }

    static int FindCommonIndent(List<string> lines)
    {
        int minIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            var indent = line.TakeWhile(char.IsWhiteSpace).Count();
            if (indent < minIndent)
                minIndent = indent;
        }
        return minIndent == int.MaxValue ? 0 : minIndent;
    }

    static Dictionary<string, object?> ResultDict(string[] lines, int totalLines, bool truncated, int? nextLineStart = null, int? nextLineEnd = null)
    {
        var result = new Dictionary<string, object?>();
        if (lines.Length <= 50)
        {
            result["_h"] = lines;
        }
        else
        {
            result["_h"] = new[] { "line", "text" };
            result["_r"] = lines;
        }
        if (truncated)
        {
            result["_truncated"] = true;
            result["_total"] = totalLines;
            if (nextLineStart.HasValue)
                result["_next"] = new Dictionary<string, object>
                {
                    ["lineStart"] = nextLineStart.Value,
                    ["lineEnd"] = nextLineEnd ?? totalLines
                };
        }
        return result;
    }

    static LineResult Error(string message, string code, params (string Key, object? Value)[] extras)
    {
        var dict = new Dictionary<string, object?>
        {
            ["error"] = message,
            ["error_code"] = code
        };
        foreach (var (key, value) in extras)
            dict[key] = value;
        return new LineResult(null, dict);
    }

    public static object GetInfo(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return FReaderError.Make("File not found", FReaderError.FILE_NOT_FOUND, ("_path", path));

        try
        {
            var info = new FileInfo(path);
            int lines;
            using (var reader = new StreamReader(path, Encoding.UTF8))
            {
                lines = 0;
                while (reader.ReadLine() is not null) { lines++; ct.ThrowIfCancellationRequested(); }
            }

            return new Dictionary<string, object?>
            {
                ["_path"] = Path.GetFullPath(path),
                ["_size"] = info.Length,
                ["_lines"] = lines,
                ["_encoding"] = "utf-8",
                ["_modified"] = File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
        }
        catch (Exception ex)
        {
            return FReaderError.Make(ex.Message, FReaderError.READ_ERROR, ("_path", path));
        }
    }

    public static object GrepInFile(
        string path,
        string pattern,
        int context = 2,
        bool caseSensitive = false,
        int maxMatches = 20,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return FReaderError.Make("File not found", FReaderError.FILE_NOT_FOUND, ("_path", path));

        try
        {
            ct.ThrowIfCancellationRequested();
            var allLines = File.ReadAllLines(path, Encoding.UTF8);
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var regexTimeout = TimeSpan.FromMilliseconds(500);
            var regexOpts = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;

            if (string.IsNullOrEmpty(pattern))
                return Error("Pattern cannot be empty", "EMPTY_PATTERN");

            var regex = new Regex(pattern, regexOpts, regexTimeout);

            var matchLines = new List<int>();
            for (int i = 0; i < allLines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (regex.IsMatch(allLines[i]))
                    {
                        matchLines.Add(i);
                        if (matchLines.Count >= maxMatches) break;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    return Error("Pattern timed out. Simplify the regex.", "REGEX_TIMEOUT");
                }
            }

            // Build context windows, merging overlapping ones
            var windows = new List<(int Start, int End)>();
            foreach (var ml in matchLines)
            {
                var ws = Math.Max(0, ml - context);
                var we = Math.Min(allLines.Length - 1, ml + context);

                if (windows.Count > 0 && ws <= windows[^1].End + 1)
                    windows[^1] = (windows[^1].Start, Math.Max(windows[^1].End, we));
                else
                    windows.Add((ws, we));
            }

            var merged = new List<string>();
            var mergedCounts = new List<int>();
            foreach (var (ws, we) in windows)
            {
                int linesInWindow = 0;
                foreach (var ml in matchLines)
                {
                    if (ml >= ws && ml <= we)
                        linesInWindow++;
                }

                merged.Add($"// --- {linesInWindow} match(es) ---");
                for (int i = ws; i <= we; i++)
                    merged.Add($"{i + 1}\t{allLines[i]}");
            }

            var result = new Dictionary<string, object?>
            {
                ["_path"] = Path.GetFullPath(path),
                ["_pattern"] = pattern,
                ["_matches"] = matchLines.Count,
                ["_r"] = merged.ToArray()
            };

            if (matchLines.Count > 0)
            {
                var totalContext = merged.Count - mergedCounts.Sum();
                result["_context_lines"] = merged.Count - merged.Count(m => m.StartsWith("// ---"));
            }

            return result;
        }
        catch (RegexParseException ex)
        {
            return Error($"Invalid regex pattern: {ex.Message}", "INVALID_REGEX");
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "GREP_ERROR");
        }
    }

    public sealed record LineResult(Dictionary<string, object?>? Data, Dictionary<string, object?>? Error);
}
