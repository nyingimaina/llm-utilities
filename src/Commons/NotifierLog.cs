using System.Text.Json;

namespace LLMUtilities.Commons;

public static class NotifierLog
{
    static readonly object _lock = new();
    static string? _path;
    static string? _cachedDir;

    static string LogPath
    {
        get
        {
            if (_path is not null) return _path;
            _cachedDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LLMUtilities");
            Directory.CreateDirectory(_cachedDir);
            _path = Path.Combine(_cachedDir, "notifications.jsonl");
            return _path;
        }
    }

    public static void Write(NotificationInfo info)
    {
        try
        {
            var line = info.ToJson();
            lock (_lock)
            {
                File.AppendAllText(LogPath, line + "\n");
            }
        }
        catch { /* best-effort logging */ }
    }

    public static List<NotificationInfo> ReadRecent(int count = 100)
    {
        try
        {
            if (!File.Exists(LogPath))
                return [];

            var lines = File.ReadAllLines(LogPath);
            var results = new List<NotificationInfo>();
            var start = Math.Max(0, lines.Length - count);
            for (int i = start; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    try
                    {
                        var info = JsonSerializer.Deserialize<NotificationInfo>(lines[i],
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (info is not null) results.Add(info);
                    }
                    catch { }
                }
            }
            return results;
        }
        catch
        {
            return [];
        }
    }
}
