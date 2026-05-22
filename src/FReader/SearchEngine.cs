using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FReader;

public static class SearchEngine
{
    public static object SearchFunction(
        string rootPath,
        string name,
        string[]? fileExtensions,
        int limit,
        bool includeGenerated,
        CancellationToken ct = default,
        Action<double, string?, double?>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var extensions = fileExtensions is { Length: > 0 } ? fileExtensions : new[] { ".cs" };
        var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var results = new List<object>();

        if (!Directory.Exists(rootPath))
            return FReaderError.Make($"Directory not found: {rootPath}", FReaderError.FILE_NOT_FOUND);

        ct.ThrowIfCancellationRequested();

        // Phase 1: collect matching files with count for progress
        var allFiles = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories))
            {
                if (!extSet.Contains(Path.GetExtension(file))) continue;
                if (!includeGenerated && IsGeneratedFile(file, rootPath)) continue;
                allFiles.Add(file);
            }
        }
        catch (UnauthorizedAccessException) { }

        ct.ThrowIfCancellationRequested();

        // Phase 1: regex pre-scan — find files containing the name
        var candidates = new List<string>();
        for (int fi = 0; fi < allFiles.Count; fi++)
        {
            ct.ThrowIfCancellationRequested();
            if (candidates.Count >= limit * 5) break;

            var file = allFiles[fi];
            progress?.Invoke((double)fi / allFiles.Count, $"phase 1: scanning {Path.GetFileName(file)}...", allFiles.Count);

            try
            {
                using var reader = new StreamReader(file);
                bool found = false;
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (found) candidates.Add(file);
            }
            catch { /* skip unreadable */ }
        }

        // Phase 2: Roslyn confirm on candidates, extracting class context
        for (int ci = 0; ci < candidates.Count; ci++)
        {
            ct.ThrowIfCancellationRequested();
            if (results.Count >= limit) break;

            var file = candidates[ci];
            progress?.Invoke(0.5 + 0.5 * (double)ci / candidates.Count, $"phase 2: confirming {Path.GetFileName(file)}...", candidates.Count);

            try
            {
                var code = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetRoot();

                foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    foreach (var member in type.Members)
                    {
                        if (member is MethodDeclarationSyntax m &&
                            string.Equals(m.Identifier.Text, name, StringComparison.OrdinalIgnoreCase))
                        {
                            var lineStart = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            var lineEnd = m.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

                            var paramTypes = m.ParameterList.Parameters
                                .Select(p => p.Type?.ToString() ?? "var");
                            var sig = m.ReturnType + " " + m.Identifier.Text + "(" +
                                      string.Join(", ", paramTypes) + ")";

                            results.Add(new
                            {
                                path = Path.GetFullPath(file),
                                className = type.Identifier.Text,
                                sig,
                                lineStart,
                                lineEnd,
                                _line_start = lineStart,
                                _line_end = lineEnd,
                                _sig = sig
                            });

                            if (results.Count >= limit) break;
                        }
                    }
                    if (results.Count >= limit) break;
                }
            }
            catch { /* skip files that fail parsing */ }
        }

        sw.Stop();
        return new
        {
            _r = results.ToArray(),
            _total_matches = results.Count,
            _search_time_ms = sw.ElapsedMilliseconds
        };
    }

    public static object Grep(
        string rootPath,
        string pattern,
        string[]? fileExtensions,
        int context,
        int maxMatches,
        bool caseSensitive,
        bool includeGenerated,
        CancellationToken ct = default,
        Action<double, string?, double?>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var extensions = fileExtensions is { Length: > 0 } ? fileExtensions : new[] { ".cs" };
        var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var regexOpts = caseSensitive ? RegexOptions.Compiled : RegexOptions.Compiled | RegexOptions.IgnoreCase;
        Regex regex;
        try { regex = new Regex(pattern, regexOpts, TimeSpan.FromMilliseconds(500)); }
        catch (RegexParseException ex)
            { return FReaderError.Make($"Invalid regex: {ex.Message}", FReaderError.INVALID_REGEX); }

        var fileResults = new List<object>();
        int totalMatches = 0;
        bool truncated = false;

        if (!Directory.Exists(rootPath))
            return FReaderError.Make($"Directory not found: {rootPath}", FReaderError.FILE_NOT_FOUND);

        ct.ThrowIfCancellationRequested();

        // Collect matching files for progress reporting
        var matchingFiles = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories))
            {
                if (!extSet.Contains(Path.GetExtension(file))) continue;
                if (!includeGenerated && IsGeneratedFile(file, rootPath)) continue;
                matchingFiles.Add(file);
            }
        }
        catch (UnauthorizedAccessException) { }

        ct.ThrowIfCancellationRequested();

        for (int fi = 0; fi < matchingFiles.Count; fi++)
        {
            ct.ThrowIfCancellationRequested();
            if (totalMatches >= maxMatches) { truncated = true; break; }

            var file = matchingFiles[fi];
            progress?.Invoke((double)fi / matchingFiles.Count, $"searching {Path.GetFileName(file)}...", matchingFiles.Count);

            try
            {
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                var matchIndices = new List<int>();
                for (int i = 0; i < lines.Length; i++)
                {
                    if (totalMatches >= maxMatches) break;
                    try
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            matchIndices.Add(i);
                            totalMatches++;
                        }
                    }
                    catch (RegexMatchTimeoutException) { break; }
                }

                if (matchIndices.Count == 0) continue;

                var windows = new List<(int Start, int End)>();
                foreach (var idx in matchIndices)
                {
                    var ws = Math.Max(0, idx - context);
                    var we = Math.Min(lines.Length - 1, idx + context);
                    if (windows.Count > 0 && ws <= windows[^1].End + 1)
                        windows[^1] = (windows[^1].Start, Math.Max(windows[^1].End, we));
                    else
                        windows.Add((ws, we));
                }

                var merged = new List<string>();
                foreach (var (ws, we) in windows)
                {
                    var count = matchIndices.Count(idx => idx >= ws && idx <= we);
                    merged.Add($"// --- {count} match(es) ---");
                    for (int i = ws; i <= we; i++)
                        merged.Add($"{i + 1}\t{lines[i]}");
                }

                fileResults.Add(new
                {
                    _path = Path.GetFullPath(file),
                    _matches = matchIndices.Count,
                    _r = merged.ToArray()
                });
            }
            catch { /* skip unreadable */ }
        }

        sw.Stop();
        var result = new Dictionary<string, object?>
        {
            ["_pattern"] = pattern,
            ["_total_matches"] = totalMatches,
            ["_files_searched"] = matchingFiles.Count,
            ["_search_time_ms"] = sw.ElapsedMilliseconds,
            ["_r"] = fileResults.ToArray()
        };
        if (truncated) result["_truncated"] = true;
        return result;
    }

    static bool IsGeneratedFile(string file, string rootPath)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        var relDir = dir.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
            ? dir.Substring(rootPath.Length).TrimStart('\\', '/')
            : dir;

        if (relDir.StartsWith("obj", StringComparison.OrdinalIgnoreCase) ||
            relDir.StartsWith("bin", StringComparison.OrdinalIgnoreCase))
            return true;

        if (relDir.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
            relDir.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            relDir.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) ||
            relDir.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            return true;

        var fileName = Path.GetFileName(file);
        if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
