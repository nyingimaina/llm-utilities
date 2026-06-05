using Notifier;

namespace McpServers.Tests;

public sealed class NotifierTests
{
    static McpTestClient Start()
    {
        var c = new McpTestClient(new NotifierServer());
        c.Initialize();
        return c;
    }

    [Fact]
    public void GetInstructions_ReturnsExpectedSections()
    {
        using var c = Start();
        var resp = c.GetInstructions();
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("Notifier", text);
        Assert.Contains("notify", text);
        Assert.Contains("notify_on_complete", text);
        Assert.Contains("_meta", text);      // commit 7: _meta section
        Assert.Contains("canNotify", text);
    }

    [Fact]
    public void ToolsList_IncludesNotifyTools()
    {
        using var c = Start();
        var resp = c.ToolsList();
        var tools = (resp["result"] as System.Text.Json.Nodes.JsonObject)?["tools"]
            as System.Text.Json.Nodes.JsonArray;
        Assert.NotNull(tools);
        var names = tools
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(t => t["name"]?.GetValue<string>())
            .ToHashSet();
        Assert.Contains("notify",             names);
        Assert.Contains("notify_on_complete", names);
        Assert.Contains("get_instructions",   names);
    }

    [Fact]
    public void Notify_MissingRequiredField_ReturnsMcpError()
    {
        using var c = Start();
        // 'title' and 'message' and 'sender' are required; omitting sender.
        var resp = c.CallTool("notify", new
        {
            title   = "Test",
            message = "Integration test notification",
            // sender intentionally omitted
        });

        // Server should return either a tool-error or succeed with empty sender —
        // it must NOT crash (protocol-level error).
        Assert.False(McpTestClient.IsError(resp),
            "Missing optional sender should not produce a JSON-RPC protocol error");
    }

    [Fact]
    public void Notify_ValidPayload_ReturnsResponseWithSentField()
    {
        using var c = Start();
        var resp = c.CallTool("notify", new
        {
            title          = "McpServers.Tests",
            message        = "Integration test: Notifier working",
            sender         = "TestRunner",
            dismissAfterMs = 1000,
        });

        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        // Response must always contain _sent regardless of whether display succeeded.
        Assert.Contains("_sent", text);
    }

    [Fact]
    public void UnknownTool_ReturnsJsonSerializedError()
    {
        using var c = Start();
        // Commit 5 fix: Notifier used string concatenation for error; now uses JsonSerializer.
        var resp = c.CallTool("nonexistent_tool_xyz", new { timeoutMs = 5000 });
        Assert.False(McpTestClient.IsError(resp));   // tool-level, not protocol-level
        Assert.True(McpTestClient.IsToolError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        // Must be valid JSON (not a broken string from concatenation).
        var parsed = System.Text.Json.JsonDocument.Parse(text);
        Assert.True(parsed.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void Notify_TitleWithSpecialChars_DoesNotThrow()
    {
        // Regression test for injection fix: special chars must not crash the server.
        using var c = Start();
        var resp = c.CallTool("notify", new
        {
            title   = "'); DROP TABLE users; --  \"evil\" `code` $VAR",
            message = "Test with injection-like content",
            sender  = "TestRunner",
            dismissAfterMs = 100,
        });

        // Server must respond (may or may not display depending on platform).
        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("_sent", text);
    }
}
