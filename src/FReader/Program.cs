using System.Text.Json;
using LLMUtilities.Commons;
using FReader;
using Serilog;

var path = "";
int? lineStart = null, lineEnd = null, truncate = null;
var infoOnly = false;
var pretty = false;
var mcpMode = false;
var listFunctions = false;
var extractFunction = "";
string? configPath = null;
string? logDir = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--path"     when i + 1 < args.Length: path      = args[++i]; break;
        case "--line-start" when i + 1 < args.Length: lineStart = int.Parse(args[++i]); break;
        case "--line-end"   when i + 1 < args.Length: lineEnd   = int.Parse(args[++i]); break;
        case "--truncate"   when i + 1 < args.Length: truncate  = int.Parse(args[++i]); break;
        case "--info":      infoOnly  = true; break;
        case "--mcp":       mcpMode   = true; break;
        case "--pretty":    pretty    = true; break;
        case "--list-functions": listFunctions = true; break;
        case "--extract-function" when i + 1 < args.Length: extractFunction = args[++i]; break;
        case "--config-path" when i + 1 < args.Length: configPath = args[++i]; break;
        case "--log-dir" when i + 1 < args.Length: logDir = args[++i]; break;
    }
}

if (mcpMode)
{
    logDir ??= ConfigPaths.LogDir("FReader");
    Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDir, "FReader.log"),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

    try
    {
        configPath ??= ConfigPaths.ConfigFile("FReader");
        using var server = new FReaderServer(configPath);
        server.Run(Console.In, Console.Out);
    }
    catch (Exception ex) { Log.Fatal(ex, "Unhandled exception"); }
    finally { Log.CloseAndFlush(); }
    return 0;
}

// ── Legacy CLI mode ────────────────────────────────────────────────────────

var opts = new JsonSerializerOptions { WriteIndented = pretty };
void Out(object data) => Console.WriteLine(JsonSerializer.Serialize(data, opts));
void Err(object data) => Console.Error.WriteLine(JsonSerializer.Serialize(data, opts));

if (string.IsNullOrEmpty(path)) { Err(new { Error = "--path is required" }); return 1; }

if (listFunctions)
{
    var funcs = ProviderFactory.ListFunctions(path);
    Out(funcs.Select(f => new { f.Name, f.Signature, f.LineStart }));
    return 0;
}

if (extractFunction != "")
{
    var func = ProviderFactory.GetFunction(path, extractFunction);
    if (func is null) { Err(new { Error = $"Function '{extractFunction}' not found" }); return 1; }
    Out(func);
    return 0;
}

if (infoOnly)
{
    Out(LineEngine.GetInfo(path));
}
else
{
    var result = LineEngine.ReadLines(path, lineStart, lineEnd, truncate);
    if (result.Error is not null)
        Err(result.Error!);
    else
        Out(result.Data!);
}

return 0;
