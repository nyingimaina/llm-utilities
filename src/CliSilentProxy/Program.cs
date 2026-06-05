using LLMUtilities.Commons;
using CliSilentProxy;
using Serilog;

string? configPath = null;
string? logDir = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--mcp": break;
        case "--config-path" when i + 1 < args.Length: configPath = args[++i]; break;
        case "--log-dir" when i + 1 < args.Length: logDir = args[++i]; break;
        default:
            Console.Error.WriteLine("Usage: CliSilentProxy --mcp [--config-path <path>] [--log-dir <dir>]");
            return 1;
    }
}

logDir ??= ConfigPaths.LogDir("CliSilentProxy");
Directory.CreateDirectory(logDir);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(logDir, "CliSilentProxy.log"),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    configPath ??= ConfigPaths.ConfigFile("CliSilentProxy");
    var server = new CliSilentProxyServer(configPath);
    server.Run(Console.In, Console.Out);
}
catch (Exception ex) { Log.Fatal(ex, "Unhandled exception"); }
finally { Log.CloseAndFlush(); }
return 0;
