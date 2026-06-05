using System.Text.Json;
using System.Text.Json.Nodes;
using LLMUtilities.Commons;
using CliSilentProxy;

namespace McpServers.Tests;

/// <summary>
/// Tests for McpServerBase protocol correctness — handshake, dispatch, error paths.
/// Uses CliSilentProxyServer as the concrete implementation since it has no
/// external dependencies (no DB, no UI) and its tool calls are simple.
/// </summary>
public sealed class McpServerBaseTests
{
    static McpTestClient StartClient() => new(new CliSilentProxyServer());

    // ── MCP handshake ─────────────────────────────────────────────────────────

    [Fact]
    public void Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        using var client = StartClient();
        var resp = client.Initialize();

        var result = resp["result"] as JsonObject;
        Assert.NotNull(result);
        Assert.Equal("2024-11-05", result["protocolVersion"]?.GetValue<string>());
        var info = result["serverInfo"] as JsonObject;
        Assert.NotNull(info);
        Assert.Equal("CliSilentProxy", info["name"]?.GetValue<string>());
    }

    [Fact]
    public void ToolsList_ReturnsGetInstructionsPlusServerTools()
    {
        using var client = StartClient();
        client.Initialize();
        var resp = client.ToolsList();

        var tools = (resp["result"] as JsonObject)?["tools"] as JsonArray;
        Assert.NotNull(tools);
        var names = tools.OfType<JsonObject>().Select(t => t["name"]?.GetValue<string>()).ToList();
        Assert.Contains("get_instructions", names);
        Assert.Contains("run", names);
        Assert.Contains("get_log", names);
    }

    [Fact]
    public void GetInstructions_BeforeInitializedNotification_StillResponds()
    {
        // Commit 1 fix: get_instructions must bypass the _initialized gate.
        using var client = new McpTestClient(new CliSilentProxyServer());

        // Send initialize but deliberately skip notifications/initialized.
        var id = 1;
        client.SendRaw(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id, method = "initialize",
            @params = new { protocolVersion = "2024-11-05", capabilities = new { } }
        }));
        var initResp = client.ReadResponse(3000);
        Assert.True(initResp.ContainsKey("result"), "initialize should succeed");

        // Call get_instructions without sending notifications/initialized.
        client.SendRaw(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = "get_instructions", arguments = new { } }
        }));
        var instrResp = client.ReadResponse(5000);

        // Must be a valid result, not a "Not initialized" error.
        Assert.False(McpTestClient.IsError(instrResp),
            $"get_instructions should not return error before init. Got: {instrResp}");
        var text = McpTestClient.GetResultText(instrResp);
        Assert.NotNull(text);
        Assert.Contains("CliSilentProxy", text);
    }

    [Fact]
    public void OtherTools_BeforeInitializedNotification_ReturnNotInitialized()
    {
        using var client = new McpTestClient(new CliSilentProxyServer());

        // Initialize but skip notifications/initialized.
        client.SendRaw(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2024-11-05", capabilities = new { } }
        }));
        client.ReadResponse(3000);

        // tools/call run without initialization should be gated.
        client.SendRaw(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = "run", arguments = new { command = "echo", timeoutMs = 5000 } }
        }));
        var resp = client.ReadResponse(3000);
        Assert.True(McpTestClient.IsError(resp),
            $"'run' before init should return error. Got: {resp}");
    }

    [Fact]
    public void InvalidJson_ReturnsParseError_ServerRemainsAlive()
    {
        using var client = StartClient();
        client.Initialize();

        client.SendRaw("this is not json {{{{");
        var errResp = client.ReadResponse(3000);
        Assert.True(McpTestClient.IsError(errResp), "invalid JSON should produce error");
        var err = errResp["error"] as JsonObject;
        Assert.Equal(-32700, err?["code"]?.GetValue<int>());

        // Server must still be alive and process the next request.
        var instr = client.GetInstructions();
        Assert.False(McpTestClient.IsError(instr), "server should still respond after parse error");
    }

    [Fact]
    public void UnknownMethod_ReturnsMethodNotFound()
    {
        using var client = StartClient();
        client.Initialize();

        client.SendRaw(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 99, method = "nonexistent/method"
        }));
        var resp = client.ReadResponse(3000);
        Assert.True(McpTestClient.IsError(resp));
        var err = resp["error"] as JsonObject;
        Assert.Equal(-32601, err?["code"]?.GetValue<int>());
    }

    // ── Async dispatch (commit 2 fix) ─────────────────────────────────────────

    [Fact]
    public void GetInstructions_RespondsImmediately_WhileSlowToolRunning()
    {
        // Verifies that the async dispatch allows get_instructions to respond
        // while a long-running tool call is in the worker queue.
        using var client = StartClient();
        client.Initialize();

        // Send a tool call that will run for ~2 seconds (ping -n 3 on Windows = 2s).
        var id1 = 1;
        client.SendRaw(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = id1, method = "tools/call",
            @params = new { name = "run", arguments = new
            {
                command = "ping",
                args    = new[] { "-n", "3", "127.0.0.1" },
                timeoutMs = 10_000,
            }}
        }));

        // Immediately queue get_instructions — should respond BEFORE ping completes.
        var id2 = 2;
        client.SendRaw(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = id2, method = "tools/call",
            @params = new { name = "get_instructions", arguments = new { } }
        }));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = client.ReadResponse(15_000);
        var firstElapsed = sw.ElapsedMilliseconds;

        // get_instructions should arrive first (within 1 second).
        // The ping takes ~2 seconds; if dispatch is async, instructions arrive <500ms.
        var firstText = McpTestClient.GetResultText(first);
        Assert.NotNull(firstText);
        Assert.Contains("CliSilentProxy", firstText);
        Assert.True(firstElapsed < 1500,
            $"get_instructions took {firstElapsed}ms — should arrive before ping (~2s) finishes");

        // The second response (ping result) must still arrive.
        var second = client.ReadResponse(15_000);
        Assert.False(McpTestClient.IsError(second), $"ping run should succeed: {second}");
    }

    [Fact]
    public void GetInstructions_ContentContains_ExpectedSections()
    {
        using var client = StartClient();
        client.Initialize();
        var resp = client.GetInstructions();

        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("HARNESS", text);
        Assert.Contains("get_log", text);    // CliSilentProxy instructions reference get_log
        Assert.Contains("_meta", text);      // commit 7: _meta section must be present
        Assert.Contains("canNotify", text);
    }
}
