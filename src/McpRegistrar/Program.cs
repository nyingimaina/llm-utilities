using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

var logDir = Path.Combine(Path.GetTempPath(), "LLMUtilities");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(logDir, "McpRegistrar.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Run(args);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception");
}
finally
{
    Log.CloseAndFlush();
}

if (!Console.IsInputRedirected && args.Length == 0)
{
    Console.Write("Press any key to exit...");
    try { Console.ReadKey(true); } catch { }
}

static void Run(string[] args)
{
    var regRowster = false;
    var regFReader = false;
    var regCliSilentProxy = false;
    var regFWriter = false;
    var regContractGenerator = false;
    var regCodeNavigator = false;
    var regNotifier = false;
    var regClaude = true;
    var regGemini = false;
    var regOpenCode = false;
    var regCursor = false;
    var regWindsurf = false;
    var regZed = false;
    var uninstall = false;
    string? rowsterPath = null;
    string? freaderPath = null;
    string? cliSilentProxyPath = null;
    string? fwriterPath = null;
    string? contractGeneratorPath = null;
    string? codeNavigatorPath = null;
    string? notifierPath = null;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--register-rowster": regRowster = true; break;
            case "--register-freader": regFReader = true; break;
            case "--register-clisilentproxy": regCliSilentProxy = true; break;
            case "--register-fwriter": regFWriter = true; break;
            case "--register-contractgenerator": regContractGenerator = true; break;
            case "--register-codenavigator": regCodeNavigator = true; break;
            case "--register-notifier": regNotifier = true; break;

            case "--skip-claude": regClaude = false; break;
            case "--register-gemini": regGemini = true; break;
            case "--register-opencode": regOpenCode = true; break;
            case "--register-cursor": regCursor = true; break;
            case "--register-windsurf": regWindsurf = true; break;
            case "--register-zed": regZed = true; break;
            case "--rowster-path"       when i + 1 < args.Length: rowsterPath       = args[++i]; break;
            case "--freader-path"       when i + 1 < args.Length: freaderPath       = args[++i]; break;
            case "--clisilentproxy-path" when i + 1 < args.Length: cliSilentProxyPath = args[++i]; break;
            case "--fwriter-path"       when i + 1 < args.Length: fwriterPath       = args[++i]; break;
            case "--contractgenerator-path" when i + 1 < args.Length: contractGeneratorPath = args[++i]; break;
            case "--codenavigator-path"     when i + 1 < args.Length: codeNavigatorPath     = args[++i]; break;
            case "--notifier-path"          when i + 1 < args.Length: notifierPath          = args[++i]; break;
            case "--uninstall": uninstall = true; break;
            default:
                Log.Warning("Unknown argument: {Arg}", args[i]);
                break;
        }
    }

    if (uninstall && (regRowster || regFReader || regCliSilentProxy || regFWriter || regContractGenerator || regCodeNavigator || regNotifier || regGemini || regOpenCode || regCursor || regWindsurf || regZed))
    {
        Log.Error("Cannot combine --uninstall with --register-* flags");
        Environment.Exit(1);
    }

    if (!uninstall && !regRowster && !regFReader && !regCliSilentProxy && !regFWriter && !regContractGenerator && !regCodeNavigator && !regNotifier && !regGemini && !regOpenCode && !regCursor && !regWindsurf && !regZed)
    {
        Log.Information("Usage: McpRegistrar [--register-rowster --rowster-path <path>] [--register-freader --freader-path <path>] [--register-clisilentproxy --clisilentproxy-path <path>] [--register-fwriter --fwriter-path <path>] [--register-contractgenerator --contractgenerator-path <path>] [--register-codenavigator --codenavigator-path <path>] [--register-notifier --notifier-path <path>] [--skip-claude] [--register-gemini] [--register-opencode] [--register-cursor] [--register-windsurf] [--register-zed]");
        Log.Information("       McpRegistrar --uninstall");
        return;
    }

    var configDir = Environment.GetEnvironmentVariable("USERPROFILE")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ── Helper: modify one config file ───────────────────────────────────

    void ModifyConfig(string configPath, string label)
    {
        var backupPath = configPath + ".llm-backup";

        JsonNode? config;

        if (File.Exists(configPath))
        {
            try
            {
                var text = File.ReadAllText(configPath);
                config = JsonNode.Parse(text);
                if (config is not JsonObject)
                    throw new InvalidOperationException("Root is not a JSON object");
                Log.Information("Read config from {Path}", configPath);
            }
            catch (Exception ex)
            {
                Log.Warning("Config file is corrupt — backing up and recreating: {Error}", ex.Message);
                File.Copy(configPath, backupPath, overwrite: true);
                Log.Information("Backed up corrupt file to {Path}", backupPath);
                config = new JsonObject();
            }
        }
        else
        {
            Log.Information("No {Label} config file found — will create new", label);
            config = new JsonObject();
        }

        var root = (JsonObject)config;
        var modified = false;

        if (uninstall)
        {
            if (!File.Exists(configPath))
            {
                Log.Information("No {Label} config file found — nothing to uninstall", label);
                return;
            }

            if (root.TryGetPropertyValue("mcpServers", out var node) && node is JsonObject ms)
            {
                var removedRowster           = ms.Remove("Rowster");
                var removedFReader           = ms.Remove("FReader");
                var removedCliSilentProxy    = ms.Remove("CliSilentProxy");
                var removedFWriter           = ms.Remove("FWriter");
                var removedContractGenerator = ms.Remove("ContractGenerator");
                var removedCodeNavigator     = ms.Remove("CodeNavigator");
                var removedNotifier          = ms.Remove("Notifier");
                if (removedRowster)           { modified = true; Log.Information("[{Label}] Removed Rowster", label); }
                if (removedFReader)           { modified = true; Log.Information("[{Label}] Removed FReader", label); }
                if (removedCliSilentProxy)    { modified = true; Log.Information("[{Label}] Removed CliSilentProxy", label); }
                if (removedFWriter)           { modified = true; Log.Information("[{Label}] Removed FWriter", label); }
                if (removedContractGenerator) { modified = true; Log.Information("[{Label}] Removed ContractGenerator", label); }
                if (removedCodeNavigator)     { modified = true; Log.Information("[{Label}] Removed CodeNavigator", label); }
                if (removedNotifier)          { modified = true; Log.Information("[{Label}] Removed Notifier", label); }
                if (!removedRowster && !removedFReader && !removedCliSilentProxy && !removedFWriter && !removedContractGenerator && !removedCodeNavigator && !removedNotifier)
                    Log.Information("[{Label}] No MCP server entries found", label);

                if (ms.Count == 0)
                {
                    root.Remove("mcpServers");
                    modified = true;
                    Log.Information("[{Label}] mcpServers is empty — removed key", label);
                }
            }
            else
            {
                Log.Information("[{Label}] No mcpServers key found", label);
            }
        }
        else
        {
            if (!root.TryGetPropertyValue("mcpServers", out var node) || node is not JsonObject ms)
            {
                ms = new JsonObject();
                root["mcpServers"] = ms;
            }

            if (regRowster && rowsterPath != null)
            {
                ms["Rowster"] = new JsonObject
                {
                    ["command"] = rowsterPath,
                    ["args"] = new JsonArray("--mcp"),
                    ["_description"] = "MySQL database query tool — execute SQL, count rows, sample data, describe/list tables, list databases, ping health check"
                };
                modified = true;
                Log.Information("[{Label}] Registered Rowster: {Path}", label, rowsterPath);
            }

            if (regFReader && freaderPath != null)
            {
                ms["FReader"] = new JsonObject
                {
                    ["command"] = freaderPath,
                    ["args"] = new JsonArray("--mcp"),
                    ["_description"] = "Primary file-reading MCP server — handles ALL file reads, function lookups, and text search. Saves 40-60% tokens vs raw read+grep. Prefer over default file tools."
                };
                modified = true;
                Log.Information("[{Label}] Registered FReader: {Path}", label, freaderPath);
            }

            if (regCliSilentProxy && cliSilentProxyPath != null)
            {
                ms["CliSilentProxy"] = new JsonObject
                {
                    ["command"] = cliSilentProxyPath,
                    ["args"] = new JsonArray("--mcp"),
                    ["_description"] = "Shell command runner -- run any executable and suppress verbose output. Success returns status+timing only (saves 90-97% tokens). Failure returns a log tail; full log available on request."
                };
                modified = true;
                Log.Information("[{Label}] Registered CliSilentProxy: {Path}", label, cliSilentProxyPath);
            }

            if (regFWriter && fwriterPath != null)
            {
                ms["FWriter"] = new JsonObject
                {
                    ["command"] = fwriterPath,
                    ["args"] = new JsonArray("--mcp"),
                    ["_description"] = "Validated code editor -- AST-level function body editing with backup/rollback, syntax validation, and file creation for C#."
                };
                modified = true;
                Log.Information("[{Label}] Registered FWriter: {Path}", label, fwriterPath);
            }

            if (regContractGenerator && contractGeneratorPath != null)
            {
                ms["ContractGenerator"] = new JsonObject
                {
                    ["command"] = contractGeneratorPath,
                    ["args"] = new JsonArray("--mcp"),
                    ["_description"] = "C# to TypeScript interface generator -- converts C# data types to TS using Roslyn."
                };
                modified = true;
                Log.Information("[{Label}] Registered ContractGenerator: {Path}", label, contractGeneratorPath);
            }

            if (regCodeNavigator && codeNavigatorPath != null)
            {
                ms["CodeNavigator"] = new JsonObject
                {
                    ["command"] = codeNavigatorPath,
                    ["args"] = new JsonArray("--mcp"),
                    ["_description"] = "Semantic code navigation -- symbols, definition, references, hover, call graph for C# and TS."
                };
                modified = true;
                Log.Information("[{Label}] Registered CodeNavigator: {Path}", label, codeNavigatorPath);
            }

            if (regNotifier && notifierPath != null)
            {
                ms["Notifier"] = new JsonObject
                {
                    ["command"] = notifierPath,
                    ["args"] = new JsonArray("--mcp"),
                    ["_description"] = "Cross-platform desktop notification service with slide-in toast UI, custom sounds, and fire-and-forget command completion alerts."
                };
                modified = true;
                Log.Information("[{Label}] Registered Notifier: {Path}", label, notifierPath);
            }

        }

        if (!modified)
        {
            Log.Information("[{Label}] No changes needed", label);
            return;
        }

        // ── Write with backup → validate → commit/rollback ───────────────────

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        try
        {
            if (File.Exists(configPath))
            {
                File.Copy(configPath, backupPath, overwrite: true);
                Log.Information("[{Label}] Backed up to {Path}", label, backupPath);
            }

            File.WriteAllText(configPath, json);

            try
            {
                var check = File.ReadAllText(configPath);
                var parsed = JsonNode.Parse(check);
                if (parsed == null)
                    throw new InvalidOperationException("Parsed result is null");
                Log.Information("[{Label}] Write validated successfully", label);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Write validation FAILED: {ex.Message}");
            }

            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
                Log.Information("[{Label}] Backup cleaned up", label);
            }

            Log.Information("[{Label}] Config updated successfully", label);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[{Label}] Failed to update config", label);

            if (File.Exists(backupPath))
            {
                Log.Information("[{Label}] Restoring backup...", label);
                File.Copy(backupPath, configPath, overwrite: true);
                File.Delete(backupPath);
                Log.Information("[{Label}] Backup restored — config is safe", label);
            }
        }
    }

    // ── Apply to Claude Code config ──────────────────────────────────────

    if (regClaude)
        ModifyConfig(Path.Combine(configDir, ".claude.json"), "Claude Code");
    else
        Log.Information("[Claude Code] Skipped (user opted out)");

    // ── Apply to Gemini CLI config ───────────────────────────────────────

    if (regGemini || (uninstall && File.Exists(Path.Combine(configDir, ".gemini", "settings.json"))))
    {
        var geminiDir = Path.Combine(configDir, ".gemini");
        Directory.CreateDirectory(geminiDir);
        ModifyConfig(Path.Combine(geminiDir, "settings.json"), "Gemini CLI");
    }

    // ── Apply to OpenCode global config ───────────────────────────────────

    void ModifyOpenCodeConfig(string configPath, string label)
    {
        var backupPath = configPath + ".llm-backup";

        JsonNode? config;
        if (File.Exists(configPath))
        {
            try
            {
                var text = File.ReadAllText(configPath);
                config = JsonNode.Parse(text);
                if (config is not JsonObject)
                    throw new InvalidOperationException("Root is not a JSON object");
                Log.Information("Read OpenCode config from {Path}", configPath);
            }
            catch (Exception ex)
            {
                Log.Warning("OpenCode config is corrupt — backing up and recreating: {Error}", ex.Message);
                File.Copy(configPath, backupPath, overwrite: true);
                Log.Information("Backed up corrupt file to {Path}", backupPath);
                config = new JsonObject();
            }
        }
        else
        {
            Log.Information("No {Label} config found — will create new", label);
            config = new JsonObject { ["$schema"] = "https://opencode.ai/config.json" };
        }

        var root = (JsonObject)config;
        var modified = false;

        if (uninstall)
        {
            if (root.TryGetPropertyValue("mcp", out var node) && node is JsonObject ms)
            {
                var removedRowster           = ms.Remove("Rowster");
                var removedFReader           = ms.Remove("FReader");
                var removedCliSilentProxy    = ms.Remove("CliSilentProxy");
                var removedFWriter           = ms.Remove("FWriter");
                var removedContractGenerator = ms.Remove("ContractGenerator");
                var removedCodeNavigator     = ms.Remove("CodeNavigator");
                var removedNotifier2          = ms.Remove("Notifier");
                if (removedRowster)           { modified = true; Log.Information("[{Label}] Removed Rowster", label); }
                if (removedFReader)           { modified = true; Log.Information("[{Label}] Removed FReader", label); }
                if (removedCliSilentProxy)    { modified = true; Log.Information("[{Label}] Removed CliSilentProxy", label); }
                if (removedFWriter)           { modified = true; Log.Information("[{Label}] Removed FWriter", label); }
                if (removedContractGenerator) { modified = true; Log.Information("[{Label}] Removed ContractGenerator", label); }
                if (removedCodeNavigator)     { modified = true; Log.Information("[{Label}] Removed CodeNavigator", label); }
                if (removedNotifier2)          { modified = true; Log.Information("[{Label}] Removed Notifier", label); }
                if (!removedRowster && !removedFReader && !removedCliSilentProxy && !removedFWriter && !removedContractGenerator && !removedCodeNavigator && !removedNotifier2)
                    Log.Information("[{Label}] No MCP server entries found", label);
                if (ms.Count == 0) { root.Remove("mcp"); modified = true; Log.Information("[{Label}] mcp is empty — removed key", label); }
            }
            else
            {
                Log.Information("[{Label}] No mcp key found", label);
            }
        }
        else
        {
            if (!root.TryGetPropertyValue("mcp", out var node) || node is not JsonObject ms)
            {
                ms = new JsonObject();
                root["mcp"] = ms;
            }

            if (regRowster && rowsterPath != null)
            {
                ms["Rowster"] = new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = new JsonArray(rowsterPath, "--mcp"),
                    ["enabled"] = true
                };
                modified = true;
                Log.Information("[{Label}] Registered Rowster", label);
            }

            if (regFReader && freaderPath != null)
            {
                ms["FReader"] = new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = new JsonArray(freaderPath, "--mcp"),
                    ["enabled"] = true
                };
                modified = true;
                Log.Information("[{Label}] Registered FReader", label);
            }

            if (regCliSilentProxy && cliSilentProxyPath != null)
            {
                ms["CliSilentProxy"] = new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = new JsonArray(cliSilentProxyPath, "--mcp"),
                    ["enabled"] = true
                };
                modified = true;
                Log.Information("[{Label}] Registered CliSilentProxy", label);
            }

            if (regFWriter && fwriterPath != null)
            {
                ms["FWriter"] = new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = new JsonArray(fwriterPath, "--mcp"),
                    ["enabled"] = true
                };
                modified = true;
                Log.Information("[{Label}] Registered FWriter", label);
            }

            if (regContractGenerator && contractGeneratorPath != null)
            {
                ms["ContractGenerator"] = new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = new JsonArray(contractGeneratorPath, "--mcp"),
                    ["enabled"] = true
                };
                modified = true;
                Log.Information("[{Label}] Registered ContractGenerator", label);
            }

            if (regCodeNavigator && codeNavigatorPath != null)
            {
                ms["CodeNavigator"] = new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = new JsonArray(codeNavigatorPath, "--mcp"),
                    ["enabled"] = true
                };
                modified = true;
                Log.Information("[{Label}] Registered CodeNavigator", label);
            }

            if (regNotifier && notifierPath != null)
            {
                ms["Notifier"] = new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = new JsonArray(notifierPath, "--mcp"),
                    ["enabled"] = true
                };
                modified = true;
                Log.Information("[{Label}] Registered Notifier", label);
            }

        }

        if (!modified)
        {
            Log.Information("[{Label}] No changes needed", label);
            return;
        }

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        try
        {
            if (File.Exists(configPath))
            {
                File.Copy(configPath, backupPath, overwrite: true);
                Log.Information("[{Label}] Backed up to {Path}", label, backupPath);
            }
            File.WriteAllText(configPath, json);
            try
            {
                var check = File.ReadAllText(configPath);
                if (JsonNode.Parse(check) == null)
                    throw new InvalidOperationException("Parsed result is null");
                Log.Information("[{Label}] Write validated successfully", label);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Write validation FAILED: {ex.Message}");
            }
            if (File.Exists(backupPath)) { File.Delete(backupPath); Log.Information("[{Label}] Backup cleaned up", label); }
            Log.Information("[{Label}] Config updated successfully", label);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[{Label}] Failed to update config", label);
            if (File.Exists(backupPath))
            {
                Log.Information("[{Label}] Restoring backup...", label);
                File.Copy(backupPath, configPath, overwrite: true);
                File.Delete(backupPath);
                Log.Information("[{Label}] Backup restored — config is safe", label);
            }
        }
    }

    if (regOpenCode || (uninstall && File.Exists(Path.Combine(configDir, ".config", "opencode", "opencode.json"))))
    {
        var openCodeDir = Path.Combine(configDir, ".config", "opencode");
        Directory.CreateDirectory(openCodeDir);
        ModifyOpenCodeConfig(Path.Combine(openCodeDir, "opencode.json"), "OpenCode");
    }

    // ── Apply to Cursor config ────────────────────────────────────────────

    if (regCursor || (uninstall && File.Exists(Path.Combine(configDir, ".cursor", "mcp.json"))))
    {
        var cursorDir = Path.Combine(configDir, ".cursor");
        Directory.CreateDirectory(cursorDir);
        ModifyConfig(Path.Combine(cursorDir, "mcp.json"), "Cursor");
    }

    // ── Apply to Windsurf config ──────────────────────────────────────────

    if (regWindsurf || (uninstall && File.Exists(Path.Combine(configDir, ".codeium", "windsurf", "mcp_config.json"))))
    {
        var windsurfDir = Path.Combine(configDir, ".codeium", "windsurf");
        Directory.CreateDirectory(windsurfDir);
        ModifyConfig(Path.Combine(windsurfDir, "mcp_config.json"), "Windsurf");
    }

    // ── Apply to Zed config ───────────────────────────────────────────────

    string GetZedConfigPath()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
            return Path.Combine(appData, "Zed", "settings.json");
        return Path.Combine(configDir, ".config", "zed", "settings.json");
    }

    if (regZed || (uninstall && File.Exists(GetZedConfigPath())))
    {
        ModifyZedConfig(GetZedConfigPath(), "Zed");
    }

    void ModifyZedConfig(string configPath, string label)
{
    var backupPath = configPath + ".llm-backup";

    JsonNode? config;
    if (File.Exists(configPath))
    {
        try
        {
            var text = File.ReadAllText(configPath);
            config = JsonNode.Parse(text);
            if (config is not JsonObject)
                throw new InvalidOperationException("Root is not a JSON object");
            Log.Information("Read Zed config from {Path}", configPath);
        }
        catch (Exception ex)
        {
            Log.Warning("Zed config is corrupt — backing up and recreating: {Error}", ex.Message);
            File.Copy(configPath, backupPath, overwrite: true);
            Log.Information("Backed up corrupt file to {Path}", backupPath);
            config = new JsonObject();
        }
    }
    else
    {
        Log.Information("No {Label} config found — will create new", label);
        config = new JsonObject();
    }

    var root = (JsonObject)config;
    var modified = false;

    if (uninstall)
    {
        if (root.TryGetPropertyValue("context_servers", out var node) && node is JsonObject cs)
        {
            var removedRowster           = cs.Remove("Rowster");
            var removedFReader           = cs.Remove("FReader");
            var removedCliSilentProxy    = cs.Remove("CliSilentProxy");
            var removedFWriter           = cs.Remove("FWriter");
            var removedContractGenerator = cs.Remove("ContractGenerator");
            var removedCodeNavigator     = cs.Remove("CodeNavigator");
            var removedNotifier3         = cs.Remove("Notifier");
            if (removedRowster)           { modified = true; Log.Information("[{Label}] Removed Rowster", label); }
            if (removedFReader)           { modified = true; Log.Information("[{Label}] Removed FReader", label); }
            if (removedCliSilentProxy)    { modified = true; Log.Information("[{Label}] Removed CliSilentProxy", label); }
            if (removedFWriter)           { modified = true; Log.Information("[{Label}] Removed FWriter", label); }
            if (removedContractGenerator) { modified = true; Log.Information("[{Label}] Removed ContractGenerator", label); }
            if (removedCodeNavigator)     { modified = true; Log.Information("[{Label}] Removed CodeNavigator", label); }
            if (removedNotifier3)         { modified = true; Log.Information("[{Label}] Removed Notifier", label); }
            if (!removedRowster && !removedFReader && !removedCliSilentProxy && !removedFWriter && !removedContractGenerator && !removedCodeNavigator && !removedNotifier3)
                Log.Information("[{Label}] No MCP server entries found", label);
            if (cs.Count == 0) { root.Remove("context_servers"); modified = true; Log.Information("[{Label}] context_servers is empty — removed key", label); }
        }
        else
        {
            Log.Information("[{Label}] No context_servers key found", label);
        }
    }
    else
    {
        if (!root.TryGetPropertyValue("context_servers", out var node) || node is not JsonObject cs)
        {
            cs = new JsonObject();
            root["context_servers"] = cs;
        }

        if (regRowster && rowsterPath != null)
        {
            cs["Rowster"] = new JsonObject
            {
                ["command"] = new JsonObject
                {
                    ["path"] = rowsterPath,
                    ["args"] = new JsonArray("--mcp")
                }
            };
            modified = true;
            Log.Information("[{Label}] Registered Rowster", label);
        }

        if (regFReader && freaderPath != null)
        {
            cs["FReader"] = new JsonObject
            {
                ["command"] = new JsonObject
                {
                    ["path"] = freaderPath,
                    ["args"] = new JsonArray("--mcp")
                }
            };
            modified = true;
            Log.Information("[{Label}] Registered FReader", label);
        }

        if (regCliSilentProxy && cliSilentProxyPath != null)
        {
            cs["CliSilentProxy"] = new JsonObject
            {
                ["command"] = new JsonObject
                {
                    ["path"] = cliSilentProxyPath,
                    ["args"] = new JsonArray("--mcp")
                }
            };
            modified = true;
            Log.Information("[{Label}] Registered CliSilentProxy", label);
        }

        if (regFWriter && fwriterPath != null)
        {
            cs["FWriter"] = new JsonObject
            {
                ["command"] = new JsonObject
                {
                    ["path"] = fwriterPath,
                    ["args"] = new JsonArray("--mcp")
                }
            };
            modified = true;
            Log.Information("[{Label}] Registered FWriter", label);
        }

        if (regContractGenerator && contractGeneratorPath != null)
        {
            cs["ContractGenerator"] = new JsonObject
            {
                ["command"] = new JsonObject
                {
                    ["path"] = contractGeneratorPath,
                    ["args"] = new JsonArray("--mcp")
                }
            };
            modified = true;
            Log.Information("[{Label}] Registered ContractGenerator", label);
        }

        if (regCodeNavigator && codeNavigatorPath != null)
        {
            cs["CodeNavigator"] = new JsonObject
            {
                ["command"] = new JsonObject
                {
                    ["path"] = codeNavigatorPath,
                    ["args"] = new JsonArray("--mcp")
                }
            };
            modified = true;
            Log.Information("[{Label}] Registered CodeNavigator", label);
        }

        if (regNotifier && notifierPath != null)
        {
            cs["Notifier"] = new JsonObject
            {
                ["command"] = new JsonObject
                {
                    ["path"] = notifierPath,
                    ["args"] = new JsonArray("--mcp")
                }
            };
            modified = true;
            Log.Information("[{Label}] Registered Notifier", label);
        }
    }

    if (!modified)
    {
        Log.Information("[{Label}] No changes needed", label);
        return;
    }

    var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    try
    {
        if (File.Exists(configPath))
        {
            File.Copy(configPath, backupPath, overwrite: true);
            Log.Information("[{Label}] Backed up to {Path}", label, backupPath);
        }
        File.WriteAllText(configPath, json);
        try
        {
            var check = File.ReadAllText(configPath);
            if (JsonNode.Parse(check) == null)
                throw new InvalidOperationException("Parsed result is null");
            Log.Information("[{Label}] Write validated successfully", label);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Write validation FAILED: {ex.Message}");
        }
        if (File.Exists(backupPath)) { File.Delete(backupPath); Log.Information("[{Label}] Backup cleaned up", label); }
        Log.Information("[{Label}] Config updated successfully", label);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "[{Label}] Failed to update config", label);
        if (File.Exists(backupPath))
        {
            Log.Information("[{Label}] Restoring backup...", label);
            File.Copy(backupPath, configPath, overwrite: true);
            File.Delete(backupPath);
            Log.Information("[{Label}] Backup restored — config is safe", label);
        }
    }
}
}
