namespace FReader;

public interface IProvider
{
    string Language { get; }
    List<FunctionInfo> ListFunctions(string path, bool includeSystem = false);
    FunctionInfo? GetFunction(string path, string name);
    List<FunctionInfo> GetFunctions(string path, string name);
    List<FunctionInfo> GetFunctions(string path, string name, string[]? parameterTypes);
    string? Summarize(string path);
}

public sealed record FunctionInfo(string Name, int LineStart, int LineEnd, string Signature);
