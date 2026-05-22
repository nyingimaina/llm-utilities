using System.Text.Json;

namespace FReader;

public sealed class JsTsProvider : IProvider, IDisposable
{
    public string Language => "JavaScript/TypeScript";

    NodeJsWorker? _worker;
    readonly string _workerScriptPath;

    public JsTsProvider(string workerScriptPath)
    {
        _workerScriptPath = workerScriptPath;
    }

    NodeJsWorker EnsureWorker()
    {
        if (_worker is null)
            _worker = new NodeJsWorker(_workerScriptPath);
        return _worker;
    }

    JsonElement? CallWorker(string method, object? argsObj = null)
    {
        JsonElement? args = null;
        if (argsObj is not null)
        {
            var json = JsonSerializer.Serialize(argsObj);
            args = JsonDocument.Parse(json).RootElement;
        }
        return EnsureWorker().SendRequest(method, args);
    }

    string ReadContent(string path) => File.ReadAllText(path);

    public List<FunctionInfo> ListFunctions(string path, bool includeSystem = false)
    {
        try
        {
            var content = ReadContent(path);
            var result = CallWorker("listFunctions", new { path, content, includeSystem });
            if (result is null) return new();

            var list = new List<FunctionInfo>();
            foreach (var item in result.Value.EnumerateArray())
            {
                list.Add(new FunctionInfo(
                    item.GetProperty("name").GetString() ?? "",
                    item.GetProperty("lineStart").GetInt32(),
                    item.GetProperty("lineEnd").GetInt32(),
                    item.GetProperty("signature").GetString() ?? ""));
            }
            return list;
        }
        catch { return new(); }
    }

    public FunctionInfo? GetFunction(string path, string name)
    {
        var all = ListFunctions(path);
        return all.Find(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public List<FunctionInfo> GetFunctions(string path, string name)
    {
        return ListFunctions(path).FindAll(f =>
            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public List<FunctionInfo> GetFunctions(string path, string name, string[]? parameterTypes)
    {
        var all = GetFunctions(path, name);
        if (parameterTypes is null || parameterTypes.Length == 0)
            return all;

        return all.Where(f => ParameterTypesMatch(f.Signature, parameterTypes)).ToList();
    }

    static bool ParameterTypesMatch(string signature, string[] parameterTypes)
    {
        var openParen = signature.IndexOf('(');
        var closeParen = signature.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen)
            return parameterTypes.Length == 0;

        var paramsPart = signature.Substring(openParen + 1, closeParen - openParen - 1);
        if (string.IsNullOrWhiteSpace(paramsPart))
            return parameterTypes.Length == 0;

        var sigTypes = paramsPart.Split(',')
            .Select(p => p.Trim())
            .Select(p =>
            {
                var colonIdx = p.IndexOf(':');
                if (colonIdx >= 0)
                {
                    var typePart = p.Substring(colonIdx + 1).Trim();
                    if (typePart.EndsWith('?')) typePart = typePart[..^1].Trim();
                    var eqIdx = typePart.IndexOf('=');
                    if (eqIdx >= 0) typePart = typePart[..eqIdx].Trim();
                    return typePart;
                }
                return p.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            })
            .Where(t => t.Length > 0)
            .ToArray();

        if (sigTypes.Length != parameterTypes.Length) return false;

        for (int i = 0; i < sigTypes.Length; i++)
        {
            if (!string.Equals(sigTypes[i], parameterTypes[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public string? Summarize(string path)
    {
        try
        {
            var content = ReadContent(path);
            var result = CallWorker("summarize", new { path, content });
            return result?.GetProperty("text").GetString();
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _worker?.Dispose();
        _worker = null;
    }
}
