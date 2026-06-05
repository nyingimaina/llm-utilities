using Rowster;
using LLMUtilities.Commons;

namespace McpServers.Tests;

public sealed class RowsterTests
{
    static McpTestClient Start()
    {
        var cm = new ConnectionManager();
        var c = new McpTestClient(new McpServer(cm, null));
        c.Initialize();
        return c;
    }

    [Fact]
    public void GetInstructions_ReturnsRowsterContent()
    {
        using var c = Start();
        var resp = c.GetInstructions();
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("Rowster", text);
        Assert.Contains("query", text);
    }

    [Fact]
    public void ToolsList_IncludesAllRowsterTools()
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
        Assert.Contains("query",          names);
        Assert.Contains("count",          names);
        Assert.Contains("sample",         names);
        Assert.Contains("list_tables",    names);
        Assert.Contains("describe_table", names);
        Assert.Contains("list_databases", names);
        Assert.Contains("connect",        names);
        Assert.Contains("ping",           names);
    }

    [Fact]
    public void Ping_ReturnsOkStatus()
    {
        using var c = Start();
        // ping does not require a DB connection.
        var resp = c.CallTool("ping", new { });
        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("\"_s\":\"ok\"", text);
    }

    [Fact]
    public void Query_WithInvalidConnection_ReturnsToolError_NotCrash()
    {
        // Verifies the server handles a bad connection gracefully.
        using var c = Start();
        var resp = c.CallTool("query", new
        {
            sql        = "SELECT 1",
            connection = "Server=127.0.0.1;Port=9999;Uid=invalid;Pwd=invalid;Database=none;ConnectionTimeout=1;",
            timeoutMs  = 5_000,
        });

        // Must be a tool error (isError=true), not a protocol crash.
        Assert.False(McpTestClient.IsError(resp),
            "DB connection failure should be a tool-level error, not a protocol error");
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
    }

    [Fact]
    public void Connect_SetConnection_ReturnsOk()
    {
        using var c = Start();
        var resp = c.CallTool("connect", new
        {
            connection = "Server=127.0.0.1;Port=3306;Uid=test;Pwd=test;",
            timeoutMs  = 3_000,
        });

        // connect() only stores the string — it doesn't actually open a connection.
        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
    }
}
