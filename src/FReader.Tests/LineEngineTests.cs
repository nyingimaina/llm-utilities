using System.Text.Json;

namespace FReader.Tests;

public class LineEngineTests
{
    static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestFixtures", "SampleClass.cs");

    [Fact]
    public void Read_EmptyFile_ReturnsEmptyR()
    {
        var temp = Path.GetTempFileName();
        var result = LineEngine.ReadLines(temp, null, null, null);
        Assert.NotNull(result.Data);
        Assert.Null(result.Error);
        var h = result.Data!["_h"] as string[];
        Assert.Empty(h!);
        File.Delete(temp);
    }

    [Fact]
    public void Read_LineStartBeyondEof_ReturnsError()
    {
        var result = LineEngine.ReadLines(FixturePath, 9999, null, null);
        Assert.Null(result.Data);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Read_LineStartGtLineEnd_ReturnsError()
    {
        var result = LineEngine.ReadLines(FixturePath, 20, 10, null);
        Assert.Null(result.Data);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Read_FullFile_ReturnsAllLines()
    {
        var result = LineEngine.ReadLines(FixturePath, null, null, null);
        Assert.NotNull(result.Data);
        var r = result.Data!["_r"] as string[];
        Assert.NotNull(r);
        Assert.True(r!.Length >= 50);
    }

    [Fact]
    public void Read_LineRange_ReturnsExpectedLines()
    {
        var result = LineEngine.ReadLines(FixturePath, 1, 5, null);
        Assert.NotNull(result.Data);
        var h = result.Data!["_h"] as string[];
        Assert.Equal(5, h!.Length);
        Assert.StartsWith("1\t", h[0]);
    }

    [Fact]
    public void Read_Truncate_Respected()
    {
        var result = LineEngine.ReadLines(FixturePath, 1, 100, 3);
        Assert.NotNull(result.Data);
        var h = result.Data!["_h"] as string[];
        Assert.True(h!.Length <= 3);
        Assert.True((bool)result.Data!["_truncated"]!);
    }

    [Fact]
    public void Read_TruncateZero_Unlimited()
    {
        var result = LineEngine.ReadLines(FixturePath, 1, 10, 0);
        Assert.NotNull(result.Data);
        var h = result.Data!["_h"] as string[];
        Assert.Equal(10, h!.Length);
    }

    [Fact]
    public void Read_TabPrefixFormat()
    {
        var result = LineEngine.ReadLines(FixturePath, 1, 1, null);
        Assert.NotNull(result.Data);
        var h = result.Data!["_h"] as string[];
        Assert.Contains('\t', h![0]);
        var parts = h[0].Split('\t', 2);
        Assert.Equal("1", parts[0]);
        Assert.NotEmpty(parts[1]);
    }

    [Fact]
    public void Read_FileNotFound_ReturnsError()
    {
        var result = LineEngine.ReadLines("z_nonexistent_file_xyz.cs", null, null, null);
        Assert.Null(result.Data);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Info_ReturnsMetadata()
    {
        var info = LineEngine.GetInfo(FixturePath);
        Assert.IsType<Dictionary<string, object?>>(info);
        var dict = (Dictionary<string, object?>)info;
        Assert.True(dict.ContainsKey("_path"));
        Assert.True(dict.ContainsKey("_lines"));
        Assert.True(dict.ContainsKey("_size"));
    }

    [Fact]
    public void Info_FileNotFound_ReturnsError()
    {
        var info = LineEngine.GetInfo("z_nonexistent.cs");
        Assert.IsType<Dictionary<string, object?>>(info);
        var dict = (Dictionary<string, object?>)info;
        Assert.True(dict.ContainsKey("error"));
        Assert.Equal("FILE_NOT_FOUND", dict["error_code"]?.ToString());
    }

    [Fact]
    public void GrepInFile_FindsMatch()
    {
        var result = LineEngine.GrepInFile(FixturePath, "ComputeResult");
        var json = JsonSerializer.Serialize(result);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        Assert.True(dict!["_matches"].GetInt32() >= 1);
    }

    [Fact]
    public void GrepInFile_NoMatch_ReturnsZero()
    {
        var result = LineEngine.GrepInFile(FixturePath, "XYZZYX");
        var dict = (Dictionary<string, object?>)result;
        Assert.Equal(0, (int)dict["_matches"]!);
    }

    [Fact]
    public void GrepInFile_FileNotFound_ReturnsError()
    {
        var result = LineEngine.GrepInFile("z_nonexistent.cs", "test");
        var dict = (Dictionary<string, object?>)result;
        Assert.True(dict.ContainsKey("error"));
    }

    [Fact]
    public void Read_StripImports_RemovesUsingLines()
    {
        var result = LineEngine.ReadLines(FixturePath, 1, 5, null, stripImports: true);
        Assert.NotNull(result.Data);
        var h = result.Data!["_h"] as string[];
        Assert.All(h!, line => Assert.DoesNotContain("using ", line));
    }

    [Fact]
    public void Read_NormalizeIndent_StripsCommonPrefix()
    {
        var result = LineEngine.ReadLines(FixturePath, 14, 16, null, normalizeIndent: true);
        Assert.NotNull(result.Data);
        var h = result.Data!["_h"] as string[];
        var contents = h!.Select(line => line.Split('\t', 2)[1]).ToArray();
        Assert.Equal("public SampleClass(int value, string name)", contents[0]);
        Assert.Equal("{", contents[1]);
        Assert.Equal("    _value = value;", contents[2]);
    }
}
