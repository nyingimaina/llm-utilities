using System.Text.Json;
using LLMUtilities.Commons;

namespace FWriter;

sealed class FWriterServer : McpServerBase
{
    readonly Dictionary<string, IWriteProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public FWriterServer() : base(new McpServerConfig
    {
        Name = "FWriter",
        Version = GetEntryVersion(),
        InstructionsToolDescription = "Returns compact instructions for validated code editing. Cache in session context.",
    })
    {
        _providers[".cs"] = new CSharpWriteProvider();
    }

    IWriteProvider? GetProvider(string path)
    {
        var ext = Path.GetExtension(path);
        return _providers.GetValueOrDefault(ext);
    }

    protected override McpTool[] RegisterTools() =>
    [
        new McpTool
        {
            Name = "validate",
            Description = "Check file syntax using Roslyn parser. Returns errors with line numbers. Read-only, zero risk. Requires timeoutMs.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path" },
                    timeoutMs = new { type = "integer", description = "Max wait in ms (max 60000). Required." }
                },
                required = new[] { "path", "timeoutMs" }
            }
        },
        new McpTool
        {
            Name = "edit_function_body",
            Description = "Replace the body of a named function/method. Backup -> validate -> write -> verify -> rollback on failure. Supports overload disambiguation with parameterTypes. Set skipValidation=true to bypass AST check. Requires timeoutMs.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path" },
                    name = new { type = "string", description = "Function name" },
                    newBody = new { type = "string", description = "New function body (everything inside { ... })" },
                    parameterTypes = new { type = "array", items = new { type = "string" }, description = "Overload disambiguation" },
                    className = new { type = "string", description = "Disambiguate cross-class names" },
                    skipValidation = new { type = "boolean", description = "Bypass AST syntax validation (default false)" },
                    timeoutMs = new { type = "integer", description = "Max wait in ms (max 60000). Required." }
                },
                required = new[] { "path", "name", "newBody", "timeoutMs" }
            }
        },
        new McpTool
        {
            Name = "create_file",
            Description = "Create a new file with syntax validation. Fails if file exists (unless overwrite=true). Zero risk to existing files. skipValidation bypasses AST check (use for non-AST languages). Requires timeoutMs.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path" },
                    content = new { type = "string", description = "File content" },
                    overwrite = new { type = "boolean", description = "Overwrite existing file (default false)" },
                    skipValidation = new { type = "boolean", description = "Bypass AST syntax validation (default false — use for non-AST languages)" },
                    timeoutMs = new { type = "integer", description = "Max wait in ms (max 60000). Required." }
                },
                required = new[] { "path", "content", "timeoutMs" }
            }
        },
    ];

    protected override object HandleToolCall(string name, JsonElement? arguments)
    {
        switch (name)
        {
            case "validate":
            {
                var path = GetString(arguments, "path") ?? "";
                var provider = GetProvider(path);
                if (provider is null)
                    throw new McpErrorException("{\"error\":\"Unsupported language for validation\",\"_fallback\":\"Use read() to view the file directly\"}");
                var result = provider.Validate(path);
                return new { _valid = result.Valid, _errors = result.Errors, _language = provider.Language };
            }

            case "edit_function_body":
            {
                var path = GetString(arguments, "path") ?? "";
                var funcName = GetString(arguments, "name") ?? "";
                var newBody = GetString(arguments, "newBody") ?? "";
                var parameterTypes = GetStringArray(arguments, "parameterTypes");
                var className = GetString(arguments, "className");
                var skipValidation = GetBool(arguments, "skipValidation") ?? false;
                var provider = GetProvider(path);
                if (provider is null)
                    throw new McpErrorException("{\"error\":\"Unsupported language for editing\"}");
                var result = provider.EditFunctionBody(path, funcName, newBody, parameterTypes, className, skipValidation);
                if (!result.Success)
                    return new { _success = false, _error = result.Error };
                return new { _success = true, _old_body = result.OldBody };
            }

            case "create_file":
            {
                var path = GetString(arguments, "path") ?? "";
                var content = GetString(arguments, "content") ?? "";
                var overwrite = GetBool(arguments, "overwrite") ?? false;
                var skipValidation = GetBool(arguments, "skipValidation") ?? false;
                var ext = Path.GetExtension(path);
                var provider = _providers.GetValueOrDefault(ext);
                if (provider is null || skipValidation)
                {
                    if (File.Exists(path) && !overwrite)
                        throw new McpErrorException("{\"error\":\"File exists. Set overwrite=true to replace.\"}");
                    File.WriteAllText(path, content);
                    return new { _success = true, _path = path, _note = skipValidation ? "Written with validation skipped" : "Written without syntax validation (unsupported language)" };
                }
                var result = provider.CreateFile(path, content, overwrite);
                if (!result.Success)
                    return new { _success = false, _error = result.Error };
                return new { _success = true, _path = path };
            }

            default:
                throw new McpErrorException("{\"error\":\"Tool not found: " + name + "\"}");
        }
    }

    protected override string? GetHarnessInstructions() => null;
    protected override object GetInstructions()
    {
        var instr = new List<string>
        {
            "=== SERVER ===",
            "FWriter -- validated code editor MCP server",
            "",
            "=== TOOLS ===",
            "validate(path, timeoutMs) -- check C# file syntax, read-only",
            "  Fallback: CliSilentProxy.run('dotnet build')",
            "edit_function_body(path, name, newBody, pt?, cls?, timeoutMs) -- replace function body, auto rollback",
            "  pt = parameterTypes[], cls = className  -- overload disambiguation",
            "  Fallback: CliSilentProxy.run('powershell Set-Content') + validate",
            "create_file(path, content, overwrite?, timeoutMs) -- new file with syntax validation",
            "  Fallback: CliSilentProxy.run('powershell Out-File')",
            "",
            "=== SAFETY ===",
            "edit_function_body: backup -> parse-validate -> write -> verify read-back -> delete backup",
            "  On any failure: original restored from backup. Cannot corrupt.",
            "create_file: parse-validate -> write. Zero risk to existing files.",
            "validate: read-only, zero risk.",
            "",
        };
        instr.AddRange(GetResiliencySection());
        instr.Add("");
        instr.AddRange(GetSelfImprovementSection());
        instr.Add("");
        instr.Add("=== COMPACT FIELDS ===");
        instr.Add("_success/_error = result   _old_body = previous body content");
        instr.Add("_valid/_errors = validation   _path = created file path");
        instr.Add("_language = provider name");
        return new { _h = instr.ToArray() };
    }
}
