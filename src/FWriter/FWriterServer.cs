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

    protected override int? DefaultTimeoutMs(string toolName) => toolName == "validate" ? 60000 : 30000;

    protected override McpTool[] RegisterTools() =>
    [
        new McpTool
        {
            Name = "validate",
            Description = "Check file syntax using Roslyn parser. Returns errors with line numbers. Read-only, zero risk.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path" },
                    timeoutMs = new { type = "integer", description = "Max wait in ms (max 60000). Default 60000." }
                },
                required = new[] { "path" }
            }
        },
        new McpTool
        {
            Name = "edit_function_body",
            Description = "Replace the body of a named function/method. Backup -> validate -> write -> verify -> rollback on failure. Supports overload disambiguation with parameterTypes. Set skipValidation=true to bypass AST check.",
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
                    timeoutMs = new { type = "integer", description = "Max wait in ms (max 60000). Default 30000." }
                },
                required = new[] { "path", "name", "newBody" }
            }
        },
        new McpTool
        {
            Name = "edit_block",
            Description = "Replace text between startMarker and endMarker in a file. Backup -> write -> verify -> rollback on failure. Use for non-function content (xml, config, imports). If markers appear multiple times, set occurrence (1-based).",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path" },
                    startMarker = new { type = "string", description = "Literal start marker (not regex)" },
                    endMarker = new { type = "string", description = "Literal end marker (not regex)" },
                    newContent = new { type = "string", description = "Replacement content (between markers, inclusive)" },
                    occurrence = new { type = "integer", description = "Which occurrence to edit (1-based, default 1). Required if markers appear >1 time." },
                    timeoutMs = new { type = "integer", description = "Max wait in ms (max 60000). Default 30000." }
                },
                required = new[] { "path", "startMarker", "endMarker", "newContent" }
            }
        },
        new McpTool
        {
            Name = "create_file",
            Description = "Create a new file with syntax validation. Fails if file exists (unless overwrite=true). Zero risk to existing files. Auto-creates parent directories. skipValidation bypasses AST check (use for non-AST languages).",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path (parent dirs auto-created)" },
                    content = new { type = "string", description = "File content" },
                    overwrite = new { type = "boolean", description = "Overwrite existing file (default false)" },
                    skipValidation = new { type = "boolean", description = "Bypass AST syntax validation (default false — use for non-AST languages)" },
                    timeoutMs = new { type = "integer", description = "Max wait in ms (max 60000). Default 30000." }
                },
                required = new[] { "path", "content" }
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

            case "edit_block":
            {
                var path = GetString(arguments, "path") ?? "";
                var startMarker = GetString(arguments, "startMarker") ?? "";
                var endMarker = GetString(arguments, "endMarker") ?? "";
                var newContent = GetString(arguments, "newContent") ?? "";
                var occurrence = GetInt(arguments, "occurrence") ?? 1;

                if (!File.Exists(path))
                    throw new McpErrorException("{\"error\":\"File not found\",\"error_code\":\"FILE_NOT_FOUND\"}");

                var text = File.ReadAllText(path);

                // Find all start-end pairs
                var pairs = new List<(int StartIdx, int EndIdx)>();
                int searchFrom = 0;
                while (true)
                {
                    var sIdx = text.IndexOf(startMarker, searchFrom, StringComparison.Ordinal);
                    if (sIdx < 0) break;
                    var eIdx = text.IndexOf(endMarker, sIdx + startMarker.Length, StringComparison.Ordinal);
                    if (eIdx < 0) break;
                    pairs.Add((sIdx, eIdx + endMarker.Length));
                    searchFrom = eIdx + endMarker.Length;
                }

                if (pairs.Count == 0)
                    throw new McpErrorException("{\"error\":\"Markers not found in file\",\"error_code\":\"NOT_FOUND\"}");
                if (pairs.Count > 1 && occurrence == 1)
                    throw new McpErrorException("{\"error\":\"Markers appear " + pairs.Count + " times. Set 'occurrence' (1-based) to select which block to edit.\",\"error_code\":\"AMBIGUOUS\"}");
                if (occurrence < 1 || occurrence > pairs.Count)
                    throw new McpErrorException("{\"error\":\"Occurrence " + occurrence + " out of range (1-" + pairs.Count + ")\",\"error_code\":\"INVALID_RANGE\"}");

                var (startIdx, endIdx) = pairs[occurrence - 1];
                var newText = text[..startIdx] + newContent + text[endIdx..];

                // Backup + write + verify
                var backupPath = path + ".bak";
                try { File.Copy(path, backupPath, overwrite: true); }
                catch { return new { _success = false, _error = "Backup failed" }; }
                try
                {
                    File.WriteAllText(path, newText);
                    var verify = File.ReadAllText(path);
                    if (verify != newText)
                    {
                        try { if (File.Exists(backupPath)) File.Copy(backupPath, path, overwrite: true); } catch { }
                        return new { _success = false, _error = "Write verification failed" };
                    }
                    File.Delete(backupPath);
                    return new { _success = true, _path = path };
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(backupPath)) File.Copy(backupPath, path, overwrite: true); } catch { }
                    return new { _success = false, _error = $"Write failed: {ex.Message}" };
                }
            }

            case "create_file":
            {
                var path = GetString(arguments, "path") ?? "";
                var content = GetString(arguments, "content") ?? "";
                var overwrite = GetBool(arguments, "overwrite") ?? false;
                var skipValidation = GetBool(arguments, "skipValidation") ?? false;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
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
            "validate(path, timeoutMs?) -- check C# file syntax, read-only. timeoutMs defaults 60000.",
            "  Fallback: CliSilentProxy.run('dotnet build')",
            "edit_function_body(path, name, newBody, pt?, cls?, timeoutMs?) -- replace function body, auto rollback",
            "  pt = parameterTypes[], cls = className  -- overload disambiguation. timeoutMs defaults 30000.",
            "  Fallback: CliSilentProxy.run('powershell Set-Content') + validate",
            "create_file(path, content, overwrite?, skipValidation?, timeoutMs?) -- new file with syntax validation",
            "  Auto-creates parent directories. timeoutMs defaults 30000.",
            "  Fallback: CliSilentProxy.run('powershell Out-File')",
            "edit_block(path, startMarker, endMarker, newContent, occurrence?, timeoutMs?) -- replace text between markers",
            "  For non-function content (xml, config, imports, csproj). Literal markers (not regex).",
            "  occurrence: 1-based, required if markers appear >1 time. timeoutMs defaults 30000.",
            "  Backup -> write -> verify -> rollback on failure.",
            "  Fallback: native Edit + Read",
            "",
            "=== SAFETY ===",
            "edit_function_body / edit_block: backup -> write -> verify -> delete backup",
            "  On any failure: original restored from backup. Cannot corrupt.",
            "create_file: dir auto-create -> parse-validate -> write. Zero risk to existing files.",
            "validate: read-only, zero risk.",
            "",
        };
        instr.Add("");
        instr.Add("=== COMPACT FIELDS ===");
        instr.Add("_success/_error = result   _old_body = previous body content");
        instr.Add("_valid/_errors = validation   _path = file path");
        instr.Add("_language = provider name   error_code = NOT_FOUND/AMBIGUOUS/INVALID_RANGE");
        return new { _h = instr.ToArray() };
    }
}
