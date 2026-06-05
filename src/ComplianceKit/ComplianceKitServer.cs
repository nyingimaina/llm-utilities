using System.Text.Json;
using System.Text.RegularExpressions;
using LLMUtilities.Commons;
using Serilog;

namespace ComplianceKit;

public sealed partial class ComplianceKitServer : McpServerBase
{
    readonly string _standardsPath;

    public ComplianceKitServer(string? configPath = null, string? standardsPath = null)
        : base(new McpServerConfig
        {
            Name = "ComplianceKit",
            Version = GetEntryVersion(),
            AnnouncementDirective = "MANDATORY FIRST STEP before any code change. Returns code quality standards, audits files, finds existing abstractions, and reviews diffs. Always call before writing code.",
            HarnessInstructions = "!!!MANDATORY!!! Call get_instructions on ComplianceKit before any code change. This server enforces DRY, SRP, naming, and consistency standards. Use find_candidates() to check for existing code, audit_file() to review your output.",
            InstructionsToolDescription = "MANDATORY FIRST STEP: call get_instructions before any code change. Returns code quality standards (DRY, SRP, naming, consistency). Use find_candidates() to check existing abstractions, audit_file() to review code, audit_diff() to check diffs.",
        }.WithOverrides(configPath))
    {
        var dir = AppContext.BaseDirectory;
        _standardsPath = standardsPath
            ?? (File.Exists(Path.Combine(dir, "code-standards.md")) ? Path.Combine(dir, "code-standards.md") : null)
            ?? (File.Exists(Path.Combine(dir, "code-standards.default.md")) ? Path.Combine(dir, "code-standards.default.md") : null)
            ?? Path.Combine(dir, "code-standards.md");
    }

    protected override bool RequiresTimeoutMs(string toolName) => false;

    protected override McpTool[] RegisterTools() =>
    [
        new McpTool
        {
            Name = "find_candidates",
            Description = "Search codebase for existing abstractions matching a description. Call BEFORE writing new code to avoid duplication. Returns file paths and code snippets.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    description = new { type = "string", description = "What you're looking for (e.g. 'user authentication', 'data export')" },
                    rootPath = new { type = "string", description = "Project root to search (default: auto-detect)" },
                    limit = new { type = "integer", description = "Max results (default 10)" },
                },
                required = new[] { "description" }
            }
        },
        new McpTool
        {
            Name = "audit_file",
            Description = "Analyze a file for code quality issues. Checks naming, length, complexity, error handling, magic numbers, and consistency. Returns structured issues array.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Absolute file path" },
                },
                required = new[] { "path" }
            }
        },
        new McpTool
        {
            Name = "audit_diff",
            Description = "Analyze a unified diff for quality issues before applying. Catches large changes, naming violations, missing error handling, hardcoded values, and TODO markers in new code.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    diff = new { type = "string", description = "Unified diff text" },
                },
                required = new[] { "diff" }
            }
        },
    ];

    protected override object HandleToolCall(string name, JsonElement? arguments)
    {
        return name switch
        {
            "find_candidates" => HandleFindCandidates(arguments),
            "audit_file" => HandleAuditFile(arguments),
            "audit_diff" => HandleAuditDiff(arguments),
            _ => throw new McpErrorException($"Tool not found: {name}"),
        };
    }

    protected override object GetInstructions()
    {
        var content = ReadStandardsFile();
        return new { _h = content };
    }

    string[] ReadStandardsFile()
    {
        try
        {
            if (!File.Exists(_standardsPath))
            {
                Log.Warning("Standards file not found at {Path}", _standardsPath);
                return
                [
                    "=== CODE QUALITY STANDARDS ===",
                    $"Standards file not found at: {_standardsPath}",
                    "",
                    "Core principles:",
                    "1. DRY — Do not repeat yourself. Extract shared code.",
                    "2. SRP — Each function/class does one thing.",
                    "3. Naming — Descriptive, consistent naming.",
                    "4. Consistency — Match surrounding code style.",
                    "5. Error handling — Never swallow errors.",
                    "6. Testing — Test behavior, not implementation.",
                    "",
                    "For the full standards document, ensure code-standards.md exists at the server path.",
                ];
            }

            return File.ReadAllLines(_standardsPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read standards file");
            return ["=== CODE QUALITY STANDARDS ===", $"Error reading standards file: {ex.Message}"];
        }
    }

    object HandleFindCandidates(JsonElement? arguments)
    {
        var description = GetString(arguments, "description") ?? "";
        var rootPath = GetString(arguments, "rootPath");
        var limit = GetInt(arguments, "limit") ?? 10;

        if (string.IsNullOrWhiteSpace(description))
            throw new McpErrorException("description is required");

        rootPath ??= DetectProjectRoot();
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            throw new McpErrorException("rootPath not found. Specify it explicitly.");

        var keywords = ExtractKeywords(description);
        if (keywords.Count == 0)
            throw new McpErrorException("Could not extract search keywords from description");

        var candidates = new List<Dictionary<string, object?>>();
        var searchPattern = string.Join("|", keywords.Select(k => Regex.Escape(k)));

        try
        {
            var searchDir = rootPath;
            var files = Directory.EnumerateFiles(searchDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains("bin\\") && !f.Contains("obj\\")
                         && !f.Contains("node_modules") && !f.Contains(".git")
                         && !f.EndsWith(".exe") && !f.EndsWith(".dll"))
                .Take(200);

            foreach (var file in files)
            {
                if (candidates.Count >= limit) break;

                try
                {
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (Regex.IsMatch(lines[i], searchPattern, RegexOptions.IgnoreCase))
                        {
                            var contextStart = Math.Max(0, i - 2);
                            var contextEnd = Math.Min(lines.Length - 1, i + 3);
                            var snippet = string.Join("\n",
                                lines[contextStart..(contextEnd + 1)]
                                    .Select((l, idx) => $"{(contextStart + idx + 1).ToString().PadLeft(4)}| {l}"));

                            candidates.Add(new Dictionary<string, object?>
                            {
                                ["file"] = Path.GetRelativePath(rootPath, file),
                                ["line"] = i + 1,
                                ["snippet"] = snippet,
                            });
                            break;
                        }
                    }
                }
                catch { continue; }
            }
        }
        catch (Exception ex)
        {
            throw new McpErrorException($"Search failed: {ex.Message}");
        }

        return new
        {
            _s = candidates.Count > 0 ? "found" : "none",
            _count = candidates.Count,
            _keywords = keywords,
            _candidates = candidates,
            _hint = candidates.Count == 0
                ? "No matches found. Try broader keywords or check rootPath."
                : null as string,
        };
    }

    object HandleAuditFile(JsonElement? arguments)
    {
        var path = GetString(arguments, "path") ?? "";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new McpErrorException($"File not found: {path}");

        var issues = new List<Dictionary<string, object?>>();
        var lines = File.ReadAllLines(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // File length
        if (lines.Length > 500)
            issues.Add(Issue("file_length", $"File is {lines.Length} lines. Consider splitting files >500 lines."));

        // Check naming consistency in the file
        var camelCaseCount = CountMatches(lines, @"\b[a-z][a-zA-Z0-9]*\b\s*(?=\()");
        var snakeCaseCount = CountMatches(lines, @"\b[a-z][a-z0-9_]*\b\s*(?=\()");
        var pascalCaseCount = CountMatches(lines, @"\b[A-Z][a-zA-Z0-9]*\b\s*(?=\()");

        if (camelCaseCount > 0 && snakeCaseCount > 0 && pascalCaseCount > 0)
        {
            var dominant = camelCaseCount >= snakeCaseCount && camelCaseCount >= pascalCaseCount ? "camelCase"
                : snakeCaseCount >= camelCaseCount && snakeCaseCount >= pascalCaseCount ? "snake_case"
                : "PascalCase";
            issues.Add(Issue("naming_mixed",
                $"Mixed naming styles detected. Most functions use {dominant}, but {camelCaseCount} camelCase, {snakeCaseCount} snake_case, and {pascalCaseCount} PascalCase patterns found. Standardize on {dominant}."));
        }

        // Comment density — too few comments for complex logic
        var commentCount = CountMatches(lines, @"^\s*(//|#|--|;)");
        if (lines.Length > 50 && commentCount == 0)
            issues.Add(Issue("missing_comments", "No comments found in file >50 lines. Add module-level and complex-logic comments."));

        // TODO / FIXME / HACK markers
        var todoCount = CountMatches(lines, @"\b(TODO|FIXME|HACK|XXX|WORKAROUND)\b");
        if (todoCount > 0)
            issues.Add(Issue("todo_markers", $"{todoCount} TODO/FIXME/HACK markers found. Resolve before committing."));

        // Magic numbers
        var magicCount = CountMatches(lines, @"[^a-zA-Z0-9_]\b\d{4,}\b[^a-zA-Z0-9_]");
        if (magicCount > 0)
            issues.Add(Issue("magic_numbers", $"{magicCount} potential magic numbers found. Extract to named constants."));

        // Deeply nested code (indentation heuristic)
        var maxIndent = 0;
        foreach (var line in lines)
        {
            var indent = line.TakeWhile(c => c is ' ' or '\t').Count();
            if (indent > maxIndent) maxIndent = indent;
        }
        if (maxIndent > 40)
            issues.Add(Issue("deep_nesting", $"Maximum indentation depth of {maxIndent} chars suggests overly nested code. Extract inner blocks to functions."));

        // Long lines
        var longLines = 0;
        foreach (var line in lines)
        {
            if (line.Length > 120) longLines++;
        }
        if (longLines > lines.Length * 0.1)
            issues.Add(Issue("long_lines", $"{longLines}/{lines.Length} lines exceed 120 chars. Consider line wrapping."));

        // Empty catch blocks / silent error swallowing
        if (ext is ".cs" or ".java" or ".js" or ".ts" or ".kt")
        {
            var emptyCatches = CountMatches(lines, @"catch\s*\([^)]*\)\s*\{\s*\}");
            if (emptyCatches > 0)
                issues.Add(Issue("empty_catch", $"{emptyCatches} empty catch block(s). Never swallow exceptions silently."));
        }

        return new
        {
            _s = issues.Count == 0 ? "clean" : "issues_found",
            _path = path,
            _total_lines = lines.Length,
            _issues = issues,
            _issue_count = issues.Count,
        };
    }

    object HandleAuditDiff(JsonElement? arguments)
    {
        var diff = GetString(arguments, "diff") ?? "";
        if (string.IsNullOrWhiteSpace(diff))
            throw new McpErrorException("diff is required");

        var lines = diff.Split('\n');
        var issues = new List<Dictionary<string, object?>>();
        var addedLines = new List<string>();
        var addedLineNumbers = new List<string>();

        string? currentFile = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith("+++ b/"))
            {
                currentFile = line[6..];
                continue;
            }

            if (line.StartsWith("+") && !line.StartsWith("+++"))
            {
                addedLines.Add(line[1..]);
                addedLineNumbers.Add($"{currentFile}:~{i}");
            }
        }

        // Large diff check
        if (addedLines.Count > 100)
            issues.Add(Issue("large_diff", $"Diff adds {addedLines.Count} lines. Break into smaller, focused changes."));

        // Check added lines for issues
        foreach (var added in addedLines)
        {
            // Magic numbers
            if (Regex.IsMatch(added, @"[^a-zA-Z0-9_]\b\d{4,}\b[^a-zA-Z0-9_]"))
            {
                issues.Add(Issue("magic_number_added", $"Magic number in added code: '{added.Trim()}'"));
                break;
            }

            // TODO/FIXME/HACK
            if (Regex.IsMatch(added, @"\b(TODO|FIXME|HACK|XXX)\b"))
            {
                issues.Add(Issue("todo_added", $"TODO/FIXME/HACK in added code: '{added.Trim()}'"));
                break;
            }

            // Console.WriteLine / print / echo debugging
            if (Regex.IsMatch(added, @"\b(console\.log|printf|print|echo|Debug\.(Write|Print)|puts)\s*\("))
            {
                issues.Add(Issue("debug_output", $"Debug print statement in added code: '{added.Trim()}'"));
                break;
            }

            // Empty catch
            if (Regex.IsMatch(added, @"catch\s*\([^)]*\)\s*\{\s*\}"))
            {
                issues.Add(Issue("empty_catch_added", $"Empty catch block in added code: '{added.Trim()}'"));
                break;
            }
        }

        // Multiple unrelated changes in one diff (too many file changes)
        var filesChanged = lines
            .Where(l => l.StartsWith("diff --git"))
            .Select(l => l.Split(' ').LastOrDefault())
            .Distinct()
            .Count();
        if (filesChanged > 5)
            issues.Add(Issue("too_many_files", $"Diff touches {filesChanged} files. Each commit should focus on one concern."));

        return new
        {
            _s = issues.Count == 0 ? "clean" : "issues_found",
            _files_changed = filesChanged,
            _lines_added = addedLines.Count,
            _issues = issues,
            _issue_count = issues.Count,
        };
    }

    static string? DetectProjectRoot()
    {
        var dir = Environment.CurrentDirectory;
        for (int i = 0; i < 5; i++)
        {
            if (dir is null) break;
            if (File.Exists(Path.Combine(dir, ".gitignore")) ||
                File.Exists(Path.Combine(dir, ".claude.json")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    static List<string> ExtractKeywords(string description)
    {
        var words = description.Split([' ', ',', '.', '!', '?', ':', ';', '\n', '\r', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "in", "of", "to", "for", "and", "or", "is", "are",
            "was", "were", "be", "been", "being", "have", "has", "had", "do",
            "does", "did", "will", "would", "could", "should", "may", "might",
            "shall", "can", "need", "dare", "ought", "used", "i", "you", "we",
            "they", "he", "she", "it", "that", "this", "these", "those", "am",
            "find", "search", "look", "get", "check", "see", "show", "list",
        };

        return words
            .Where(w => w.Length >= 3 && !stopwords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    static int CountMatches(string[] lines, string pattern)
    {
        var count = 0;
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, pattern))
                count++;
        }
        return count;
    }

    static Dictionary<string, object?> Issue(string code, string message) => new()
    {
        ["_code"] = code,
        ["_message"] = message,
    };
}
