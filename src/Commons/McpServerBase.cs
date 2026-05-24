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
    bool _initialized;
    CancellationTokenSource? _timeoutCts;
    string? _progressToken;
    bool _canNotify;
    string? _notifyDescription;
    string? _notifySender;
    string? _notifyProject;
    readonly Queue<long> _notifyTimestamps = new();

    protected CancellationToken CancellationToken => _timeoutCts?.Token ?? System.Threading.CancellationToken.None;

    protected McpServerBase(McpServerConfig config)
    {
        _config = config;
        _json = GetJsonOptions();
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
    protected virtual string GetNotificationContext(string toolName, JsonElement? arguments) => toolName;

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
            _notifySender = null;
            _notifyProject = null;
            _notifyDescription = GetNotificationContext(name, arguments);
            if (req.Params?.TryGetProperty("_meta", out var meta) == true)
            {
                if (meta.TryGetProperty("progressToken", out var pt))
                    _progressToken = pt.ValueKind == JsonValueKind.String ? pt.GetString() : pt.GetRawText();
                if (meta.TryGetProperty("canNotify", out var cn))
                    _canNotify = cn.ValueKind == JsonValueKind.True;
                if (meta.TryGetProperty("sender", out var sn))
                    _notifySender = sn.GetString();
                if (meta.TryGetProperty("project", out var pj))
                    _notifyProject = pj.GetString();
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
            if (_notifyDescription is not null)
                obj["_task"] = _notifyDescription;
            return obj;
        }

        // LLM did not opt in → fire cold notification
        var desc = _notifyDescription ?? _config.Name;
        var sender = _notifySender;
        var project = _notifyProject;
        _ = Task.Run(() => ColdFireNotification(elapsedMs, desc, sender, project));
        return result;
    }

    void ColdFireNotification(long elapsedMs, string description, string? sender, string? project)
    {
        try
        {
            var helperName = OperatingSystem.IsWindows() ? "NotifierHelper.exe" : "NotifierHelper";
            var helper = Path.Combine(AppContext.BaseDirectory, helperName);
            if (!File.Exists(helper)) return;

            var title = sender is not null ? $"{sender}: {description}" : $"{_config.Name}: {description}";
            var message = project is not null
                ? $"{project} | Completed in {elapsedMs / 1000.0:F1}s"
                : $"Completed in {elapsedMs / 1000.0:F1}s";

            var psi = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add("--message");
            psi.ArgumentList.Add(message);
            psi.ArgumentList.Add("--type");
            psi.ArgumentList.Add("info");
            psi.ArgumentList.Add("--sender");
            psi.ArgumentList.Add(sender ?? _config.Name);
            psi.ArgumentList.Add("--dismiss-after-ms");
            psi.ArgumentList.Add("8000");
            psi.ArgumentList.Add("--task");
            psi.ArgumentList.Add(description);
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


}
