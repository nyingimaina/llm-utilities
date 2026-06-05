using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMUtilities.Commons;

namespace McpServers.Tests;

/// <summary>
/// In-process JSON-RPC transport for testing McpServerBase subclasses without
/// spawning child processes. Both directions use BlockingCollection-backed
/// TextReader/TextWriter so the server and test thread can exchange messages
/// over a synchronous pipe.
/// </summary>
sealed class McpTestClient : IDisposable
{
    readonly BlockingPipeReader _serverInput;
    readonly BlockingPipeWriter _serverOutput;
    readonly Thread _serverThread;
    int _nextId = 1;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public McpTestClient(McpServerBase server)
    {
        _serverInput  = new BlockingPipeReader();
        _serverOutput = new BlockingPipeWriter();

        _serverThread = new Thread(() => server.Run(_serverInput, _serverOutput))
        {
            IsBackground = true,
            Name = $"McpServer-{server.GetType().Name}",
        };
        _serverThread.Start();
    }

    // ── client helpers ────────────────────────────────────────────────────────

    public JsonObject Initialize()
    {
        var id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method = "initialize", @params = new
        {
            protocolVersion = "2024-11-05",
            capabilities    = new { },
            clientInfo      = new { name = "TestClient", version = "1.0" },
        }});
        var resp = ReadResponse(3000);
        Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
        return resp;
    }

    public JsonObject ToolsList()
    {
        var id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method = "tools/list" });
        return ReadResponse(3000);
    }

    public JsonObject CallTool(string name, object? args = null)
    {
        var id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method = "tools/call", @params = new
        {
            name,
            arguments = args ?? new { },
        }});
        return ReadResponse(30_000);
    }

    public JsonObject GetInstructions() => CallTool("get_instructions");

    /// <summary>Send a tool call, then immediately send a second request on the
    /// same connection. Returns whichever response arrives first.</summary>
    public (JsonObject first, JsonObject second) RaceRequests(
        string tool1, object? args1,
        string tool2, object? args2,
        int timeoutMs = 15_000)
    {
        var id1 = _nextId++;
        var id2 = _nextId++;
        Send(new { jsonrpc = "2.0", id = id1, method = "tools/call", @params = new { name = tool1, arguments = args1 ?? new { } } });
        Send(new { jsonrpc = "2.0", id = id2, method = "tools/call", @params = new { name = tool2, arguments = args2 ?? new { } } });
        var r1 = ReadResponse(timeoutMs);
        var r2 = ReadResponse(timeoutMs);
        return (r1, r2);
    }

    public void SendRaw(string json) => _serverInput.Enqueue(json);

    public void CloseInput() => _serverInput.Close();

    // ── low-level ─────────────────────────────────────────────────────────────

    void Send(object msg) =>
        _serverInput.Enqueue(JsonSerializer.Serialize(msg, Json));

    public JsonObject ReadResponse(int timeoutMs = 5000)
    {
        var line = _serverOutput.Dequeue(timeoutMs)
            ?? throw new TimeoutException($"No MCP response within {timeoutMs}ms");
        return JsonSerializer.Deserialize<JsonObject>(line, Json)
            ?? throw new InvalidOperationException($"Could not parse response: {line}");
    }

    public bool TryReadResponse(int timeoutMs, out JsonObject? response)
    {
        var line = _serverOutput.Dequeue(timeoutMs);
        if (line is null) { response = null; return false; }
        response = JsonSerializer.Deserialize<JsonObject>(line, Json);
        return response is not null;
    }

    public void Dispose()
    {
        _serverInput.Close();
        _serverThread.Join(3000);
    }

    // ── static JSON helpers ───────────────────────────────────────────────────

    public static string? GetResultText(JsonObject resp)
    {
        if (resp["result"] is JsonObject result &&
            result["content"] is JsonArray arr &&
            arr.Count > 0 &&
            arr[0] is JsonObject first &&
            first["text"] is JsonValue tv)
            return tv.GetValue<string>();
        return null;
    }

    public static bool IsError(JsonObject resp) =>
        resp.ContainsKey("error");

    public static bool IsToolError(JsonObject resp)
    {
        if (resp["result"] is JsonObject r &&
            r["isError"] is JsonValue v)
            return v.TryGetValue<bool>(out var b) && b;
        return false;
    }
}

// ── transport primitives ─────────────────────────────────────────────────────

sealed class BlockingPipeReader : TextReader
{
    readonly BlockingCollection<string?> _q = new(boundedCapacity: 256);

    public void Enqueue(string line) => _q.Add(line);
    public new void Close() { try { _q.Add(null); } catch (InvalidOperationException) { } }

    public override string? ReadLine()
    {
        try { return _q.Take(); }
        catch (InvalidOperationException) { return null; }
    }
}

sealed class BlockingPipeWriter : TextWriter
{
    readonly BlockingCollection<string> _q = new(boundedCapacity: 256);

    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value) { if (value is not null) _q.Add(value); }
    public override void Flush() { }

    public string? Dequeue(int timeoutMs) =>
        _q.TryTake(out var line, timeoutMs) ? line : null;
}
