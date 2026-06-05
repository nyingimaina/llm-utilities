namespace FReader;

public interface IProvider
{
    string Language { get; }
    List<FunctionInfo> ListFunctions(string path, bool includeSystem = false, CancellationToken ct = default);
    FunctionInfo? GetFunction(string path, string name, CancellationToken ct = default);
    List<FunctionInfo> GetFunctions(string path, string name, CancellationToken ct = default);
    List<FunctionInfo> GetFunctions(string path, string name, string[]? parameterTypes, CancellationToken ct = default);
    string? Summarize(string path, CancellationToken ct = default);
}

public sealed record FunctionInfo(string Name, int LineStart, int LineEnd, string Signature);
