using System.Text.Json;
using LLMUtilities.Commons;

namespace FReader;

sealed class FReaderServer : McpServerBase, IDisposable
{
    protected override bool RequiresTimeoutMs(string toolName) => toolName != "record_benchmark";
    protected override int? DefaultTimeoutMs(string toolName) => toolName == "grep" || toolName == "search_function" ? 60000 : 30000;
    protected override Dictionary<string, int>? PerToolAutoNotifyThresholdMs => new()
    {
        ["read"] = 2000,
        ["grep"] = 5000,
        ["search_function"] = 5000,
        ["grep_in_file"] = 2000,
        ["summarize"] = 3000,
        ["read_function"] = 2000,
        ["read_functions"] = 2000,
        ["list_functions"] = 2000,
        ["info"] = 1000,
        ["extract_function"] = 2000,
    };
    public void Dispose() => ProviderFactory.Shutdown();
    const string Harness = """
=== FReader -- mandatory harness ===
Use FReader for ALL file reads in this project. Do not use Read, Grep, or Bash for file content.

SPECIALIZED PROVIDERS (AST-level extraction for these languages):
  .cs       | CSharp  -- full class/method/property extraction
  .ts/.tsx/.mts/.cts  | TypeScript AST -- functions, methods, constructors
  .js/.jsx/.mjs/.cjs  | TypeScript AST -- includes JSDoc type support
  Others (css, html, py, json, md) | read + grep_in_file only

TOOL SELECTION (in priority order):
  summarize       > first look at any file (87-96% token savings)
  read_function   > reading a named function (1 call vs 3)
  read_functions  > reading multiple functions (1 call, merges adjacent)
  search_function > finding a function across files (definitions only)
  grep_in_file    > regex search when file is known
  list_functions  > enumerate all functions in a file
  extract_function > get line range only (no body)
  read            > line-range read when no function tool applies
  info            > file metadata before reading

COMPACT FIELDS: _r=rows _h=headers _line_start/_line_end=range _sig=signature _text=text output
""";

    public FReaderServer(string? configPath = null) : base(new McpServerConfig
    {
        Name = "FReader",
        Version = GetEntryVersion(),
        AnnouncementDirective = "PREFER over built-in Read/Grep/Glob: AST-level extraction saves 87-96% tokens vs raw read+grep. summarize() first, read_function() to drill in, search_function() cross-file, grep_in_file() in-file regex.",
        InstructionsToolDescription = "MANDATORY FIRST STEP: read critical server instructions before using any other tool. Defines compact fields, token-saving conventions, and usage patterns.",
        HarnessInstructions = Harness,
        AutoNotifyThresholdMs = 3000,
    }.WithOverrides(configPath))
    {
    }

    protected override McpTool[] RegisterTools()
    {
        return new[]
        {
            new McpTool
            {
                Name = "read",
                Description = "Read lines from a file. Tab-prefix format. Supports stripImports, normalizeIndent, aliases. Truncates at truncate param (default 200), returns _truncated + _next continuation.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        lineStart = new { type = "integer" },
                        lineEnd = new { type = "integer" },
                        truncate = new { type = "integer", description = "Max lines (default 200)" },
                        stripImports = new { type = "boolean" },
                        normalizeIndent = new { type = "boolean" },
                        aliases = new { type = "boolean", description = "Alias repeated type names (default false)" },
                        timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Default 30000." }
                    },
                    required = new[] { "path" }
                }
            },
            new McpTool
            {
                Name = "info",
                Description = "PREFERRED over built-in Read. File metadata (size, lines, encoding).",
                InputSchema = new
                {
                    type = "object",
                    properties = new { path = new { type = "string" }, timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Default 30000." } },
                    required = new[] { "path" }
                }
            },
            new McpTool
            {
                Name = "list_functions",
                Description = "List functions in source file. Columnar with lineEnd. includeSystem filter.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        includeSystem = new { type = "boolean" },
                        timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Default 30000." }
                    },
                    required = new[] { "path" }
                }
            },
            new McpTool
            {
                Name = "extract_function",
                Description = "Get line range(s) of named function. Overload disambiguation via parameterTypes.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        name = new { type = "string" },
                        parameterTypes = new { type = "array", items = new { type = "string" }, description = "Overload disambiguation" },
                        className = new { type = "string", description = "Class for cross-class disambiguation" },
                        timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Default 30000." }
                    },
                    required = new[] { "path", "name" }
                }
            },
            new McpTool
            {
                Name = "read_function",
                Description = "Read named function body. Supports overloads, className, docs, stripImports, aliases.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        name = new { type = "string" },
                        parameterTypes = new { type = "array", items = new { type = "string" }, description = "Overload disambiguation" },
                        className = new { type = "string", description = "Class for cross-class disambiguation" },
                        includeDocs = new { type = "boolean" },
                        stripImports = new { type = "boolean" },
                        normalizeIndent = new { type = "boolean" },
                        aliases = new { type = "boolean" },
                        timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Default 30000." }
                    },
                    required = new[] { "path", "name" }
                }
            },
            new McpTool
            {
                Name = "read_functions",
                Description = "Batch-read multiple functions. Merges adjacent ranges. maxChars cap.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        names = new { type = "array", items = new { type = "string" } },
                        stripImports = new { type = "boolean" },
                        normalizeIndent = new { type = "boolean" },
                        aliases = new { type = "boolean" },
                        maxChars = new { type = "integer", description = "Hard cap on response chars (default 8000)" },
                        timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Default 30000." }
                    },
                    required = new[] { "path", "names" }
                }
            },
            new McpTool
            {
                Name = "summarize",
                Description = "Structural overview: namespace, classes, attrs, sigs. 87-96% savings. Supports aliases.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        aliases = new { type = "boolean" },
                        timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Default 30000." }
                    },
                    required = new[] { "path" }
                }
            },
            new McpTool
            {
                Name = "grep_in_file",
                Description = "PREFERRED over built-in Grep for known files. Regex search within a file. Merges overlapping context windows.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        pattern = new { type = "string" },
                        context = new { type = "integer" },
                        caseSensitive = new { type = "boolean" },
                        maxMatches = new { type = "integer" },
                        timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Default 30000." }
                    },
                    required = new[] { "path", "pattern" }
                }
            },
            new McpTool
            {
                Name = "search_function",
                Description = "PREFERRED over glob+grep. Search directory tree for named function. Regex+Roslyn. Excludes obj/bin by default. Supports progress pings.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        rootPath = new { type = "string", description = "Root directory" },
                        name = new { type = "string", description = "Function name" },
                        fileExtensions = new { type = "array", items = new { type = "string" }, description = "File types (default [.cs])" },
                        limit = new { type = "integer", description = "Max matches (default 20)" },
                        includeGenerated = new { type = "boolean", description = "Include obj/bin/generated files (default false)" },
                        timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Default 60000." }
                    },
                    required = new[] { "rootPath", "name" }
                }
            },
            new McpTool
            {
                Name = "grep",
                Description = "Cross-file regex search with context windows. Supports progress pings.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        rootPath = new { type = "string", description = "Root directory to search" },
                        pattern = new { type = "string", description = "Regex pattern" },
                        fileExtensions = new { type = "array", items = new { type = "string" }, description = "File types (default [.cs])" },
                        context = new { type = "integer", description = "Lines before/after each match (default 2)" },
                        maxMatches = new { type = "integer", description = "Global cap (default 50)" },
                        caseSensitive = new { type = "boolean", description = "Case-sensitive match (default false)" },
                        includeGenerated = new { type = "boolean", description = "Include obj/bin/generated files (default false)" },
                        timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Default 60000." }
                    },
                    required = new[] { "rootPath", "pattern" }
                }
            },
            new McpTool
            {
                Name = "record_benchmark",
                Description = "Record an adaptive benchmark score (0.0-1.0) for a feature. HUMAN-ONLY — do not call autonomously. Waives timeoutMs.",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        featureName = new { type = "string", description = "Feature name (e.g. read_function, search_function)" },
                        score = new { type = "number", description = "Score 0.0 (useless) to 1.0 (perfect)" },
                        path = new { type = "string", description = "Any file path in the project (for auto-detecting project root)" },
                    },
                    required = new[] { "featureName", "score", "path" }
                }
            }
        };
    }

    protected override object HandleToolCall(string name, JsonElement? arguments)
    {
        switch (name)
        {
            case "read": return HandleRead(arguments);
            case "info": return HandleInfo(arguments);
            case "list_functions": return HandleListFunctions(arguments);
            case "extract_function": return HandleExtractFunction(arguments);
            case "read_function": return HandleReadFunction(arguments);
            case "read_functions": return HandleReadFunctions(arguments);
            case "summarize": return HandleSummarize(arguments);
            case "grep_in_file": return HandleGrepInFile(arguments);
            case "search_function": return HandleSearchFunction(arguments);
            case "grep": return HandleGrep(arguments);
            case "record_benchmark": return HandleRecordBenchmark(arguments);
            default: throw new ArgumentException($"Tool not found: {name}");
        }
    }

    object HandleRead(JsonElement? arguments)
    {
        var path = GetString(arguments, "path") ?? "";
        var lineStart = GetInt(arguments, "lineStart");
        var lineEnd = GetInt(arguments, "lineEnd");
        var truncate = GetInt(arguments, "truncate") ?? 200;
        var stripImports = GetBool(arguments, "stripImports") ?? false;
        var normalizeIndent = GetBool(arguments, "normalizeIndent") ?? false;
        var useAliases = GetBool(arguments, "aliases") ?? false;

        var result = LineEngine.ReadLines(path, lineStart, lineEnd, truncate, stripImports, normalizeIndent, CancellationToken);
        if (result.Error is not null)
            throw new McpErrorException(JsonSerializer.Serialize(result.Error));

        var data = result.Data!;
        if (useAliases && data.TryGetValue("_r", out var rVal) && rVal is string[] lines)
        {
            var (aliases, aliasedLines, warning) = TypeAliaser.Apply(lines);
            if (aliases is not null)
            {
                data["_r"] = aliasedLines;
                data["_aliases"] = aliases;
                data["_alias_warning"] = warning;
            }
        }
        return data;
    }

    object HandleInfo(JsonElement? arguments)
    {
        var path = GetString(arguments, "path") ?? "";
        var result = LineEngine.GetInfo(path, CancellationToken);
        if (result is Dictionary<string, object?> err && err.ContainsKey("error"))
            throw new McpErrorException(JsonSerializer.Serialize(result));
        return result;
    }

    object HandleListFunctions(JsonElement? arguments)
    {
        var path = GetString(arguments, "path") ?? "";
        var includeSystem = GetBool(arguments, "includeSystem") ?? false;

        if (!ProviderFactory.HasProvider(path))
            throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make(
                $"FReader has no AST provider for {Path.GetExtension(path)} files",
                FReaderError.UNSUPPORTED_LANGUAGE,
                ("_supported_extensions", ProviderFactory.SupportedExtensions),
                ("_hint", "Use read() for plain line-based reading of this file, or grep_in_file for regex search"))));

        var funcs = ProviderFactory.ListFunctions(path, includeSystem, CancellationToken);
        var rows = funcs.Select(f => new object[] { f.Name, f.Signature, f.LineStart, f.LineEnd }).ToArray();
        return new
        {
            _h = new[] { "name", "signature", "lineStart", "lineEnd" },
            _r = rows
        };
    }

    object HandleExtractFunction(JsonElement? arguments)
    {
        var path = GetString(arguments, "path") ?? "";
        var funcName = GetString(arguments, "name") ?? "";
        var parameterTypes = GetStringArray(arguments, "parameterTypes");
        var className = GetString(arguments, "className");

        if (!ProviderFactory.HasProvider(path))
            throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make(
                $"FReader has no AST provider for {Path.GetExtension(path)} files",
                FReaderError.UNSUPPORTED_LANGUAGE,
                ("_supported_extensions", ProviderFactory.SupportedExtensions),
                ("_hint", "Use read() for plain line-based reading of this file, or grep_in_file for regex search"))));

        var funcs = ProviderFactory.GetFunctions(path, funcName, parameterTypes, CancellationToken);
        if (funcs.Count == 0)
        {
            if (parameterTypes is { Length: > 0 })
            {
                var allFuncs = ProviderFactory.GetFunctions(path, funcName, ct: CancellationToken);
                if (allFuncs.Count > 0)
                {
                    var overloads = allFuncs.Select(f => new
                    {
                        sig = f.Signature,
                        lineStart = f.LineStart,
                        lineEnd = f.LineEnd
                    }).ToArray();

                    throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make(
                        $"No overload of '{funcName}' matches parameterTypes [{string.Join(", ", parameterTypes)}]",
                        FReaderError.NOT_FOUND,
                        ("_available_overloads", overloads))));
                }
            }

            throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make($"Function '{funcName}' not found", FReaderError.NOT_FOUND)));
        }

        // Filter by className if specified
        List<FunctionInfo> targetFuncs;
        if (!string.IsNullOrEmpty(className))
        {
            targetFuncs = funcs.Where(f => ClassContainsFunction(path, f, className, CancellationToken)).ToList();
            if (targetFuncs.Count == 0)
                throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make($"Function '{funcName}' not found in class '{className}'",
                    FReaderError.NOT_FOUND)));
        }
        else
        {
            targetFuncs = funcs;
        }

        if (targetFuncs.Count == 1)
        {
            var f = targetFuncs[0];
            return new
            {
                _name = f.Name,
                _line_start = f.LineStart,
                _line_end = f.LineEnd,
                _sig = f.Signature
            };
        }

        // Multiple overloads -- return all
        return new
        {
            _name = funcName,
            _overload_count = targetFuncs.Count,
            _overloads = targetFuncs.Select(f => new
            {
                _line_start = f.LineStart,
                _line_end = f.LineEnd,
                _sig = f.Signature
            }).ToArray(),
            _hint = "Multiple overloads found. Use parameterTypes to select one, or use read_function which supports multi-overload responses."
        };
    }

    object HandleReadFunction(JsonElement? arguments)
    {
        var path = GetString(arguments, "path") ?? "";
        var funcName = GetString(arguments, "name") ?? "";
        var includeDocs = GetBool(arguments, "includeDocs") ?? false;
        var stripImports = GetBool(arguments, "stripImports") ?? false;
        var normalizeIndentFn = GetBool(arguments, "normalizeIndent") ?? false;
        var useAliases = GetBool(arguments, "aliases") ?? false;
        var parameterTypes = GetStringArray(arguments, "parameterTypes");
        var className = GetString(arguments, "className");

        if (!ProviderFactory.HasProvider(path))
            throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make(
                $"FReader has no AST provider for {Path.GetExtension(path)} files",
                FReaderError.UNSUPPORTED_LANGUAGE,
                ("_supported_extensions", ProviderFactory.SupportedExtensions),
                ("_hint", "Use read() for plain line-based reading of this file, or grep_in_file for regex search"))));

        var funcs = ProviderFactory.GetFunctions(path, funcName, parameterTypes, CancellationToken);
        if (funcs.Count == 0)
        {
            if (parameterTypes is { Length: > 0 })
            {
                var allFuncs = ProviderFactory.GetFunctions(path, funcName, ct: CancellationToken);
                if (allFuncs.Count > 0)
                {
                    var overloads = allFuncs.Select(f => new
                    {
                        sig = f.Signature,
                        lineStart = f.LineStart,
                        lineEnd = f.LineEnd
                    }).ToArray();

                    throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make(
                        $"No overload of '{funcName}' matches parameterTypes [{string.Join(", ", parameterTypes)}]",
                        FReaderError.NOT_FOUND,
                        ("_available_overloads", overloads))));
                }
            }

            throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make($"Function '{funcName}' not found", FReaderError.NOT_FOUND)));
        }

        // Filter by className if specified
        List<FunctionInfo> targetFuncs;
        if (!string.IsNullOrEmpty(className))
        {
            targetFuncs = funcs.Where(f => ClassContainsFunction(path, f, className, CancellationToken)).ToList();
            if (targetFuncs.Count == 0)
                throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make($"Function '{funcName}' not found in class '{className}'",
                    FReaderError.NOT_FOUND)));
        }
        else
        {
            targetFuncs = funcs;
        }

        if (targetFuncs.Count == 1)
        {
            var f = targetFuncs[0];
            var ls = f.LineStart;
            var le = f.LineEnd;

            if (includeDocs)
            {
                CancellationToken.ThrowIfCancellationRequested();
                var allLines = File.ReadAllLines(path);
                while (ls > 1)
                {
                    var prev = allLines[ls - 2].TrimStart();
                    if (prev.StartsWith("///") || prev.StartsWith("//"))
                        ls--;
                    else
                        break;
                }
            }

            var readResult = LineEngine.ReadLines(path, ls, le, truncate: 0, stripImports, normalizeIndentFn, CancellationToken);
            if (readResult.Error is not null)
                throw new McpErrorException(JsonSerializer.Serialize(readResult.Error));

            var sourceLines = ExtractLines(readResult.Data!);
            var result = new Dictionary<string, object?>
            {
                ["_name"] = f.Name,
                ["_sig"] = f.Signature,
                ["_line_start"] = f.LineStart,
                ["_line_end"] = f.LineEnd,
                ["_r"] = sourceLines
            };

            if (useAliases)
            {
                var (aliases, aliasedLines, warning) = TypeAliaser.Apply(sourceLines);
                if (aliases is not null)
                {
                    result["_r"] = aliasedLines;
                    result["_aliases"] = aliases;
                    result["_alias_warning"] = warning;
                }
            }
            return result;
        }

        // Multiple matches
        var matches = new List<object>();
        foreach (var f in targetFuncs)
        {
            var readResult = LineEngine.ReadLines(path, f.LineStart, f.LineEnd, truncate: 0, stripImports, normalizeIndentFn, CancellationToken);
            var sourceLines = readResult.Data is not null ? ExtractLines(readResult.Data) : Array.Empty<string>();
            var matchData = new Dictionary<string, object?>
            {
                ["_name"] = f.Name,
                ["_sig"] = f.Signature,
                ["_line_start"] = f.LineStart,
                ["_line_end"] = f.LineEnd,
                ["_r"] = sourceLines
            };

            if (useAliases)
            {
                var (aliases, aliasedLines, warning) = TypeAliaser.Apply(sourceLines);
                if (aliases is not null)
                {
                    matchData["_r"] = aliasedLines;
                    matchData["_aliases"] = aliases;
                    matchData["_alias_warning"] = warning;
                }
            }
            matches.Add(matchData);
        }

        return new
        {
            _name = funcName,
            _count = targetFuncs.Count,
            _match = matches
        };
    }

    object HandleReadFunctions(JsonElement? arguments)
    {
        var path = GetString(arguments, "path") ?? "";
        var stripImports = GetBool(arguments, "stripImports") ?? false;
        var normalizeIndentFn = GetBool(arguments, "normalizeIndent") ?? false;
        var useAliases = GetBool(arguments, "aliases") ?? false;
        var maxChars = GetInt(arguments, "maxChars") ?? 8000;

        if (!ProviderFactory.HasProvider(path))
            throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make(
                $"FReader has no AST provider for {Path.GetExtension(path)} files",
                FReaderError.UNSUPPORTED_LANGUAGE,
                ("_supported_extensions", ProviderFactory.SupportedExtensions),
                ("_hint", "Use read() for plain line-based reading of this file, or grep_in_file for regex search"))));

        var namesEl = arguments?.GetProperty("names");
        if (namesEl is null || namesEl.Value.GetArrayLength() == 0)
            throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make("names array is required", FReaderError.INVALID_REQUEST)));

        if (namesEl.Value.GetArrayLength() == 1)
            throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make("Use read_function for a single function name", FReaderError.INVALID_REQUEST)));

        var names = new List<string>();
        foreach (var n in namesEl.Value.EnumerateArray())
        {
            var nm = n.GetString() ?? "";
            if (nm.Length > 0 && !names.Contains(nm, StringComparer.OrdinalIgnoreCase))
                names.Add(nm);
        }

        if (names.Count > 10)
            names = names.Take(10).ToList();

        // Collect all matching function ranges
        var allFuncs = new List<(FunctionInfo F, int Order)>();
        foreach (var nm in names)
        {
            var funcs = ProviderFactory.GetFunctions(path, nm, ct: CancellationToken);
            foreach (var f in funcs)
                allFuncs.Add((f, names.IndexOf(nm)));
        }

        if (allFuncs.Count == 0)
            throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make("No matching functions found", FReaderError.NOT_FOUND)));

        // Sort by line number
        allFuncs.Sort((a, b) => a.F.LineStart.CompareTo(b.F.LineStart));

        // Check if adjacent functions should be merged
        var merged = new List<(int Start, int End, List<(FunctionInfo F, int Order)> Items)>();
        foreach (var (f, order) in allFuncs)
        {
            if (merged.Count > 0 && f.LineStart <= merged[^1].End + 1)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, f.LineEnd), merged[^1].Items.Append((f, order)).ToList());
            }
            else
            {
                merged.Add((f.LineStart, f.LineEnd, new List<(FunctionInfo, int)> { (f, order) }));
            }
        }

        // Check total file coverage for hint
        int totalCoverageLines = 0;
        foreach (var (start, end, _) in merged)
            totalCoverageLines += end - start + 1;

        // Read each merged block, respecting maxChars
        var results = new List<object>();
        int totalChars = 0;
        foreach (var (start, end, items) in merged)
        {
            var readResult = LineEngine.ReadLines(path, start, end, truncate: 0, stripImports, normalizeIndentFn, CancellationToken);
            if (readResult.Error is not null) continue;

            var lines = readResult.Data is not null ? ExtractLines(readResult.Data) : Array.Empty<string>();

            var serialized = JsonSerializer.Serialize(new { _r = lines });
            if (totalChars + serialized.Length > maxChars)
                throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make(
                    $"Response would exceed maxChars ({maxChars}). " +
                    $"Returned {results.Count} of {merged.Count} function groups.",
                    FReaderError.MAX_CHARS_EXCEEDED,
                    ("_returned", results.Count),
                    ("_total_groups", merged.Count))));
            totalChars += serialized.Length;

            var funcNames = string.Join(", ", items.Select(i => i.F.Name));
            var resultEntry = new Dictionary<string, object?>
            {
                ["_name"] = items.Count == 1 ? items[0].F.Name : funcNames,
                ["_sig"] = items.Count == 1 ? items[0].F.Signature : $"{items.Count} functions merged",
                ["_line_start"] = start,
                ["_line_end"] = end,
                ["_r"] = lines
            };

            if (useAliases)
            {
                var (aliases, aliasedLines, warning) = TypeAliaser.Apply(lines);
                if (aliases is not null)
                {
                    resultEntry["_r"] = aliasedLines;
                    resultEntry["_aliases"] = aliases;
                    resultEntry["_alias_warning"] = warning;
                }
            }
            results.Add(resultEntry);
        }

        var response = new Dictionary<string, object?>
        {
            ["_path"] = path,
            ["_count"] = results.Count,
            ["_results"] = results
        };

        // Warning if > 70% of file
        CancellationToken.ThrowIfCancellationRequested();
        var fileInfo = LineEngine.GetInfo(path, CancellationToken);
        if (fileInfo is Dictionary<string, object?> finfo && finfo.TryGetValue("_lines", out var linesVal) && linesVal is int totalFileLines)
        {
            var coveragePct = (double)totalCoverageLines / totalFileLines * 100;
            if (coveragePct > 70)
                response["_hint"] = $"Reading {coveragePct:F0}% of the file. Consider read(path) instead.";
        }

        return response;
    }

    object HandleSummarize(JsonElement? arguments)
    {
        var path = GetString(arguments, "path") ?? "";
        var useAliases = GetBool(arguments, "aliases") ?? false;

        var summary = ProviderFactory.Summarize(path, CancellationToken);
        if (summary is null)
            throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make("Unable to summarize file (unsupported language or read error)",
                FReaderError.UNSUPPORTED_LANGUAGE)));

        var result = new Dictionary<string, object?> { ["_text"] = summary };

        if (useAliases)
        {
            var summaryLines = summary.Split('\n');
            var (aliases, aliasedLines, warning) = TypeAliaser.Apply(summaryLines);
            if (aliases is not null)
            {
                result["_text"] = string.Join("\n", aliasedLines);
                result["_aliases"] = aliases;
                result["_alias_warning"] = warning;
            }
        }
        return result;
    }

    object HandleGrepInFile(JsonElement? arguments)
    {
        var path = GetString(arguments, "path") ?? "";
        var pattern = GetString(arguments, "pattern") ?? "";
        var context = GetInt(arguments, "context") ?? 2;
        var caseSensitive = GetBool(arguments, "caseSensitive") ?? false;
        var maxMatches = GetInt(arguments, "maxMatches") ?? 20;

        var result = LineEngine.GrepInFile(path, pattern, context, caseSensitive, maxMatches, CancellationToken);
        if (result is Dictionary<string, object?> grepErr && grepErr.ContainsKey("error"))
            throw new McpErrorException(JsonSerializer.Serialize(result));
        return result;
    }

    static string[] ExtractLines(Dictionary<string, object?> data)
    {
        if (data.TryGetValue("_r", out var r) && r is string[] rLines)
            return rLines;
        if (data.TryGetValue("_h", out var h) && h is string[] hLines)
            return hLines;
        return [];
    }

    object HandleSearchFunction(JsonElement? arguments)
    {
        var rootPath = GetString(arguments, "rootPath") ?? "";
        var funcName = GetString(arguments, "name") ?? "";
        var limit = GetInt(arguments, "limit") ?? 20;
        var includeGenerated = GetBool(arguments, "includeGenerated") ?? false;
        var fileExtensions = GetStringArray(arguments, "fileExtensions");

        var result = SearchEngine.SearchFunction(rootPath, funcName, fileExtensions, limit, includeGenerated,
            CancellationToken, (p, msg, t) => ReportProgress(p, msg, t));
        if (result is Dictionary<string, object?> sfErr && sfErr.ContainsKey("error"))
            throw new McpErrorException(JsonSerializer.Serialize(result));
        return result;
    }

    object HandleGrep(JsonElement? arguments)
    {
        var rootPath = GetString(arguments, "rootPath") ?? "";
        var pattern = GetString(arguments, "pattern") ?? "";
        var context = GetInt(arguments, "context") ?? 2;
        var maxMatches = GetInt(arguments, "maxMatches") ?? 50;
        var caseSensitive = GetBool(arguments, "caseSensitive") ?? false;
        var includeGenerated = GetBool(arguments, "includeGenerated") ?? false;
        var fileExtensions = GetStringArray(arguments, "fileExtensions");

        var result = SearchEngine.Grep(rootPath, pattern, fileExtensions, context, maxMatches, caseSensitive, includeGenerated,
            CancellationToken, (p, msg, t) => ReportProgress(p, msg, t));
        if (result is Dictionary<string, object?> grepErr && grepErr.ContainsKey("error"))
            throw new McpErrorException(JsonSerializer.Serialize(result));
        return result;
    }

    object HandleRecordBenchmark(JsonElement? arguments)
    {
        var featureName = GetString(arguments, "featureName") ?? "";
        var score = GetDouble(arguments, "score") ?? 0;
        var path = GetString(arguments, "path") ?? "";
        var root = FeatureScoreManager.FindProjectRoot(path);
        if (root is null)
            throw new McpErrorException(JsonSerializer.Serialize(FReaderError.Make("Could not detect a project root (no .git, .sln, or .csproj found)",
                FReaderError.INVALID_REQUEST)));
        var msg = FeatureScoreManager.RecordScore(root, featureName, score);
        return new { _message = msg, _project_root = root };
    }

    static bool ClassContainsFunction(string path, FunctionInfo func, string className, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var code = File.ReadAllText(path);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code, cancellationToken: ct);
            var root = tree.GetRoot(ct);

            foreach (var type in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
            {
                if (!string.Equals(type.Identifier.Text, className, StringComparison.OrdinalIgnoreCase))
                    continue;

                var span = type.GetLocation().GetLineSpan();
                var typeStart = span.StartLinePosition.Line + 1;
                var typeEnd = span.EndLinePosition.Line + 1;

                if (func.LineStart >= typeStart && func.LineEnd <= typeEnd)
                    return true;
            }
        }
        catch { }
        return false;
    }

    protected override string? GetHarnessInstructions() => Harness;

    protected override object GetInstructions()
    {
        var instr = new List<string>
        {
            "=== HARNESS ===",
            "Use FReader for ALL file reads -- saves 40-60% tokens vs raw read+grep.",
            "",
            "=== FILES TO AVOID ===",
            "DO NOT use FReader for .md/.markdown/.rmd files -- the tab-prefixed format",
            "strips markdown formatting. Use the native file read tool instead.",
            "",
            "Exception: if you are about to edit a file with the native Edit or Write tool,",
            "use the native Read tool for that file -- not FReader. The native Edit/Write",
            "tools require a prior native Read and will reject an FReader read as insufficient.",
            "",
            "=== GREENFIELD ===",
            "If the working directory contains no source files, skip summarize,",
            "list_functions, and read_function -- they return empty results.",
            "Use FWriter create_file instead.",
            "",
            "  summarize         | 87-96% compression (prefer over reading entire file)",
            "  read_function     | read body in 1 call (prefer over list+extract+read)",
            "  read_functions    | batch read (prefer over multiple read_function)",
            "  search_function   | cross-file search (prefer over glob+grep)",
            "  grep_in_file      | single-file regex (prefer over read+manual scan)",
            "  list_functions    | function listing with lineEnd",
            "  extract_function  | line range lookup",
            "  read              | line-range reading (use summarize first for unfamiliar files)",
            "  info              | file metadata",
            "",
            "=== SERVER ===",
            "FReader -- file reader MCP server",
            "",
            "=== TOOLS (11 + record_benchmark [HUMAN-ONLY]) ===",
            "read(path, opts?)           -- read lines, tab-prefix format",
            "  opts: lineStart, lineEnd, truncate, stripImports, normalizeIndent, aliases",
            "",
            "info(path)                  -- file metadata (size, lines, encoding, modified)",
            "",
            "list_functions(path, sys?)  -- function list with lineEnd",
            "",
            "extract_function(p, name, pt?, cls?) -- get line range(s) of a function",
            "",
            "read_function(p, name, op?) -- read body in 1 call",
            "  opts: parameterTypes, className, includeDocs, stripImports, normalizeIndent, aliases",
            "",
            "read_functions(p, names,?)  -- batch read N functions",
            "  merges adjacent ranges, sorted by line number",
            "  opts: stripImports, normalizeIndent, aliases, maxChars",
            "",
            "summarize(p)                -- structural overview (87-96% savings)",
            "  opts: aliases",
            "",
            "grep_in_file(p, pat, ctx?)  -- regex search in single file",
            "",
            "search_function(root, name) -- cross-file function search",
            "  opts: fileExtensions, limit, includeGenerated",
            "",
            "grep(root, pattern)         -- cross-file regex search",
            "  opts: fileExtensions, context, maxMatches, caseSensitive, includeGenerated",
            "",
            "record_benchmark(feat, score, path) -- HUMAN-ONLY. Records a 0-1 benchmark score for a feature.",
            "=== COMPACT FIELDS ===",
            "_h/_r = columnar data    _truncated/_total = capped output",
            "_name/_sig = func info   _line_start/_line_end = range",
            "error/error_code = errors (no _ prefix on errors)",
            "_text = text output      _match = overloads",
            "_results = batch results _path/_size/_lines = metadata",
            "_aliases/_alias_warning = type aliases active",
            "_available_overloads = overload list on mismatch",
            "_total_matches/_search_time_ms = search results",
            "",
            "=== TIPS ===",
            "1. For unfamiliar files, start with summarize -- not read (87-96% savings)",
            "2. Use read_function instead of list+extract+read (saves 2 calls)",
            "3. Use read_functions for multi-function workflows",
            "4. Use grep_in_file when you know the file but not the location",
            "5. Use search_function when you don't know the file",
            "6. set parameterTypes to disambiguate overloads in read_function",
            "7. Use className when the same name exists in multiple classes",
            "8. Enable aliases for files with repeated long type names",
            "9. Tab-prefix format: \"N\\tcontent\" -- parse by splitting on first tab",
            "10. Error responses use error + error_code (no underscore prefix)",
            "11. read truncates at 'truncate' param (default 200). _truncated + _next = continuation.",
            "12. timeoutMs defaults: 30000 for most tools, 60000 for grep/search_function. Omit unless you need longer.",
            "",
        };
        instr.Add("=== TIMEOUT ===");
        instr.Add("timeoutMs defaults to 30000 (read/search) / 60000 (grep).");
        instr.Add("");
        instr.Add("=== SPECIALIZED PROVIDERS (language-aware extraction) ===");
        instr.Add("The following file types have dedicated language providers for AST-level extraction:");
        instr.Add("");
        instr.Add("  .cs       | CSharpProvider  | Roslyn static analysis -- full class, method, property, event extraction");
        instr.Add("  .ts .tsx  | JsTsProvider    | TypeScript compiler AST -- functions, methods, constructors, get/set, arrows");
        instr.Add("  .mts .cts | JsTsProvider    | (same as TS -- ES module/CJS variants)");
        instr.Add("  .js .jsx  | JsTsProvider    | TypeScript compiler AST -- includes JSDoc type inference");
        instr.Add("  .mjs .cjs | JsTsProvider    | (same as JS -- ES module/CJS variants)");
        instr.Add("");
        instr.Add("All specialized providers support: list_functions, read_function (with overload disambiguation");
        instr.Add("via parameterTypes), read_functions (batch), extract_function, summarize (87-96% compression).");
        instr.Add("");
        instr.Add("Files without a specialized provider (e.g. .css, .html, .py, .json, .md) still support");
        instr.Add("read, info, grep_in_file -- no provider needed for line-range reading and regex search.");
        instr.Add("");
        instr.Add("Use summarize first to get a structural overview, then read_function to drill into specific functions.");
        instr.Add("This avoids reading the entire file and saves 60-90% tokens.");
        return new { _h = instr.ToArray() };
    }
}
