using LLMUtilities.Commons;

namespace ControlPanel.Models;

public sealed record ServerInfo
{
    public required string Name { get; init; }
    public required string ExePath { get; init; }

    public string? FileVersion { get; init; }
    public string? AssemblyVersion { get; init; }
    public long FileSize { get; init; }

    public string ConfigPath => ConfigPaths.ConfigFile(Name);
    public bool ConfigExists => File.Exists(ConfigPath);
    public string? ConfigContent { get; set; }

    public string LogDir => ConfigPaths.LogDir(Name);
    public string LogFile => ConfigPaths.LogFile(Name);
    public bool LogExists => File.Exists(LogFile);
    public long LogSize => LogExists ? new FileInfo(LogFile).Length : 0;

    public string? RegistrationStatus { get; set; }

    public string Summary => $"{Name} v{AssemblyVersion ?? FileVersion ?? "?"} | {(ConfigExists ? "config ✓" : "config ○")} | Log: {LogSize:N0} bytes";
}
