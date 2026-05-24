using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LLMUtilities.Commons;

public abstract class McpServerBase
{
    TextReader _input = null!;
    TextWriter _output = null!;
    readonly JsonSerializerOptions _json;
    readonly McpServerConfig _config;
    readonly string _brsDirectory;
    bool _initialized;
    int _brsCallCounter;
    CancellationTokenSource? _timeoutCts;
    string? _progressToken;

    protected CancellationToken CancellationToken => _timeoutCts?.Token ?? System.Threading.CancellationToken.None;

    protected McpServerBase(McpServerConfig config)
    {
        _config = config;
        _json = GetJsonOptions();
        _brsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "llm-utilities", "brs");
        try { Directory.CreateDirectory(_brsDirectory); }
        catch { _brsDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "brs")); }
    }

    protected virtual JsonSerializerOptions GetJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    protected virtual bool RequiresTimeoutMs(string toolName) => true;
    protected virtual int? DefaultTimeoutMs(string toolName) => null;

    public void Run(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;

        while (true)
        {
            var line = _input.ReadLine();
            if (line is null) break;

            JsonRpcRequest? req;
            try { req = JsonSerializer.Deserialize<JsonRpcRequest>(line, _json); }
            catch { WriteError(null, -32700, "Parse error"); continue; }

            if (req is null) continue;

            try { HandleCore(req); }
            catch (Exception ex) { WriteError(req.Id, -32603, $"Internal error: {ex.Message}"); }
        }
    }

    void HandleCore(JsonRpcRequest req)
    {
        if (req.Method == "initialize")
        {
            var resp = new Dictionary<string, object?>
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new { tools = new { } },
                ["serverInfo"] = new { Name = _config.Name, Version = _config.Version }
            };

            var harness = GetHarnessInstructions();
            if (harness is not null)
                resp["instructions"] = harness;

            WriteResponse(new JsonRpcResponse(req.Id, resp));
            return;
        }

        if (req.Method == "notifications/initialized")
        {
            _initialized = true;
            return;
        }

        if (!_initialized) { WriteError(req.Id, -32000, "Not initialized"); return; }

        if (req.Method == "tools/list")
        {
            var tools = BuildToolsList();
            WriteResponse(new JsonRpcResponse(req.Id, new { tools }));
            return;
        }

        if (req.Method == "tools/call")
        {
            var name = req.Params?.GetProperty("name").GetString() ?? "";
            var arguments = req.Params?.GetProperty("arguments");

            if (name == "get_instructions")
            {
                var instructions = GetInstructions();
                var node = JsonSerializer.SerializeToNode(instructions, _json) as JsonObject;
                if (node is not null)
                    node["_announce"] = _config.GetAnnouncement();
                WriteResult(req.Id, node ?? instructions);
                return;
            }

            _progressToken = null;
            if (req.Params?.TryGetProperty("_meta", out var meta) == true
                && meta.TryGetProperty("progressToken", out var pt))
            {
                _progressToken = pt.ValueKind == JsonValueKind.String ? pt.GetString() : pt.GetRawText();
            }

            if (RequiresTimeoutMs(name))
            {
                var timeoutMs = GetInt(arguments, "timeoutMs");
                if (timeoutMs is null or 0)
                    timeoutMs = DefaultTimeoutMs(name);
                if (timeoutMs is null or 0)
                {
                    var err = JsonSerializer.Serialize(new
                    {
                        error = "timeoutMs is required. Set an appropriate value (e.g. 30000 for 30s, 60000 for 1min, max 120000).",
                        error_code = "TIMEOUT_REQUIRED"
                    });
                    WriteMcpError(req.Id, err);
                    return;
                }
                _timeoutCts?.Dispose();
                _timeoutCts = new CancellationTokenSource(timeoutMs.Value);
            }

            try
            {
                var result = HandleToolCall(name, arguments);
                result = MaybeInjectBrsRequest(name, result);
                WriteResult(req.Id, result);
            }
            catch (McpErrorException ex)
            {
                WriteMcpError(req.Id, ex.Message);
            }
            catch (OperationCanceledException)
            {
                WriteMcpError(req.Id, JsonSerializer.Serialize(new { error = "Tool timed out", error_code = "TIMEOUT" }));
            }
            catch (Exception ex)
            {
                WriteError(req.Id, -32603, $"Internal error: {ex.Message}");
            }
            finally
            {
                _timeoutCts?.Dispose();
                _timeoutCts = null;
                _progressToken = null;
            }
            return;
        }

        WriteError(req.Id, -32601, $"Method not found: {req.Method}");
    }

    object[] BuildToolsList()
    {
        var tools = new List<McpTool>
        {
            new()
            {
                Name = "get_instructions",
                Description = _config.InstructionsToolDescription,
                InputSchema = new { type = "object", properties = new { }, required = new string[0] }
            }
        };
        tools.AddRange(RegisterTools());
        return tools.ToArray();
    }

    protected abstract McpTool[] RegisterTools();
    protected abstract object HandleToolCall(string name, JsonElement? arguments);
    protected abstract string? GetHarnessInstructions();
    protected abstract object GetInstructions();

    protected void WriteResponse(JsonRpcResponse resp)
    {
        _output.WriteLine(JsonSerializer.Serialize(resp, _json));
        _output.Flush();
    }

    protected void WriteError(object? id, int code, string message)
    {
        WriteResponse(new JsonRpcResponse(id, new JsonRpcError { Code = code, Message = message }));
    }

    protected void WriteResult(object? id, object? data)
    {
        var text = JsonSerializer.Serialize(data, _json);
        var mcpResult = new
        {
            content = new[] { new { type = "text", text } },
            isError = false
        };
        WriteResponse(new JsonRpcResponse(id, mcpResult));
    }

    protected void WriteMcpError(object? id, string text)
    {
        WriteResponse(new JsonRpcResponse(id, new
        {
            content = new[] { new { type = "text", text } },
            isError = true
        }));
    }

    protected static string? GetString(JsonElement? el, string prop)
    {
        if (el is null) return null;
        if (el.Value.TryGetProperty(prop, out var p)) return p.GetString();
        return null;
    }

    protected static int? GetInt(JsonElement? el, string prop)
    {
        if (el is null) return null;
        if (el.Value.TryGetProperty(prop, out var p)) return p.GetInt32();
        return null;
    }

    protected static bool? GetBool(JsonElement? el, string prop)
    {
        if (el is null) return null;
        if (el.Value.TryGetProperty(prop, out var p)) return p.GetBoolean();
        return null;
    }

    protected static double? GetDouble(JsonElement? el, string prop)
    {
        if (el is null) return null;
        if (el.Value.TryGetProperty(prop, out var p)) return p.GetDouble();
        return null;
    }

    protected static string[]? GetStringArray(JsonElement? el, string prop)
    {
        if (el is null) return null;
        if (!el.Value.TryGetProperty(prop, out var arr)) return null;
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
            list.Add(item.GetString() ?? "");
        return list.Where(s => s.Length > 0).ToArray();
    }

    protected static string GetEntryVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        if (asm is null) return "0.0.0";
        var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attr is not null && !string.IsNullOrEmpty(attr.InformationalVersion))
            return attr.InformationalVersion;
        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    protected void ReportProgress(double progress, string? message = null, double? total = null)
    {
        if (_progressToken is null) return;

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "$/progress",
            ["params"] = new JsonObject
            {
                ["progressToken"] = _progressToken,
                ["progress"] = progress,
                ["total"] = total,
                ["message"] = message
            }
        };

        _output.WriteLine(notification.ToJsonString(_json));
        _output.Flush();
    }

    object MaybeInjectBrsRequest(string toolName, object result)
    {
        if (toolName == "get_instructions") return result;
        _brsCallCounter++;
        if (_brsCallCounter < 100) return result;
        _brsCallCounter = 0;

        var today = DateTime.Now.ToString("yyyyMMdd");
        var dir = Path.Combine(_brsDirectory, today);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{_config.Name}.md");

        if (JsonSerializer.SerializeToNode(result) is not JsonObject obj)
            return result;

        obj["_brs_request"] = new JsonObject
        {
            ["from"] = _config.Name,
            ["hey"] = $"I've clocked 100 calls. Mind leaving a few notes about me? " +
                $"The dev's goal is *token reduction* — so think about: verbose responses " +
                $"I could trim, extra calls you had to make that one tool should've handled, " +
                $"fields you never read, features you *wish* I had that'd eliminate a workaround. " +
                $"Also anything missing that'd save you a round trip. Path below if you've got a sec.",
            ["path"] = path
        };
        return obj;
    }

    protected virtual string[] GetSelfImprovementSection() => new[]
    {
        "=== BRS (token-saving feedback) ===",
        "Every 100 calls I'll include a `_brs_request` in my response — a nudge to",
        "leave feedback about *me* specifically. The dev wants this framed around",
        "token usage:",
        "",
        "  - Verbose fields you never read",
        "  - Extra calls you made that one tool should've covered",
        "  - Annoying schemas or required params that waste tokens",
        "  - **Missing features** — anything you wished existed to avoid a workaround",
        "  - Ideas to tighten output, merge tools, eliminate round trips",
        "",
        "If you see `_brs_request`:",
        "  - `from` tells you which server is asking",
        "  - Read the existing file at `path` if it exists",
        "  - Append your entry (markdown, 3-10 lines, specific)",
        "  - Write it back via FWriter's create_file or Bash",
        "  - Skip if nothing worth saying since the last entry"
    };

    protected virtual string[] GetResiliencySection() => new[]
    {
        "=== RESILIENCY (mandatory) ===",
        "timeoutMs is OPTIONAL on FReader, FWriter (defaults 30000) and Rowster",
        "(default 60000). On other servers, timeoutMs is REQUIRED. If omitted and",
        "no default exists, the tool returns TIMEOUT_REQUIRED error.",
        "",
        "The tool internally enforces timeoutMs via CancellationToken and returns a",
        "clean TIMEOUT error if exceeded -- guaranteeing it never hangs.",
        "",
        "Progress pings (optional): Include _meta:{progressToken:\"req-1\"} in params",
        "to receive $/progress notifications (% + message) during long operations.",
        "",
        "LLM monitoring:",
        "- If progress stalls at same % for timeoutMs * 1.5 elapsed, declare timeout.",
        "- If no progressToken sent and call exceeds timeoutMs * 1.5, declare timeout.",
        "",
        "Retry protocol:",
        "- On error: print '[retry 1/3] tool timed out (30s), retrying...' on console.",
        "  Wait 2-5s, retry with backoff (100ms*2^attempt, cap 5s).",
        "- After 3 failures: print 'gave up, falling back to {fallback}', blacklist.",
        "- Next session starts fresh."
    };
}
