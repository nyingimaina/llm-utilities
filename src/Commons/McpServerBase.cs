using System.Diagnostics;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Serilog;
using System.Runtime.InteropServices;

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
    string? _notifyArgsJson;
    string? _notifyCwd;
    string? _notifyTool;
    readonly Queue<long> _notifyTimestamps = new();
    readonly List<NotificationInfo> _notifyPending = new();

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
    protected virtual int? AutoNotifyThresholdMs => _config.AutoNotifyThresholdMs;
    protected virtual Dictionary<string, int>? PerToolAutoNotifyThresholdMs => null;
    protected virtual int NotifyMaxPerWindow => 3;
    protected virtual int NotifyWindowMs => 60_000;
    protected virtual string GetNotificationContext(string toolName, JsonElement? arguments)
    {
        var path = GetString(arguments, "path") ?? GetString(arguments, "rootPath");
        return path is not null ? $"{toolName}({Path.GetFileName(path)})" : toolName;
    }

    public void Run(TextReader input, TextWriter output)
    {
        Log.Information("Server started: {Name} v{Version}", _config.Name, _config.Version);
        _input = input;
        _output = output;

        while (true)
        {
            var line = _input.ReadLine();
            if (line is null) break;

            JsonRpcRequest? req;
            try { req = JsonSerializer.Deserialize<JsonRpcRequest>(line, _json); }
            catch { Log.Warning("Parse error on input line"); WriteError(null, -32700, "Parse error"); continue; }

            if (req is null) continue;

            try { HandleCore(req); }
            catch (Exception ex) { Log.Error(ex, "Unhandled exception handling request {Method}", req.Method); WriteError(req.Id, -32603, $"Internal error: {ex.Message}"); }
        }

        Log.Information("Server shutting down");
    }

    void HandleCore(JsonRpcRequest req)
    {
        if (req.Method == "initialize")
        {
            var caps = new Dictionary<string, object?> { ["tools"] = new { } };
            var harness = GetHarnessInstructions() ?? _config.HarnessInstructions;
            if (harness is not null)
                caps["instructions"] = harness;

            var resp = new Dictionary<string, object?>
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = caps,
                ["serverInfo"] = new { Name = _config.Name, Version = _config.Version }
            };

            Log.Information("Client initialized (protocol 2024-11-05)");
            WriteResponse(new JsonRpcResponse(req.Id, resp));
            return;
        }

        if (req.Method == "notifications/initialized")
        {
            _initialized = true;
            Log.Information("Received initialized notification");
            return;
        }

        if (!_initialized) { Log.Warning("Request {Method} before initialization", req.Method); WriteError(req.Id, -32000, "Not initialized"); return; }

        if (req.Method == "tools/list")
        {
            var tools = BuildToolsList();
            Log.Information("Tools listed ({Count} tools)", tools.Length);
            WriteResponse(new JsonRpcResponse(req.Id, new { tools }));
            return;
        }

        if (req.Method == "tools/call")
        {
            var name = req.Params?.GetProperty("name").GetString() ?? "";
            var arguments = req.Params?.GetProperty("arguments");
            Log.Information("Tool call: {Tool}", name);

            if (name == "get_instructions")
            {
                var instructions = GetInstructions();
                var node = JsonSerializer.SerializeToNode(instructions, _json) as JsonObject;
                if (node is not null)
                    node["_announce"] = _config.GetAnnouncement();
                Log.Information("Tool completed: {Tool}", name);
                WriteResult(req.Id, node ?? instructions);
                return;
            }

            _progressToken = null;
            _canNotify = false;
            _notifySender = null;
            _notifyProject = null;
            _notifyArgsJson = null;
            _notifyCwd = Environment.CurrentDirectory;
            _notifyTool = name;
            _notifyDescription = GetNotificationContext(name, arguments);
            if (arguments is not null)
            {
                try { _notifyArgsJson = arguments.Value.GetRawText(); }
                catch { }
            }
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
                Log.Information("Tool completed: {Tool} in {ElapsedMs}ms", name, sw.ElapsedMilliseconds);
                result = MaybeInjectShouldNotify(name, result, sw.ElapsedMilliseconds);
                WriteResult(req.Id, result);
            }
            catch (McpErrorException ex)
            {
                Log.Warning("Tool returned error: {Tool} - {Message}", name, ex.Message);
                WriteMcpError(req.Id, ex.Message);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Tool timed out: {Tool}", name);
                WriteMcpError(req.Id, JsonSerializer.Serialize(new { error = "Tool timed out", error_code = "TIMEOUT" }));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Tool threw exception: {Tool}", name);
                WriteError(req.Id, -32603, $"Internal error: {ex.Message}");
            }
            finally
            {
                _timeoutCts?.Dispose();
                _timeoutCts = null;
                _progressToken = null;
                _canNotify = false;
                _notifyArgsJson = null;
                _notifyCwd = null;
                _notifyTool = null;
            }
            return;
        }

        Log.Warning("Method not found: {Method}", req.Method);
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
    protected virtual string? GetHarnessInstructions() => _config.HarnessInstructions;
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
        var baseThreshold = AutoNotifyThresholdMs;
        var perTool = PerToolAutoNotifyThresholdMs;
        int? threshold;
        if (perTool is not null && perTool.TryGetValue(toolName, out var pt))
            threshold = pt;
        else
            threshold = baseThreshold;
        if (threshold is null || elapsedMs < threshold) return result;
        if (toolName == "get_instructions" || toolName == "get_log") return result;

        var maxPerWindow = NotifyMaxPerWindow;
        var windowMs = NotifyWindowMs;
        var now = Stopwatch.GetTimestamp();
        var windowTicks = windowMs * Stopwatch.Frequency / 1000;

        // LLM opted in via _meta._canNotify → inject flag so LLM calls Notifier itself
        if (_canNotify)
        {
            lock (_notifyTimestamps)
            {
                while (_notifyTimestamps.Count > 0 && now - _notifyTimestamps.Peek() > windowTicks)
                    _notifyTimestamps.Dequeue();

                if (JsonSerializer.SerializeToNode(result) is not JsonObject obj)
                    return result;
                obj["_shouldNotify"] = true;
                if (_notifyDescription is not null)
                    obj["_task"] = _notifyDescription;
                if (_notifyTimestamps.Count >= maxPerWindow)
                {
                    obj["_notify_cooldown_ms"] = 0;
                    return obj;
                }
                _notifyTimestamps.Enqueue(now);
            }
            return result;
        }

        // LLM did NOT opt in → fire notification from the server
        var info = BuildNotificationInfo(toolName, elapsedMs, result);
        lock (_notifyTimestamps)
        {
            while (_notifyTimestamps.Count > 0 && now - _notifyTimestamps.Peek() > windowTicks)
                _notifyTimestamps.Dequeue();

            if (_notifyTimestamps.Count >= maxPerWindow)
            {
                // Rate-limited: accumulate for grouped notification
                lock (_notifyPending)
                    _notifyPending.Add(info);
                return result;
            }

            _notifyTimestamps.Enqueue(now);

            // Check for accumulated pending entries
            List<NotificationInfo>? pending = null;
            lock (_notifyPending)
            {
                if (_notifyPending.Count > 0)
                {
                    pending = [.._notifyPending];
                    _notifyPending.Clear();
                }
            }

            if (pending is not null)
            {
                pending.Add(info);
                var groupInfo = info with
                {
                    GroupCount = pending.Count,
                    Grouped = pending,
                    Summary = $"{_config.Name}: {pending.Count} tools completed",
                };
                NotifierLog.Write(groupInfo);
                _ = Task.Run(() => ColdFireNotification(groupInfo));
            }
            else
            {
                NotifierLog.Write(info);
                _ = Task.Run(() => ColdFireNotification(info));
            }
        }
        return result;
    }

    NotificationInfo BuildNotificationInfo(string toolName, long elapsedMs, object result)
    {
        bool ok = true;
        string? error = null;
        string? resultHint = null;
        try
        {
            var node = JsonSerializer.SerializeToNode(result);
            if (node is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("isError", out var isErrNode) &&
                    isErrNode is JsonValue isErrVal && isErrVal.TryGetValue<bool>(out var isErr) && isErr)
                    ok = false;

                if (obj.TryGetPropertyValue("content", out var contentNode) &&
                    contentNode is JsonArray arr && arr.Count > 0 &&
                    arr[0] is JsonObject first &&
                    first.TryGetPropertyValue("text", out var textNode) &&
                    textNode is JsonValue textVal)
                {
                    var raw = textVal.TryGetValue<string>(out var s) ? s ?? "" : "";
                    if (raw.Length > 0)
                    {
                        var firstLine = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
                        resultHint = firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine;
                    }
                }
            }
        }
        catch { }

        var cwd = _notifyCwd ?? Environment.CurrentDirectory;
        var project = _notifyProject;
        if (project is null)
        {
            var parentDir = Path.GetFileName(Path.GetDirectoryName(cwd));
            var grandparent = Path.GetFileName(cwd);
            if (grandparent is not null && parentDir is not null)
                project = $"{parentDir}/{grandparent}";
            else
                project = Path.GetFileName(cwd);
        }

        return new NotificationInfo
        {
            Server = _config.Name,
            LlmClient = ProcessHelper.DetectLlmClient(),
            Tool = toolName,
            ArgsJson = _notifyArgsJson,
            ArgsDisplay = _notifyDescription,
            Project = project,
            Cwd = cwd,
            ElapsedMs = elapsedMs,
            Ok = ok,
            Error = error,
            Timestamp = DateTime.UtcNow,
            ResultHint = resultHint,
        };
    }

    void ColdFireNotification(NotificationInfo info)
    {
        try
        {
            var helperName = OperatingSystem.IsWindows() ? "NotifierHelper.exe" : "NotifierHelper";
            var helper = Path.Combine(AppContext.BaseDirectory, helperName);
            if (!File.Exists(helper))
            {
                Log.Warning("NotifierHelper.exe not found at {Path}; notification for {Server}.{Tool} not shown", helper, info.Server, info.Tool);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--data");
            psi.ArgumentList.Add(info.ToJson());
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to launch NotifierHelper for {Server}.{Tool}", info.Server, info.Tool);
        }
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
