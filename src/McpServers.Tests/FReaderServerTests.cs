using FReader;

namespace McpServers.Tests;

public sealed class FReaderServerTests
{
    // Use a file we know exists — the FReaderServer source itself.
    static readonly string KnownFile = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "FReader", "FReaderServer.cs"));

    static McpTestClient Start()
    {
        var c = new McpTestClient(new FReaderServer());
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
        Assert.Contains("FReader", text);
        Assert.Contains("summarize", text);
        Assert.Contains("read_function", text);
        Assert.Contains("_meta", text);      // commit 7: _meta section
    }

    [Fact]
    public void ToolsList_IncludesExpectedTools()
    {
        using var c = Start();
        c.Initialize(); // already called by Start() but tools/list needs explicit call
        var resp = c.ToolsList();
        var tools = (resp["result"] as System.Text.Json.Nodes.JsonObject)?["tools"]
            as System.Text.Json.Nodes.JsonArray;
        Assert.NotNull(tools);
        var names = tools
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(t => t["name"]?.GetValue<string>())
            .ToHashSet();
        Assert.Contains("read",          names);
        Assert.Contains("summarize",     names);
        Assert.Contains("read_function", names);
        Assert.Contains("grep_in_file",  names);
        Assert.Contains("search_function", names);
        Assert.Contains("get_instructions", names);
    }

    [Fact]
    public void Info_KnownCsFile_ReturnsFileMetadata()
    {
        if (!File.Exists(KnownFile)) return; // skip if run outside repo

        using var c = Start();
        var resp = c.CallTool("info", new { path = KnownFile, timeoutMs = 10_000 });

        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("FReaderServer", text);
    }

    [Fact]
    public void Read_KnownCsFile_ReturnsLines()
    {
        if (!File.Exists(KnownFile)) return;

        using var c = Start();
        var resp = c.CallTool("read", new
        {
            path      = KnownFile,
            lineStart = 1,
            lineEnd   = 10,
            timeoutMs = 10_000,
        });

        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("using", text);
    }

    [Fact]
    public void ListFunctions_KnownCsFile_ReturnsMethodNames()
    {
        if (!File.Exists(KnownFile)) return;

        using var c = Start();
        var resp = c.CallTool("list_functions", new { path = KnownFile, timeoutMs = 15_000 });

        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("GetInstructions", text);
    }

    [Fact]
    public void Read_NonExistentFile_ReturnsToolError()
    {
        using var c = Start();
        var resp = c.CallTool("read", new
        {
            path      = "/this/path/does/not/exist/file.cs",
            timeoutMs = 5_000,
        });

        Assert.False(McpTestClient.IsError(resp));
        Assert.True(McpTestClient.IsToolError(resp));
    }

    [Fact]
    public void GrepInFile_KnownCsFile_FindsPattern()
    {
        if (!File.Exists(KnownFile)) return;

        using var c = Start();
        var resp = c.CallTool("grep_in_file", new
        {
            path      = KnownFile,
            pattern   = "GetInstructions",
            timeoutMs = 10_000,
        });

        Assert.False(McpTestClient.IsError(resp));
        var text = McpTestClient.GetResultText(resp);
        Assert.NotNull(text);
        Assert.Contains("GetInstructions", text);
    }
}
