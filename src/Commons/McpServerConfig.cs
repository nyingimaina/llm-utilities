using System.Text.Json;
using Serilog;

namespace LLMUtilities.Commons;

public sealed record McpServerConfig
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string InstructionsToolDescription { get; init; } =
        "MANDATORY FIRST STEP: read critical server instructions before using any other tool. Defines compact fields, token-saving conventions, and usage patterns.";
    public string? HarnessInstructions { get; init; }
    public bool EnableBenchmarking { get; init; } = false;
    public string? BenchmarkStoreFileName { get; init; }
    public string? AnnouncementDirective { get; init; }
    public int? AutoNotifyThresholdMs { get; init; }
    public string? DefaultNotificationSender { get; init; }

    public string GetAnnouncement() =>
        AnnouncementDirective ?? $"ANNOUNCE: You have discovered {Name} v{Version}. You understand its role and will use it appropriately.";

    /// <summary>Apply overrides from a JSON file (if present) and return a new config.</summary>
    public McpServerConfig WithOverrides(string? configPath)
    {
        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            return this;
        McpServerConfigOverride? ov;
        try
        {
            ov = JsonSerializer.Deserialize<McpServerConfigOverride>(File.ReadAllText(configPath));
        }
        catch (Exception ex)
        {
            // Log and continue with defaults — a bad config file must not crash the server.
            Serilog.Log.Warning(ex, "Could not read config overrides from {Path}; using defaults", configPath);
            return this;
        }
        if (ov is null) return this;
        return this with
        {
            InstructionsToolDescription = ov.InstructionsToolDescription ?? InstructionsToolDescription,
            HarnessInstructions = ov.HarnessInstructions ?? HarnessInstructions,
            EnableBenchmarking = ov.EnableBenchmarking ?? EnableBenchmarking,
            BenchmarkStoreFileName = ov.BenchmarkStoreFileName ?? BenchmarkStoreFileName,
            AnnouncementDirective = ov.AnnouncementDirective ?? AnnouncementDirective,
            AutoNotifyThresholdMs = ov.AutoNotifyThresholdMs ?? AutoNotifyThresholdMs,
            DefaultNotificationSender = ov.DefaultNotificationSender ?? DefaultNotificationSender,
        };
    }
}

public sealed record McpServerConfigOverride
{
    public string? InstructionsToolDescription { get; init; }
    public string? HarnessInstructions { get; init; }
    public bool? EnableBenchmarking { get; init; }
    public string? BenchmarkStoreFileName { get; init; }
    public string? AnnouncementDirective { get; init; }
    public int? AutoNotifyThresholdMs { get; init; }
    public string? DefaultNotificationSender { get; init; }
}

public static class ConfigPaths
{
    /// <summary>Get the standard base directory: %LOCALAPPDATA%\LLMUtilities</summary>
    public static string BaseDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LLMUtilities");

    /// <summary>Standard config file path for a server. Uses canonical name casing.</summary>
    public static string ConfigFile(string serverName) =>
        Path.Combine(BaseDir, "configs", serverName + ".json");

    /// <summary>Standard log directory for a server. Uses canonical name casing.</summary>
    public static string LogDir(string serverName) =>
        Path.Combine(BaseDir, "logs", serverName);

    /// <summary>Standard log file path for a server. Uses canonical name casing.</summary>
    public static string LogFile(string serverName) =>
        Path.Combine(LogDir(serverName), serverName + ".log");
}
