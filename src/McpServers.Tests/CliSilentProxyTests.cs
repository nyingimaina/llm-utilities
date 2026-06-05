using System.Text.Json.Nodes;
using CliSilentProxy;

namespace McpServers.Tests;

public sealed class CliSilentProxyTests
{
    static McpTestClient Start()
    {
        var c = new McpTestClient(new CliSilentProxyServer());
        c.Initialize();
        return c;
    }

    // ── get_instructions ──────────────────────────────────────────────────────

    [Fact]
    public void GetInstructions_ReturnsInstructionArray()
    {
        using var c = Start();
        var resp = c.GetInstructions();
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        // The result is a JSON-encoded object with _h array.
        Assert.Contains("_h", text);
        Assert.Contains("run", text);
        Assert.Contains("get_log", text);
    }

    [Fact]
    public void GetInstructions_ContainsAnnounceField()
    {
        using var c = Start();
        var resp = c.GetInstructions();
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("_announce", text);
    }

    // ── run tool ─────────────────────────────────────────────────────────────

    [Fact]
    public void Run_EchoCommand_ReturnsOkStatus()
    {
        using var c = Start();
        var resp = c.CallTool("run", new
        {
            command = "cmd",
            args    = new[] { "/c", "echo", "hello-from-test" },
            timeoutMs = 10_000,
        });

        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        // Success: _s:"ok"
        Assert.Contains("\"_s\":\"ok\"", text);
        Assert.Contains("\"_exit\":0", text);
    }

    [Fact]
    public void Run_InvalidCommand_ReturnsMcpError()
    {
        using var c = Start();
        var resp = c.CallTool("run", new
        {
            command   = "this_command_does_not_exist_xyz123",
            timeoutMs = 5_000,
        });

        // Should return a tool error (McpErrorException), not a JSON-RPC protocol error.
        Assert.False(McpTestClient.IsError(resp), "Should be a tool-level error, not protocol error");
        Assert.True(McpTestClient.IsToolError(resp) || McpTestClient.GetResultText(resp)?.Contains("Failed to start") == true);
    }

    [Fact]
    public void Run_WithoutTimeoutMs_ReturnsMcpError_TimeoutRequired()
    {
        using var c = Start();
        var resp = c.CallTool("run", new { command = "echo" });

        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("TIMEOUT_REQUIRED", text);
    }

    [Fact]
    public void Run_Timeout_ReturnsTimeoutStatus()
    {
        using var c = Start();
        // ping -n 10 takes ~9s; timeout after 500ms.
        var resp = c.CallTool("run", new
        {
            command   = "ping",
            args      = new[] { "-n", "10", "127.0.0.1" },
            timeoutMs = 500,
        });

        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("timeout", text);
    }

    [Fact]
    public void GetLog_NonExistentId_ReturnsToolError()
    {
        using var c = Start();
        var resp = c.CallTool("get_log", new { id = 999_999, timeoutMs = 5_000 });

        Assert.False(McpTestClient.IsError(resp));
        Assert.True(McpTestClient.IsToolError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_WithCaptureOutput_ReturnsStdout()
    {
        using var c = Start();
        var resp = c.CallTool("run", new
        {
            command = "cmd",
            args    = new[] { "/c", "echo", "hello-from-capture" },
            timeoutMs = 10_000,
            captureOutput = true,
        });

        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("_stdout", text);
        Assert.Contains("hello-from-capture", text);
    }

    // ── concurrent safety ─────────────────────────────────────────────────────

    [Fact]
    public void MultipleSequentialCalls_AllSucceed()
    {
        using var c = Start();
        for (int i = 0; i < 5; i++)
        {
            var resp = c.CallTool("run", new
            {
                command   = "cmd",
                args      = new[] { "/c", "echo", $"iteration-{i}" },
                timeoutMs = 10_000,
            });
            Assert.False(McpTestClient.IsError(resp), $"Iteration {i} should succeed");
            var text = McpTestClient.GetResultText(resp);
            Assert.Contains("\"_s\":\"ok\"", text!);
        }
    }
}
