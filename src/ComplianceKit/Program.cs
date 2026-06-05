using ComplianceKit;
using LLMUtilities.Commons;
using Serilog;

string? configPath = null;
string? logDir = null;
string? standardsPath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--mcp": break;
        case "--config-path" when i + 1 < args.Length: configPath = args[++i]; break;
        case "--log-dir"     when i + 1 < args.Length: logDir     = args[++i]; break;
        case "--standards-path" when i + 1 < args.Length: standardsPath = args[++i]; break;
        default:
            Console.Error.WriteLine("Usage: ComplianceKit.exe --mcp [--config-path <path>] [--log-dir <dir>] [--standards-path <path>]");
            return 1;
    }
}

logDir ??= ConfigPaths.LogDir("ComplianceKit");
Directory.CreateDirectory(logDir);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(logDir, "ComplianceKit.log"),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    configPath ??= ConfigPaths.ConfigFile("ComplianceKit");
    new ComplianceKitServer(configPath, standardsPath).Run(Console.In, Console.Out);
}
catch (Exception ex) { Log.Fatal(ex, "Unhandled exception"); }
finally { Log.CloseAndFlush(); }
return 0;
