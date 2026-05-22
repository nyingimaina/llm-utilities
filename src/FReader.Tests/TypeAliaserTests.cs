namespace FReader.Tests;

public class TypeAliaserTests
{
    [Fact]
    public void Apply_NoRepeatedTypes_ReturnsNull()
    {
        var lines = new[] { "1\tvar x = new StringBuilder();" };
        var (aliases, output, warning) = TypeAliaser.Apply(lines);
        Assert.Null(aliases);
        Assert.Null(warning);
    }

    [Fact]
    public void Apply_RepeatedType_CreatesAlias()
    {
        var lines = new[]
        {
            "1\tvar a = new ConfigurationManager();",
            "2\tvar b = new ConfigurationManager();",
            "3\tvar c = new ConfigurationManager();"
        };
        var (aliases, output, warning) = TypeAliaser.Apply(lines);
        Assert.NotNull(aliases);
        Assert.Contains("ConfigurationManager", aliases);
        Assert.NotNull(warning);
    }

    [Fact]
    public void Apply_ReplacesInContent()
    {
        var lines = new[]
        {
            "1\tvar a = new ConfigurationManager();",
            "2\tvar b = new ConfigurationManager();",
            "3\tvar c = new ConfigurationManager();"
        };
        var (aliases, output, warning) = TypeAliaser.Apply(lines);
        Assert.DoesNotContain("ConfigurationManager", output[0]);
        Assert.Contains(aliases!["ConfigurationManager"], output[0]);
    }

    [Fact]
    public void Apply_DoesNotAliasPrimitives()
    {
        var lines = new[]
        {
            "1\tint a = 1;",
            "2\tint b = 2;",
            "3\tint c = 3;"
        };
        var (aliases, output, warning) = TypeAliaser.Apply(lines);
        Assert.Null(aliases);
    }

    [Fact]
    public void Apply_DoesNotAliasSingleAppearance()
    {
        var lines = new[]
        {
            "1\tvar a = new StringBuilder();",
            "2\tvar b = new List<int>();",
            "3\tvar c = new Dictionary<int,int>();"
        };
        var (aliases, output, warning) = TypeAliaser.Apply(lines);
        Assert.Null(aliases);
    }

    [Fact]
    public void Apply_MultipleTypes_GeneratesUniqueAliases()
    {
        var lines = new[]
        {
            "1\tAbcDef GhiJkl();",
            "2\tAbcDef GhiJkl();",
            "3\tAbcDef GhiJkl();",
            "4\tAbcDef GhiJkl();"
        };
        var (aliases, output, warning) = TypeAliaser.Apply(lines);
        Assert.NotNull(aliases);
        Assert.Contains("AbcDef", aliases);
        Assert.Contains("GhiJkl", aliases);
        Assert.NotEqual(aliases["AbcDef"], aliases["GhiJkl"]);
    }

    [Fact]
    public void Apply_CollisionAvoidance_AppendsSuffix()
    {
        var lines = new[]
        {
            "1\tABC ABc();",
            "2\tABC ABc();",
            "3\tABC ABc();",
            "4\tABC ABc();"
        };
        var (aliases, output, warning) = TypeAliaser.Apply(lines, minAliasLen: 1);
        Assert.NotNull(aliases);
        Assert.NotEqual(aliases["ABC"], aliases["ABc"]);
    }

    [Fact]
    public void Apply_KeepsTabPrefix()
    {
        var lines = new[]
        {
            "42\tvar x = new StringBuilder();",
            "43\tvar y = new StringBuilder();",
            "44\tvar z = new StringBuilder();"
        };
        var (aliases, output, warning) = TypeAliaser.Apply(lines);
        Assert.StartsWith("42\t", output[0]);
        Assert.StartsWith("43\t", output[1]);
    }

    [Fact]
    public void Apply_DoesNotAliasShortNames()
    {
        var lines = new[]
        {
            "1\tFoo Bar();",
            "2\tFoo Bar();",
            "3\tFoo Bar();"
        };
        var (aliases, output, warning) = TypeAliaser.Apply(lines, minAliasLen: 2);
        Assert.NotNull(aliases);
        // Foo and Bar are both 3 chars — uppercase-starting — should be aliased but not to 1-char
        Assert.True(aliases["Foo"].Length >= 2);
        Assert.True(aliases["Bar"].Length >= 2);
    }
}
