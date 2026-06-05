namespace ControlPanel.Models;

public sealed record RegistrationEntry
{
    public required string LlmName { get; init; }
    public required string ConfigPath { get; init; }
    public bool ConfigExists { get; init; }
    public bool HasRowster { get; init; }
    public bool HasFReader { get; init; }
    public bool HasCliSilentProxy { get; init; }
    public bool HasNotifier { get; init; }

    public string RowsterLine => StatusLine("Rowster", HasRowster);
    public string FReaderLine => StatusLine("FReader", HasFReader);
    public string CliSilentProxyLine => StatusLine("CliSilentProxy", HasCliSilentProxy);
    public string NotifierLine => StatusLine("Notifier", HasNotifier);

    static string StatusLine(string name, bool present) =>
        present ? $"✓ {name}" : $"○ {name}";
}
