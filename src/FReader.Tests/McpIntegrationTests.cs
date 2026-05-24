using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FReader.Tests;

public class McpIntegrationTests : IDisposable
{
    readonly Process _proc;
    readonly StreamWriter _stdin;
    readonly StreamReader _stdout;
    int _id;

    public McpIntegrationTests()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "FReader",
            "bin", "Release", "net9.0", "FReader.dll");
        if (!File.Exists(dll))
        {
            dll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "FReader",
                "bin", "Debug", "net9.0", "FReader.dll");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dll}\" --mcp",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _proc = Process.Start(psi)!;
        _stdin = _proc.StandardInput;
        _stdout = _proc.StandardOutput;
        Initialize();
    }

    void Initialize()
    {
        Send("initialize", null);
        Receive();
        Send("notifications/initialized", null);
    }

    string Send(string method, object? args, bool isNotification = false)
    {
        _id++;
        var req = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = isNotification ? null : _id,
            ["method"] = method,
            ["params"] = args ?? new { }
        };
        if (isNotification) req.Remove("id");
        var json = JsonSerializer.Serialize(req, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        _stdin.WriteLine(json);
        _stdin.Flush();
        return json;
    }

    JsonElement Receive()
    {
        var line = _stdout.ReadLine()!;
        return JsonSerializer.Deserialize<JsonElement>(line);
    }

    string CallTool(string tool, object? args = null)
    {
        Send("tools/call", new { name = tool, arguments = args ?? new { } });
        var resp = Receive();
        if (resp.TryGetProperty("error", out var error))
            return $"(error) {{code:{error.GetProperty("code")},message:{error.GetProperty("message")}}}";
        return resp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestFixtures", "SampleClass.cs");

    [Fact]
    public void ToolsList_ReturnsAllTools()
    {
        Send("tools/list", new { });
        var resp = Receive();
        var tools = resp.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(12, tools.Count);
        var names = tools.Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("read", names);
        Assert.Contains("read_function", names);
        Assert.Contains("read_functions", names);
        Assert.Contains("summarize", names);
        Assert.Contains("grep_in_file", names);
        Assert.Contains("search_function", names);
        Assert.Contains("record_benchmark", names);
        Assert.Contains("list_functions", names);
        Assert.Contains("extract_function", names);
        Assert.Contains("info", names);
        Assert.Contains("get_instructions", names);
    }

    [Fact]
    public void GetInstructions_ReturnsHelp()
    {
        var text = CallTool("get_instructions");
        Assert.Contains("record_benchmark", text);
        Assert.Contains("search_function", text);
    }

    [Fact]
    public void Read_AllLines_ReturnsContent()
    {
        var text = CallTool("read", new { path = FixturePath, timeoutMs = 30000 });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_r", out _));
        Assert.True(data.TryGetProperty("_h", out var h));
        Assert.Equal(2, h.GetArrayLength()); // ["line", "text"]
    }

    [Fact]
    public void Read_InvalidRange_ReturnsErrorFormat()
    {
        var text = CallTool("read", new { path = FixturePath, lineStart = 50, lineEnd = 10, timeoutMs = 30000 });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("error", out _));
        Assert.True(data.TryGetProperty("error_code", out var code));
        Assert.Equal("INVALID_RANGE", code.GetString());
    }

    [Fact]
    public void ListFunctions_ReturnsColumnar()
    {
        var text = CallTool("list_functions", new { path = FixturePath, timeoutMs = 30000 });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_h", out var h));
        Assert.True(data.TryGetProperty("_r", out var r));
        Assert.Equal(4, h.EnumerateArray().Count());
    }

    [Fact]
    public void ExtractFunction_FindsFunction()
    {
        var text = CallTool("extract_function", new { path = FixturePath, name = "ProcessAsync", timeoutMs = 30000 });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_name", out _));
        Assert.True(data.TryGetProperty("_line_start", out _));
    }

    [Fact]
    public void ExtractFunction_MultipleOverloads_ReturnsAll()
    {
        var text = CallTool("extract_function", new { path = FixturePath, name = "ComputeResult", timeoutMs = 30000 });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_overload_count", out var count));
        Assert.Equal(2, count.GetInt32());
        Assert.True(data.TryGetProperty("_overloads", out var overloads));
        Assert.Equal(2, overloads.GetArrayLength());
        Assert.True(data.TryGetProperty("_hint", out _));
    }

    [Fact]
    public void ExtractFunction_NotFound_ReturnsErrorCode()
    {
        var text = CallTool("extract_function", new { path = FixturePath, name = "Z_NONEXISTENT", timeoutMs = 30000 });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("error_code", out _));
    }

    [Fact]
    public void ReadFunction_ReturnsBody()
    {
        var text = CallTool("read_function", new { path = FixturePath, name = "ProcessAsync", timeoutMs = 30000 });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_r", out _));
        Assert.True(data.TryGetProperty("_name", out _));
    }

    [Fact]
    public void ReadFunction_WithParameterTypes_Disambiguates()
    {
        var text = CallTool("read_function", new
        {
            path = FixturePath,
            name = "ComputeResult",
            parameterTypes = new[] { "int" },
            timeoutMs = 30000
        });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_r", out _));
    }

    [Fact]
    public void ReadFunction_WrongParamTypes_ReturnsOverloads()
    {
        var text = CallTool("read_function", new
        {
            path = FixturePath,
            name = "ComputeResult",
            parameterTypes = new[] { "bool" },
            timeoutMs = 30000
        });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_available_overloads", out _));
    }

    [Fact]
    public void ReadFunctions_Batch_ReturnsMultiple()
    {
        var text = CallTool("read_functions", new
        {
            path = FixturePath,
            names = new[] { "ComputeResult", "ProcessAsync" },
            timeoutMs = 30000
        });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_results", out var results));
        Assert.True(results.GetArrayLength() >= 1);
    }

    [Fact]
    public void ReadFunctions_MaxChars_Exceeded()
    {
        var text = CallTool("read_functions", new
        {
            path = FixturePath,
            names = new[] { "ComputeResult", "ProcessAsync" },
            maxChars = 10,
            timeoutMs = 30000
        });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("error_code", out var code));
        Assert.Equal("MAX_CHARS_EXCEEDED", code.GetString());
    }

    [Fact]
    public void Read_WithAliases_ReturnsAliasesField()
    {
        var text = CallTool("read", new
        {
            path = FixturePath,
            lineStart = 1,
            lineEnd = 80,
            aliases = true,
            timeoutMs = 30000
        });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_aliases", out _));
        Assert.True(data.TryGetProperty("_alias_warning", out _));
    }

    [Fact]
    public void Summarize_ReturnsText()
    {
        var text = CallTool("summarize", new { path = FixturePath, timeoutMs = 30000 });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_text", out _));
    }

    [Fact]
    public void GrepInFile_FindsMatch()
    {
        var text = CallTool("grep_in_file", new
        {
            path = FixturePath,
            pattern = "ComputeResult",
            timeoutMs = 30000
        });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_matches", out var matches));
        Assert.True(matches.GetInt32() >= 1);
    }

    [Fact]
    public void SearchFunction_FindsFunction()
    {
        var text = CallTool("search_function", new
        {
            rootPath = Path.GetDirectoryName(FixturePath),
            name = "ComputeResult",
            timeoutMs = 30000
        });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_total_matches", out var total));
        Assert.True(total.GetInt32() >= 1);
    }

    [Fact]
    public void Info_FileNotFound_ReturnsErrorCode()
    {
        var text = CallTool("info", new { path = "z_nonexistent.cs", timeoutMs = 30000 });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("error_code", out var code));
        Assert.Equal("FILE_NOT_FOUND", code.GetString());
    }

    [Fact]
    public void GrepInFile_NotFound_ReturnsErrorCode()
    {
        var text = CallTool("grep_in_file", new
        {
            path = FixturePath,
            pattern = "ZZ_NONEXISTENT",
            timeoutMs = 30000
        });
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(data.TryGetProperty("_matches", out var matches));
        Assert.Equal(0, matches.GetInt32());
    }

    public void Dispose()
    {
        try { _proc.Kill(); _proc.Dispose(); } catch { }
    }
}
