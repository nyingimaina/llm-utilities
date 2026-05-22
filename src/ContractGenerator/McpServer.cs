using System.Text.Json;
using LLMUtilities.Commons;

namespace ContractGenerator;

sealed class McpServer : McpServerBase
{
    public McpServer()
        : base(new McpServerConfig
        {
            Name = "ContractGenerator",
            Version = GetEntryVersion(),
            InstructionsToolDescription = "Returns compact usage instructions and type-mapping rules for C# to TypeScript interface generation. Cache in context.",
            AnnouncementDirective = "FEATURE: C# to TypeScript interface generator. generate(cs_path) for single file, generate_batch(cs_paths[]) for batch, sync(cs_path, ts_path) to diff existing TS. Deterministic Roslyn-based conversion — never manually translate C# data types. Supports [JsonPropertyName], [JsonIgnore], collections, dictionaries, nullables.",
        })
    {
    }

    protected override string? GetHarnessInstructions() => null;

    protected override McpTool[] RegisterTools() => new[]
    {
        new McpTool
        {
            Name = "generate",
            Description = "Single C# file to TypeScript interface. Requires timeoutMs.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    cs_path = new { type = "string", description = "Path to C# file" },
                    timeoutMs = new { type = "integer", description = "Required." }
                },
                required = new[] { "cs_path", "timeoutMs" }
            }
        },
        new McpTool
        {
            Name = "generate_batch",
            Description = "Multiple C# files to TypeScript interfaces, one output. Requires timeoutMs.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    cs_paths = new { type = "array", items = new { type = "string" }, description = "Paths to C# files" },
                    timeoutMs = new { type = "integer", description = "Required." }
                },
                required = new[] { "cs_paths", "timeoutMs" }
            }
        },
        new McpTool
        {
            Name = "sync",
            Description = "Compare existing TS interface against C# source, return drift diff. Requires timeoutMs.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    cs_path = new { type = "string" },
                    ts_path = new { type = "string" },
                    timeoutMs = new { type = "integer", description = "Required." }
                },
                required = new[] { "cs_path", "ts_path", "timeoutMs" }
            }
        },
    };

    protected override object HandleToolCall(string name, JsonElement? arguments)
    {
        return name switch
        {
            "generate" => HandleGenerate(arguments),
            "generate_batch" => HandleGenerateBatch(arguments),
            "sync" => HandleSync(arguments),
            _ => throw new McpErrorException(System.Text.Json.JsonSerializer.Serialize(new { error = $"Unknown tool: {name}", error_code = "UNKNOWN_TOOL" }))
        };
    }

    object HandleGenerate(JsonElement? args)
    {
        var csPath = GetString(args, "cs_path") ?? "";
        if (!File.Exists(csPath))
            throw new McpErrorException(System.Text.Json.JsonSerializer.Serialize(new { error = $"File not found: {csPath}", error_code = "FILE_NOT_FOUND" }));

        var result = ContractGenerator.Generate(csPath);
        return new { cs_path = csPath, interfaces = result.Interfaces, errors = result.Errors };
    }

    object HandleGenerateBatch(JsonElement? args)
    {
        var paths = GetStringArray(args, "cs_paths");
        if (paths is null || paths.Length == 0)
            throw new McpErrorException(System.Text.Json.JsonSerializer.Serialize(new { error = "cs_paths is required", error_code = "INVALID_INPUT" }));

        var allResults = new List<object>();
        var allErrors = new List<string>();
        foreach (var p in paths)
        {
            if (!File.Exists(p))
            {
                allErrors.Add($"File not found: {p}");
                continue;
            }
            var result = ContractGenerator.Generate(p);
            allResults.Add(new { cs_path = p, interfaces = result.Interfaces });
            allErrors.AddRange(result.Errors);
        }
        return new { generated = allResults, errors = allErrors.Count > 0 ? allErrors : null };
    }

    object HandleSync(JsonElement? args)
    {
        var csPath = GetString(args, "cs_path") ?? "";
        var tsPath = GetString(args, "ts_path") ?? "";
        if (!File.Exists(csPath))
            throw new McpErrorException(System.Text.Json.JsonSerializer.Serialize(new { error = $"C# file not found: {csPath}", error_code = "FILE_NOT_FOUND" }));
        if (!File.Exists(tsPath))
            throw new McpErrorException(System.Text.Json.JsonSerializer.Serialize(new { error = $"TS file not found: {tsPath}", error_code = "FILE_NOT_FOUND" }));

        var result = ContractGenerator.Generate(csPath);
        var tsContent = File.ReadAllText(tsPath);
        var diffs = new List<string>();

        var generatedByPath = result.Interfaces.Values.FirstOrDefault();
        if (generatedByPath is null)
        {
            var expectedName = Path.GetFileNameWithoutExtension(csPath);
            generatedByPath = result.Interfaces.Values.FirstOrDefault(i => i.Contains(expectedName));
        }

        if (generatedByPath is string genContent)
        {
            var genLines = genContent.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet();
            var tsLines = tsContent.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("export")).ToHashSet();
            var missing = genLines.Except(tsLines).Where(l => l.Contains(':')).ToList();
            var extra = tsLines.Except(genLines).Where(l => l.Contains(':')).ToList();

            foreach (var m in missing)
                diffs.Add($"MISSING in TS: {m.Replace(";", "")}");
            foreach (var e in extra)
                diffs.Add($"EXTRA in TS: {e.Replace(";", "")}");
        }

        return new { cs_path = csPath, ts_path = tsPath, drift = diffs };
    }

    protected override object GetInstructions()
    {
        var instr = new List<string>
        {
            "=== SERVER ===",
            "ContractGenerator -- C# to TypeScript contract generator",
            "",
            "=== TOOLS ===",
            "generate(cs_path) -- single C# file to TS interface",
            "generate_batch(cs_paths[]) -- batch generate",
            "sync(cs_path, ts_path) -- diff existing TS against C# source",
            "",
            "=== TYPE MAPPING ===",
            "int/long/short/byte/uint/ulong -> number",
            "double/float/decimal -> number",
            "string -> string  |  bool -> boolean",
            "DateTime/DateTimeOffset/Guid -> string",
            "object -> unknown  |  void -> void",
            "T? (nullable) -> T | null",
            "T[]/List<T>/IEnumerable<T>/ImmutableList<T> -> T[]",
            "Dictionary<K,V>/IDictionary<K,V>/ImmutableDictionary<K,V> -> Record<K,V>",
            "HashSet<T>/ImmutableHashSet<T> -> T[]",
            "",
            "=== NAMING ===",
            "PascalCase -> camelCase (all properties)",
            "[JsonPropertyName(\"...\")] overrides name",
            "[JsonIgnore] excludes property",
            "public class/record/interface -> export default interface I{Name}",
            "",
            "=== LIMITATIONS ===",
            "Enums -> number with comment",
            "Navigation properties -> unknown with comment if file not in same dir",
            "[JsonConverter] attributes flagged with comment",
            "Complex generics noted as comment",
            "",
            "=== OUTPUT ===",
            "Returns TypeScript as string. Does NOT write files - LLM writes via standard tools.",
        };
        instr.AddRange(GetResiliencySection());
        instr.Add("");
        instr.AddRange(GetSelfImprovementSection());
        return new { _h = instr.ToArray() };
    }
}
