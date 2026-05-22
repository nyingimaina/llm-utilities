using System.Text.Json;

namespace FReader.Tests;

public class SearchEngineTests
{
    static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestFixtures");

    static JsonElement RunSearch(string name, string[]? extensions = null, int limit = 20, bool includeGenerated = false)
    {
        var result = SearchEngine.SearchFunction(FixtureDir, name, extensions, limit, includeGenerated);
        var json = JsonSerializer.Serialize(result);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    [Fact]
    public void SearchFunction_FindsByName()
    {
        var doc = RunSearch("ComputeResult");
        Assert.True(doc.TryGetProperty("_total_matches", out var total));
        Assert.True(total.GetInt32() >= 1);
    }

    [Fact]
    public void SearchFunction_NotFound_ReturnsZero()
    {
        var doc = RunSearch("Z_NONEXISTENT_FUNC");
        Assert.True(doc.TryGetProperty("_total_matches", out var total));
        Assert.Equal(0, total.GetInt32());
    }

    [Fact]
    public void SearchFunction_ExcludesGenerated()
    {
        var doc = RunSearch("ComputeResult");
        Assert.True(doc.TryGetProperty("_r", out var r));
        foreach (var entry in r.EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString()!;
            Assert.DoesNotContain("\\bin\\", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\obj\\", path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SearchFunction_InvalidDirectory_ReturnsError()
    {
        var result = SearchEngine.SearchFunction("z_nonexistent_dir", "Foo", null, 20, false);
        var json = JsonSerializer.Serialize(result);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.True(doc.TryGetProperty("error", out _));
    }

    [Fact]
    public void SearchFunction_ReturnsSearchTime()
    {
        var doc = RunSearch("ComputeResult");
        Assert.True(doc.TryGetProperty("_search_time_ms", out _));
    }

    [Fact]
    public void SearchFunction_RespectsLimit()
    {
        var doc = RunSearch("ComputeResult", limit: 1);
        Assert.True(doc.TryGetProperty("_r", out var r));
        Assert.True(r.GetArrayLength() <= 1);
    }
}
