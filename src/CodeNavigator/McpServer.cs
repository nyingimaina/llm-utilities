using System.Text.Json;
using LLMUtilities.Commons;

namespace CodeNavigator;

sealed class McpServer : McpServerBase
{
    readonly CSharpNavigatorProvider _csNav = new();
    JsTsNavigatorProvider? _tsNav;
    WorkspaceAnalyzer? _workspace;

    WorkspaceAnalyzer GetWorkspace(string path) =>
        _workspace ??= WorkspaceAnalyzer.Create(File.Exists(path) ? path : null)!;

    JsTsNavigatorProvider TsNav =>
        _tsNav ?? throw new McpErrorException(JsonSerializer.Serialize(new
        {
            error = "TypeScript worker not available. Ensure Node.js is installed and TypeScript is available via NODE_PATH or node_modules.",
            error_code = "WORKER_UNAVAILABLE"
        }));

    public McpServer()
        : base(new McpServerConfig
        {
            Name = "CodeNavigator",
            Version = GetEntryVersion(),
            InstructionsToolDescription = "Returns compact usage instructions for semantic code navigation via Roslyn (C#) and TypeScript compiler API (TS/JS). Cache in context.",
            AnnouncementDirective = "FEATURE: Semantic code navigation. symbols(path) for full symbol tree, definition(path, symbol), references(path, symbol), hover(path, symbol) for signatures+docs. C#: all 8 tools. TS/JS: symbols, definition, hover, references. workspace_symbols(query) for cross-file search.",
        })
    {
    }

    protected override string? GetHarnessInstructions() => null;

    protected override bool RequiresTimeoutMs(string toolName) => true;

    protected override McpTool[] RegisterTools() => new[]
    {
        new McpTool { Name = "symbols", Description = "All symbols in file: classes, methods, props with line numbers. Requires timeoutMs.", InputSchema = Schema("path") },
        new McpTool { Name = "definition", Description = "File + line of named symbol definition. Requires timeoutMs.", InputSchema = Schema("path", "symbol") },
        new McpTool { Name = "references", Description = "All call sites of named symbol across workspace. Requires timeoutMs.", InputSchema = Schema("path", "symbol") },
        new McpTool { Name = "hover", Description = "Type signature, return type, doc comment. Requires timeoutMs.", InputSchema = Schema("path", "symbol") },
        new McpTool { Name = "workspace_symbols", Description = "Search all symbols matching name fragment. Requires timeoutMs.", InputSchema = Schema(null, "query") },
        new McpTool { Name = "implementations", Description = "Concrete implementations of interface/abstract. Requires timeoutMs.", InputSchema = Schema("path", "symbol") },
        new McpTool { Name = "incoming_calls", Description = "What calls this symbol (one level up). Requires timeoutMs.", InputSchema = Schema("path", "symbol") },
        new McpTool { Name = "outgoing_calls", Description = "What this symbol calls (one level down). Requires timeoutMs.", InputSchema = Schema("path", "symbol") },
    };

    protected override object HandleToolCall(string name, JsonElement? arguments)
    {
        try
        {
            return name switch
            {
                "symbols" => HandleSymbols(arguments),
                "definition" => HandleDefinition(arguments),
                "references" => HandleReferences(arguments),
                "hover" => HandleHover(arguments),
                "workspace_symbols" => HandleWorkspaceSymbols(arguments),
                "implementations" => HandleImplementations(arguments),
                "incoming_calls" => HandleIncomingCalls(arguments),
                "outgoing_calls" => HandleOutgoingCalls(arguments),
                _ => throw new McpErrorException(JsonSerializer.Serialize(new { error = $"Unknown tool: {name}", error_code = "UNKNOWN_TOOL" }))
            };
        }
        catch (McpErrorException) { throw; }
        catch (Exception ex)
        {
            throw new McpErrorException(JsonSerializer.Serialize(new { error = ex.Message, error_code = "INTERNAL_ERROR" }));
        }
    }

    string GetFileExtension(string path) => Path.GetExtension(path).ToLowerInvariant();
    bool IsCsFile(string path) => GetFileExtension(path) == ".cs";
    bool IsTsFile(string path) => new[] { ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs" }.Contains(GetFileExtension(path));

    object HandleSymbols(JsonElement? args)
    {
        var path = GetString(args, "path") ?? "";
        if (!File.Exists(path)) throw Err("FILE_NOT_FOUND", $"File not found: {path}");
        if (IsCsFile(path)) return _csNav.Symbols(path);
        if (IsTsFile(path)) return EnsureWorker().Invoke("symbols", path);
        throw Err("UNSUPPORTED_LANGUAGE", $"Unsupported file type: {path}");
    }

    object HandleDefinition(JsonElement? args)
    {
        var path = GetString(args, "path");
        var symbol = GetString(args, "symbol") ?? "";
        if (path is not null && !File.Exists(path)) throw Err("FILE_NOT_FOUND", $"File not found: {path}");
        if (path is null || IsCsFile(path)) return _csNav.FindDefinition(path, symbol);
        if (IsTsFile(path)) return EnsureWorker().Invoke("definition", path, symbol);
        throw Err("UNSUPPORTED_LANGUAGE", $"Unsupported file type: {path}");
    }

    object HandleReferences(JsonElement? args)
    {
        var path = GetString(args, "path") ?? "";
        var symbol = GetString(args, "symbol") ?? "";
        if (!File.Exists(path)) throw Err("FILE_NOT_FOUND", $"File not found: {path}");
        if (IsCsFile(path)) return _csNav.FindReferences(path, symbol, GetWorkspace(path));
        if (IsTsFile(path)) return EnsureWorker().Invoke("references", path, symbol);
        throw Err("UNSUPPORTED_LANGUAGE", $"Unsupported file type: {path}");
    }

    object HandleHover(JsonElement? args)
    {
        var path = GetString(args, "path") ?? "";
        var symbol = GetString(args, "symbol") ?? "";
        if (!File.Exists(path)) throw Err("FILE_NOT_FOUND", $"File not found: {path}");
        if (IsCsFile(path)) return _csNav.Hover(path, symbol);
        if (IsTsFile(path)) return EnsureWorker().Invoke("hover", path, symbol);
        throw Err("UNSUPPORTED_LANGUAGE", $"Unsupported file type: {path}");
    }

    object HandleWorkspaceSymbols(JsonElement? args)
    {
        var query = GetString(args, "query") ?? "";
        var ws = WorkspaceAnalyzer.Create(null);
        return _csNav.WorkspaceSymbols(query, ws);
    }

    object HandleImplementations(JsonElement? args)
    {
        var path = GetString(args, "path") ?? "";
        var symbol = GetString(args, "symbol") ?? "";
        if (!File.Exists(path)) throw Err("FILE_NOT_FOUND", $"File not found: {path}");
        if (IsCsFile(path)) return _csNav.FindImplementations(path, symbol, GetWorkspace(path));
        throw Err("UNSUPPORTED_LANGUAGE", $"TS implementations not yet supported");
    }

    object HandleIncomingCalls(JsonElement? args)
    {
        var path = GetString(args, "path") ?? "";
        var symbol = GetString(args, "symbol") ?? "";
        if (!File.Exists(path)) throw Err("FILE_NOT_FOUND", $"File not found: {path}");
        if (IsCsFile(path)) return _csNav.FindCallers(path, symbol, GetWorkspace(path));
        throw Err("UNSUPPORTED_LANGUAGE", $"TS call graph not yet supported");
    }

    object HandleOutgoingCalls(JsonElement? args)
    {
        var path = GetString(args, "path") ?? "";
        var symbol = GetString(args, "symbol") ?? "";
        if (!File.Exists(path)) throw Err("FILE_NOT_FOUND", $"File not found: {path}");
        if (IsCsFile(path)) return _csNav.FindCallees(path, symbol);
        throw Err("UNSUPPORTED_LANGUAGE", $"TS call graph not yet supported");
    }

    JsTsNavigatorProvider EnsureWorker()
    {
        if (_tsNav is null)
        {
            var workerDir = Path.GetDirectoryName(typeof(McpServer).Assembly.Location) ?? ".";
            _tsNav = new JsTsNavigatorProvider(Path.Combine(workerDir, "ts-worker.js"));
        }
        return _tsNav;
    }

    static object Schema(string? required1, string? required2 = null)
    {
        var props = new Dictionary<string, object>();
        var req = new List<string>();

        if (required1 is not null) { props["path"] = new { type = "string" }; req.Add("path"); }
        if (required2 is not null) { props["symbol"] = new { type = "string" }; req.Add("symbol"); }
        if (required2 == "query") { props["query"] = new { type = "string" }; req.Add("query"); }
        props["timeoutMs"] = new { type = "integer", description = "Required." };
        req.Add("timeoutMs");

        return new { type = "object", properties = props, required = req.ToArray() };
    }

    static McpErrorException Err(string code, string message) =>
        new McpErrorException(JsonSerializer.Serialize(new { error = message, error_code = code }));

    protected override object GetInstructions()
    {
        var instr = new List<string>
        {
            "=== SERVER ===",
            "CodeNavigator -- semantic code navigation (Roslyn + TS compiler API)",
            "",
            "=== TOOLS ===",
            "symbols(path) -- all symbols with line numbers",
            "definition(path, symbol) -- definition location",
            "references(path, symbol) -- all call sites across workspace",
            "hover(path, symbol) -- type signature and doc comment",
            "workspace_symbols(query) -- search all symbols by name fragment",
            "implementations(path, symbol) -- concrete impls of interface/abstract",
            "incoming_calls(path, symbol) -- what calls this symbol",
            "outgoing_calls(path, symbol) -- what this symbol calls",
            "",
            "=== LANGUAGE SUPPORT ===",
            ".cs  | CSharp  | Full Roslyn -- all 8 tools",
            ".ts/.tsx/.js/.jsx  | TypeScript | symbols, definition, hover, references",
            ".mts/.cts/.mjs/.cjs  | TypeScript | same as above",
            "",
            "=== WORKSPACE FEATURES ===",
            "references(path, symbol)   cross-file search across all .cs files in workspace",
            "implementations(path, symbol)  finds concrete classes implementing interface/abstract",
            "incoming_calls(path, symbol)  finds all callers across workspace",
            "workspace_symbols(query)   searches all symbols by name fragment across workspace",
            "Workspace auto-discovered by walking up from path to find .sln/.csproj.",
            "",
            "=== COMPACT FIELDS ===",
            "_symbols = symbol list with line ranges",
            "_references = per-file call sites",
            "_def = { file, line }",
            "_hover = { signature, doc }",
            "_calls = { caller, callerFile, callerLine, callLine, file }",
            "_implementations = [{ kind, name, file, line }]",
            "error/error_code = structured errors (no _ prefix)",
        };
        instr.AddRange(GetResiliencySection());
        instr.Add("");
        instr.AddRange(GetSelfImprovementSection());
        return new { _h = instr.ToArray() };
    }
}
