namespace FReader;

public static class ProviderFactory
{
    static readonly Dictionary<string, IProvider> _providers = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = new CSharpProvider(),
        [".ts"] = new JsTsProvider(FindWorkerScript()),
        [".tsx"] = new JsTsProvider(FindWorkerScript()),
        [".mts"] = new JsTsProvider(FindWorkerScript()),
        [".cts"] = new JsTsProvider(FindWorkerScript()),
        [".js"] = new JsTsProvider(FindWorkerScript()),
        [".jsx"] = new JsTsProvider(FindWorkerScript()),
        [".mjs"] = new JsTsProvider(FindWorkerScript()),
        [".cjs"] = new JsTsProvider(FindWorkerScript()),
    };

    static string FindWorkerScript()
    {
        var dir = AppContext.BaseDirectory;
        var probe = Path.Combine(dir, "ts-worker.js");
        if (File.Exists(probe)) return probe;

        var srcDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "FReader");
        probe = Path.GetFullPath(Path.Combine(srcDir, "ts-worker.js"));
        if (File.Exists(probe)) return probe;

        return "ts-worker.js";
    }

    public static void Shutdown()
    {
        foreach (var provider in _providers.Values)
        {
            if (provider is IDisposable d)
                d.Dispose();
        }
        _providers.Clear();
    }

    public static string GetWorkerScriptPath() => FindWorkerScript();

    public static string[] SupportedExtensions => [".cs", ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"];

    public static bool HasProvider(string path)
    {
        var ext = Path.GetExtension(path);
        return _providers.ContainsKey(ext);
    }

    public static IProvider? GetProvider(string path)
    {
        var ext = Path.GetExtension(path);
        return _providers.GetValueOrDefault(ext);
    }

    public static List<FunctionInfo> ListFunctions(string path, bool includeSystem = false, CancellationToken ct = default)
    {
        var provider = GetProvider(path);
        if (provider is null) return new List<FunctionInfo>();
        try { return provider.ListFunctions(path, includeSystem, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return new List<FunctionInfo>(); }
    }

    public static FunctionInfo? GetFunction(string path, string name, CancellationToken ct = default)
    {
        var provider = GetProvider(path);
        if (provider is null) return null;
        try { return provider.GetFunction(path, name, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static List<FunctionInfo> GetFunctions(string path, string name, CancellationToken ct = default)
    {
        var provider = GetProvider(path);
        if (provider is null) return new List<FunctionInfo>();
        try { return provider.GetFunctions(path, name, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return new List<FunctionInfo>(); }
    }

    public static List<FunctionInfo> GetFunctions(string path, string name, string[]? parameterTypes, CancellationToken ct = default)
    {
        var provider = GetProvider(path);
        if (provider is null) return new List<FunctionInfo>();
        try { return provider.GetFunctions(path, name, parameterTypes, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return new List<FunctionInfo>(); }
    }

    public static string? Summarize(string path, CancellationToken ct = default)
    {
        var provider = GetProvider(path);
        if (provider is null) return null;
        try { return provider.Summarize(path, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
