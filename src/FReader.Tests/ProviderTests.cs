namespace FReader.Tests;

public class ProviderTests
{
    static readonly bool _tsReady = InitTs();

    static bool InitTs()
    {
        var candidates = new[]
        {
            @"D:\work\nyingi\code\systems\Jattac.Apps.CompanyMan\front-end\node_modules",
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c))
            {
                Environment.SetEnvironmentVariable("NODE_PATH", c);
                return true;
            }
        }
        return false;
    }
    static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestFixtures", "SampleClass.cs");

    [Fact]
    public void ListFunctions_ReturnsFunctions()
    {
        var funcs = ProviderFactory.ListFunctions(FixturePath);
        Assert.NotEmpty(funcs);
        Assert.Contains(funcs, f => f.Name == "ComputeResult");
    }

    [Fact]
    public void ListFunctions_IncludeSystemFalse_FiltersToString()
    {
        var funcs = ProviderFactory.ListFunctions(FixturePath, includeSystem: false);
        Assert.DoesNotContain(funcs, f => f.Name == "ToString");
        Assert.DoesNotContain(funcs, f => f.Name == "GetHashCode");
        Assert.DoesNotContain(funcs, f => f.Name == "Equals");
    }

    [Fact]
    public void ListFunctions_IncludeSystemTrue_IncludesToString()
    {
        var funcs = ProviderFactory.ListFunctions(FixturePath, includeSystem: true);
        Assert.Contains(funcs, f => f.Name == "ToString");
    }

    [Fact]
    public void ListFunctions_ReturnsLineEnd()
    {
        var funcs = ProviderFactory.ListFunctions(FixturePath);
        Assert.All(funcs, f => Assert.True(f.LineEnd >= f.LineStart));
    }

    [Fact]
    public void GetFunction_FindsByName()
    {
        var func = ProviderFactory.GetFunction(FixturePath, "ComputeResult");
        Assert.NotNull(func);
        Assert.Equal("ComputeResult", func!.Name);
    }

    [Fact]
    public void GetFunction_CaseInsensitive()
    {
        var func = ProviderFactory.GetFunction(FixturePath, "computeresult");
        Assert.NotNull(func);
    }

    [Fact]
    public void GetFunction_NotFound_ReturnsNull()
    {
        var func = ProviderFactory.GetFunction(FixturePath, "Z_NONEXISTENT");
        Assert.Null(func);
    }

    [Fact]
    public void GetFunctions_Overloads_ReturnsBoth()
    {
        var funcs = ProviderFactory.GetFunctions(FixturePath, "ComputeResult");
        Assert.Equal(2, funcs.Count);
    }

    [Fact]
    public void GetFunctions_WithParameterTypesInt_ReturnsSingle()
    {
        var funcs = ProviderFactory.GetFunctions(FixturePath, "ComputeResult", ["int"]);
        Assert.Single(funcs);
        Assert.DoesNotContain("TInput", funcs[0].Signature);
    }

    [Fact]
    public void GetFunctions_WithWrongParamTypes_ReturnsEmpty()
    {
        var funcs = ProviderFactory.GetFunctions(FixturePath, "ComputeResult", ["bool"]);
        Assert.Empty(funcs);
    }

    [Fact]
    public void Summarize_ReturnsText()
    {
        var summary = ProviderFactory.Summarize(FixturePath);
        Assert.NotNull(summary);
        Assert.Contains("SampleClass", summary);
        Assert.Contains("ComputeResult", summary);
    }

    [Fact]
    public void Summarize_UnsupportedExtension_ReturnsNull()
    {
        var summary = ProviderFactory.Summarize("test.py");
        Assert.Null(summary);
    }

    [Fact]
    public void GetFunction_FindsConstructor()
    {
        var func = ProviderFactory.GetFunction(FixturePath, "SampleClass");
        Assert.NotNull(func);
        Assert.True(func!.LineEnd >= func!.LineStart);
    }

    [Fact]
    public void UnsupportedLanguage_ReturnsEmpty()
    {
        var funcs = ProviderFactory.ListFunctions("test.py");
        Assert.Empty(funcs);
    }

    // ── TS/JS Provider Tests ───────────────────────────────────────────────

    static readonly string TsFixturePath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestFixtures", "SampleModule.ts");

    [Fact]
    public void ListFunctions_Ts_ReturnsFunctions()
    {
        var funcs = ProviderFactory.ListFunctions(TsFixturePath);
        Assert.NotEmpty(funcs);
        Assert.Contains(funcs, f => f.Name == "getUsers");
        Assert.Contains(funcs, f => f.Name == "getUser");
        Assert.Contains(funcs, f => f.Name == "buildUrl");
        Assert.Contains(funcs, f => f.Name == "mapUsers");
    }

    [Fact]
    public void ListFunctions_Ts_IncludesConstructor()
    {
        var funcs = ProviderFactory.ListFunctions(TsFixturePath);
        Assert.Contains(funcs, f => f.Name == "constructor" || f.Name == "UserService");
    }

    [Fact]
    public void ListFunctions_Ts_ReturnsAccessors()
    {
        var funcs = ProviderFactory.ListFunctions(TsFixturePath);
        var apiBaseUrl = funcs.Where(f => f.Name == "apiBaseUrl").ToList();
        Assert.NotEmpty(apiBaseUrl);
    }

    [Fact]
    public void ListFunctions_Ts_ReturnsLineEnd()
    {
        var funcs = ProviderFactory.ListFunctions(TsFixturePath);
        Assert.All(funcs, f => Assert.True(f.LineEnd >= f.LineStart));
    }

    [Fact]
    public void GetFunction_Ts_FindsByName()
    {
        var func = ProviderFactory.GetFunction(TsFixturePath, "getUsers");
        Assert.NotNull(func);
        Assert.Equal("getUsers", func!.Name);
    }

    [Fact]
    public void GetFunction_Ts_CaseInsensitive()
    {
        var func = ProviderFactory.GetFunction(TsFixturePath, "GETUSERS");
        Assert.NotNull(func);
    }

    [Fact]
    public void GetFunction_Ts_NotFound_ReturnsNull()
    {
        var func = ProviderFactory.GetFunction(TsFixturePath, "Z_NONEXISTENT");
        Assert.Null(func);
    }

    [Fact]
    public void GetFunctions_Ts_Overloads()
    {
        // No overloads in fixture, but should still find single
        var funcs = ProviderFactory.GetFunctions(TsFixturePath, "getUsers");
        Assert.Single(funcs);
    }

    [Fact]
    public void GetFunctions_Ts_WithParameterTypes()
    {
        var funcs = ProviderFactory.GetFunctions(TsFixturePath, "constructor", ["string"]);
        Assert.NotEmpty(funcs);
    }

    [Fact]
    public void GetFunctions_Ts_WrongParamTypes_ReturnsEmpty()
    {
        var funcs = ProviderFactory.GetFunctions(TsFixturePath, "buildUrl", ["number"]);
        Assert.Empty(funcs);
    }

    [Fact]
    public void Summarize_Ts_ReturnsText()
    {
        var summary = ProviderFactory.Summarize(TsFixturePath);
        Assert.NotNull(summary);
        Assert.Contains("UserService", summary);
        Assert.Contains("IUser", summary);
        Assert.Contains("UserRole", summary);
        Assert.Contains("mapUsers", summary);
    }

    [Fact]
    public void Summarize_Ts_ShowsImports()
    {
        var summary = ProviderFactory.Summarize(TsFixturePath);
        Assert.NotNull(summary);
        Assert.Contains("@angular/core", summary);
    }

    [Fact]
    public void ListFunctions_Ts_ArrowFunctionVariable()
    {
        // defaultPageSize is a const, not a function - should not appear in listFunctions
        var funcs = ProviderFactory.ListFunctions(TsFixturePath);
        Assert.DoesNotContain(funcs, f => f.Name == "defaultPageSize");
    }
}
