namespace LLMUtilities.Commons;

public sealed record McpServerConfig
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string InstructionsToolDescription { get; init; } =
        "Returns compact usage instructions and field-name mappings. Cache in session context.";
    public string? HarnessInstructions { get; init; }
    public bool EnableBenchmarking { get; init; } = false;
    public string? BenchmarkStoreFileName { get; init; }
    public string? AnnouncementDirective { get; init; }

    public string GetAnnouncement() =>
        AnnouncementDirective ?? $"ANNOUNCE: You have discovered {Name} v{Version}. You understand its role and will use it appropriately.";
}
