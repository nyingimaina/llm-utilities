using System.Diagnostics;
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
    bool _canNotify;
    readonly Queue<long> _notifyTimestamps = new();

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
    protected virtual int? AutoNotifyThresholdMs => null;
    protected virtual int NotifyMaxPerWindow => 3;
    protected virtual int NotifyWindowMs => 60_000;

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
            _canNotify = false;
            if (req.Params?.TryGetProperty("_meta", out var meta) == true)
            {
                if (meta.TryGetProperty("progressToken", out var pt))
                    _progressToken = pt.ValueKind == JsonValueKind.String ? pt.GetString() : pt.GetRawText();
                if (meta.TryGetProperty("canNotify", out var cn))
                    _canNotify = cn.ValueKind == JsonValueKind.True;
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
                var sw = Stopwatch.StartNew();
                var result = HandleToolCall(name, arguments);
                sw.Stop();
                result = MaybeInjectBrsRequest(name, result);
                result = MaybeInjectShouldNotify(name, result, sw.ElapsedMilliseconds);
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
                _canNotify = false;
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

    object MaybeInjectShouldNotify(string toolName, object result, long elapsedMs)
    {
        var threshold = AutoNotifyThresholdMs;
        if (threshold is null || elapsedMs < threshold) return result;
        if (toolName == "get_instructions" || toolName == "get_log") return result;

        // Rate-limit: sliding window
        var maxPerWindow = NotifyMaxPerWindow;
        var windowMs = NotifyWindowMs;
        var now = Stopwatch.GetTimestamp();
        var windowTicks = windowMs * Stopwatch.Frequency / 1000;

        lock (_notifyTimestamps)
        {
            while (_notifyTimestamps.Count > 0 && now - _notifyTimestamps.Peek() > windowTicks)
                _notifyTimestamps.Dequeue();

            if (_notifyTimestamps.Count >= maxPerWindow)
            {
                // Rate-limited — handle based on opt-in
                if (_canNotify)
                {
                    // LLM opted in: tell it to notify but flag the cooldown
                    if (JsonSerializer.SerializeToNode(result) is not JsonObject obj)
                        return result;
                    obj["_shouldNotify"] = true;
                    var oldest = _notifyTimestamps.Peek();
                    var elapsedWinTicks = now - oldest;
                    var remainingMs = windowMs - (elapsedWinTicks * 1000 / Stopwatch.Frequency);
                    obj["_notify_cooldown_ms"] = Math.Max(0, remainingMs);
                    return obj;
                }
                // Not opted in: suppress silently
                return result;
            }

            _notifyTimestamps.Enqueue(now);
        }

        // LLM opted in via _meta._canNotify → give it the flag so it calls Notifier itself
        if (_canNotify)
        {
            if (JsonSerializer.SerializeToNode(result) is not JsonObject obj)
                return result;
            obj["_shouldNotify"] = true;
            return obj;
        }

        // LLM did not opt in → fire cold notification
        _ = Task.Run(() => ColdFireNotification(elapsedMs));
        return result;
    }

    void ColdFireNotification(long elapsedMs)
    {
        try
        {
            var helperName = OperatingSystem.IsWindows() ? "NotifierHelper.exe" : "NotifierHelper";
            var helper = Path.Combine(AppContext.BaseDirectory, helperName);
            if (!File.Exists(helper)) return;

            var psi = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add($"{_config.Name}: {elapsedMs / 1000.0:F1}s");
            psi.ArgumentList.Add("--message");
            psi.ArgumentList.Add($"Completed in {elapsedMs / 1000.0:F1}s");
            psi.ArgumentList.Add("--type");
            psi.ArgumentList.Add("info");
            psi.ArgumentList.Add("--sender");
            psi.ArgumentList.Add(_config.Name);
            psi.ArgumentList.Add("--dismiss-after-ms");
            psi.ArgumentList.Add("8000");
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch { /* best-effort */ }
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
        "- Next session starts fresh.",
        "",
        "=== NOTIFICATION WORKFLOW (mandatory read) ===",
        "This suite includes a Notifier MCP server that sends desktop toast notifications.",
        "IT IS YOUR RESPONSIBILITY to ensure the user receives notifications about important",
        "events. Here is how the notification contract works:",
        "",
        "1. For ANY tool call that takes longer than a threshold (typically >5s), the server",
        "   will include _shouldNotify: true in its response IF you pass",
        "   _meta:{canNotify:true} in your tool call parameters.",
        "",
        "2. When you see _shouldNotify: true, you MUST call Notifier.notify() with a",
        "   SPECIFIC, HUMAN-READABLE message. Do not paste raw exit codes or generic",
        "   tool names. Example GOOD: 'dotnet build succeeded (56s, 0 errors, 12 warnings)'",
        "   Example BAD: 'CliSilentProxy: run completed in 8.2s'. The user needs to know",
        "   WHAT happened, not WHICH tool ran. Include: what was done, outcome, duration,",
        "   and key metrics (error count, test results, etc.).",
        "",
        "3. NOTIFICATION QUALITY RULES:",
        "   - DO include: specific operation name, outcome summary, duration",
        "   - DO NOT include: internal server names, raw exit codes, tool names",
        "   - DO write for a human: 'Build succeeded (12s)' not 'run exit 0'",
        "   - DO mention failures clearly: '3 tests failed, 12 passed (8s)'",
        "   - DO set sender to your LLM name so the user knows who sent it",
        "   - If _notify_cooldown_ms is present, you are rate-limited (max 3/60s).",
        "     Either skip the notification or aggregate: instead of 3 separate calls,",
        "     fire one summarizing 'Completed 3 tasks in the last minute'.",
        "",
        "4. If you do NOT pass _meta:{canNotify:true}, the server fires a generic",
        "   auto-notification. No action needed from you, but the message will be",
        "   mechanical (e.g. 'CliSilentProxy: 8.2s'). This is the fallback — YOU can",
        "   do better by opting in and writing a proper message.",
        "",
        "5. For non-server tasks (code review, analysis, reasoning) you should call",
        "   Notifier.notify() directly. Same quality rules apply.",
        "",
        "6. DO NOT notify for every trivial synchronous step — only when the user",
        "   would benefit from being alerted after tabbing away.",
        "",
        "7. Always set sender (your LLM name) so the user knows who sent it.",
    };
}
