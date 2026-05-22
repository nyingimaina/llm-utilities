using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMUtilities.Commons;

public record FeatureScores
{
    public double Avg => Scores.Count > 0 ? Scores.Average() : 0;
    public bool Deprecated => Scores.Count >= 8 && Avg < 0.25;
    public List<double> Scores { get; init; } = new();
    public List<string> SessionDates { get; init; } = new();
}

public record ProjectData
{
    public Dictionary<string, FeatureScores> Features { get; init; } = new();
}

public record BenchmarkStore
{
    public int Version { get; init; } = 2;
    public Dictionary<string, ProjectData> Projects { get; init; } = new();
}

public static class FeatureScoreManager
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string? FindProjectRoot(string? filePath)
    {
        if (filePath is null) return null;
        var dir = File.Exists(filePath) ? Path.GetDirectoryName(Path.GetFullPath(filePath)) : Path.GetFullPath(filePath);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
            if (Directory.GetFiles(dir, "*.sln").Length > 0) return dir;
            if (Directory.GetFiles(dir, "*.csproj").Length > 0) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    public static string DefaultStoreFileName => ".freader-benchmarks.json";

    public static string GetStorePath(string projectRoot, string? fileName = null) =>
        Path.Combine(projectRoot, fileName ?? DefaultStoreFileName);

    public static BenchmarkStore Load(string projectRoot, string? fileName = null)
    {
        var path = GetStorePath(projectRoot, fileName);
        if (!File.Exists(path)) return new BenchmarkStore();
        try
        {
            var text = File.ReadAllText(path);
            var store = JsonSerializer.Deserialize<BenchmarkStore>(text, Json) ?? new BenchmarkStore();
            if (store.Version < 2)
            {
                store = new BenchmarkStore { Projects = store.Projects };
                Save(projectRoot, store, fileName);
            }
            return store;
        }
        catch { return new BenchmarkStore(); }
    }

    public static void Save(string projectRoot, BenchmarkStore store, string? fileName = null)
    {
        var path = GetStorePath(projectRoot, fileName);
        var text = JsonSerializer.Serialize(store, Json);
        File.WriteAllText(path, text);
    }

    public static (bool Allowed, string? Reason) CanBenchmark(string projectRoot, string featureName, string? fileName = null)
    {
        var store = Load(projectRoot, fileName);
        if (!store.Projects.TryGetValue(projectRoot, out var proj))
            return (true, null);
        if (!proj.Features.TryGetValue(featureName, out var feat))
            return (true, null);

        if (feat.Scores.Count < 8)
            return (true, null);

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var weekAgo = DateTime.Today.AddDays(-7);
        var recentSessions = feat.SessionDates.Count(d => DateTime.Parse(d) >= weekAgo);
        if (recentSessions >= 3)
        {
            var nextDate = feat.SessionDates
                .Select(d => DateTime.Parse(d))
                .Where(d => d >= weekAgo)
                .OrderBy(d => d)
                .First()
                .AddDays(7);
            return (false, $"Max 3 benchmark sessions per week for '{featureName}'. Next allowed: {nextDate:yyyy-MM-dd}.");
        }

        if (feat.SessionDates.Contains(today))
            return (false, $"Already benchmarked '{featureName}' today ({today}). One session per calendar day.");

        return (true, null);
    }

    public static string RecordScore(string projectRoot, string featureName, double score, string? fileName = null)
    {
        if (score < 0 || score > 1) return "Score must be between 0 and 1.";
        var (allowed, reason) = CanBenchmark(projectRoot, featureName, fileName);
        if (!allowed) return reason!;

        var store = Load(projectRoot, fileName);
        if (!store.Projects.TryGetValue(projectRoot, out var proj))
        {
            proj = new ProjectData();
            store.Projects[projectRoot] = proj;
        }

        if (!proj.Features.TryGetValue(featureName, out var feat))
        {
            feat = new FeatureScores();
            proj.Features[featureName] = feat;
        }

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (!feat.SessionDates.Contains(today))
            feat.SessionDates.Add(today);

        feat.Scores.Add(score);
        Save(projectRoot, store, fileName);

        var status = feat.Deprecated
            ? $"WARNING: Feature '{featureName}' deprecated (avg {feat.Avg:F2} over {feat.Scores.Count} runs, below 0.25 threshold). Consider avoiding this feature."
            : $"Feature '{featureName}' avg: {feat.Avg:F2} over {feat.Scores.Count} runs. Status: active.";
        return status;
    }

    public static Dictionary<string, object> GetBenchmarkInfo(string? filePath, string? fileName = null)
    {
        var root = FindProjectRoot(filePath);
        if (root is null) return new();

        var store = Load(root, fileName);
        var result = new Dictionary<string, object>
        {
            ["_project_root"] = root,
        };

        if (store.Projects.TryGetValue(root, out var proj))
        {
            var features = new Dictionary<string, object>();
            foreach (var (name, feat) in proj.Features)
            {
                features[name] = new
                {
                    avg_score = Math.Round(feat.Avg, 2),
                    runs = feat.Scores.Count,
                    deprecated = feat.Deprecated,
                };
            }
            result["_benchmarks"] = features;
        }

        return result;
    }
}
