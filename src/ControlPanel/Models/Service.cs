using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMUtilities.Commons;

namespace ControlPanel.Models;

public static class Service
{
    static readonly string AppDir = Path.GetDirectoryName(
        Environment.ProcessPath ?? ConfigPaths.BaseDir) ?? ConfigPaths.BaseDir;

    static readonly string[] ServerNames = ["Rowster", "FReader", "CliSilentProxy", "Notifier"];
    static readonly string[] KnownLlmConfigs =
    [
        "Claude Code|.claude.json|mcpServers",
        "Cursor|.cursor\\mcp.json|mcpServers",
        "OpenCode|.config\\opencode\\opencode.json|mcp",
        "Gemini CLI|.gemini\\settings.json|mcpServers",
        "Windsurf|.codeium\\windsurf\\mcp_config.json|mcpServers",
        "Zed|AppData\\Zed\\settings.json|context_servers",
    ];

    public static List<ServerInfo> DetectServers()
    {
        var results = new List<ServerInfo>();
        foreach (var name in ServerNames)
        {
            var exe = Path.Combine(AppDir, name + ".exe");
            if (!File.Exists(exe)) continue;

            var fvi = FileVersionInfo.GetVersionInfo(exe);
            var asmV = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            var configContent = ReadConfigFile(ConfigPaths.ConfigFile(name));

            results.Add(new ServerInfo
            {
                Name = name,
                ExePath = exe,
                FileVersion = fvi.FileVersion,
                AssemblyVersion = fvi.ProductVersion,
                FileSize = new FileInfo(exe).Length,
                ConfigContent = configContent,
            });
        }
        return results;
    }

    static string? ReadConfigFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            var node = JsonNode.Parse(text);
            return JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return null; }
    }

    public static List<RegistrationEntry> DetectRegistrations()
    {
        var userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var results = new List<RegistrationEntry>();

        foreach (var entry in KnownLlmConfigs)
        {
            var parts = entry.Split('|');
            var name = parts[0];
            var relPath = parts[1];
            var key = parts[2];

            var configPath = relPath.StartsWith("AppData")
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), relPath[8..])
                : Path.Combine(userDir, relPath);

            var exists = File.Exists(configPath);
            var (hasR, hasF, hasC, hasN) = (false, false, false, false);

            if (exists)
            {
                try
                {
                    var doc = JsonNode.Parse(File.ReadAllText(configPath));
                    if (doc is JsonObject root && root.TryGetPropertyValue(key, out var node) && node is JsonObject ms)
                    {
                        hasR = ms.ContainsKey("Rowster");
                        hasF = ms.ContainsKey("FReader");
                        hasC = ms.ContainsKey("CliSilentProxy");
                        hasN = ms.ContainsKey("Notifier");
                    }
                }
                catch { }
            }

            results.Add(new RegistrationEntry
            {
                LlmName = name,
                ConfigPath = configPath,
                ConfigExists = exists,
                HasRowster = hasR,
                HasFReader = hasF,
                HasCliSilentProxy = hasC,
                HasNotifier = hasN,
            });
        }
        return results;
    }

    public static bool SaveConfig(string serverName, string json)
    {
        try
        {
            var path = ConfigPaths.ConfigFile(serverName);
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            // Validate JSON before writing
            var parsed = JsonNode.Parse(json);
            File.WriteAllText(path, JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch { return false; }
    }

    public static string? ReadLogTail(string serverName, int maxLines = 200)
    {
        try
        {
            var logFile = ConfigPaths.LogFile(serverName);
            if (!File.Exists(logFile)) return null;
            var lines = File.ReadAllLines(logFile);
            var tail = lines.Length > maxLines ? lines[^maxLines..] : lines;
            return string.Join(Environment.NewLine, tail);
        }
        catch { return null; }
    }

    public static (bool Installed, string? Path) FindInstalledDotNet()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dotnet = Path.Combine(pf, "dotnet", "dotnet.exe");
        if (File.Exists(dotnet)) return (true, dotnet);
        // Also check common alternatives
        var alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet", "dotnet.exe");
        if (File.Exists(alt)) return (true, alt);
        return (false, null);
    }

    public static string? TestConnection(string serverName)
    {
        var exe = Path.Combine(AppDir, serverName + ".exe");
        if (!File.Exists(exe)) return $"Not found: {exe}";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "--mcp" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            // MCP uses line-delimited JSON-RPC — read one line at a time
            var init = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"ControlPanel\",\"version\":\"1.0\"}}}";
            proc.StandardInput.WriteLine(init);
            proc.StandardInput.Flush();

            // Read the initialize result line
            var lineTask = proc.StandardOutput.ReadLineAsync();
            if (!lineTask.Wait(5000))
            {
                try { proc.Kill(); } catch { }
                return $"✗ {serverName} did not respond within 5s";
            }

            var line = lineTask.Result;
            if (string.IsNullOrEmpty(line))
            {
                try { proc.Kill(); } catch { }
                return $"✗ {serverName} returned empty response";
            }

            var node = JsonNode.Parse(line);
            if (node is JsonObject root
                && root.TryGetPropertyValue("result", out var r)
                && r is JsonObject res
                && res.TryGetPropertyValue("serverInfo", out var si)
                && si is JsonObject info)
            {
                var name = info["name"]?.GetValue<string>() ?? serverName;
                var ver = info["version"]?.GetValue<string>() ?? "?";
                return $"✓ {name} v{ver} responded";
            }

            return $"✓ {serverName} started";
        }
        catch (Exception ex)
        {
            return $"✗ {ex.Message}";
        }
    }
}
