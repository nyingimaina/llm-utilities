using System.Diagnostics;
using System.Text.Json;

namespace CodeNavigator;

sealed class JsTsNavigatorProvider : IDisposable
{
    Process? _process;
    StreamWriter? _stdin;
    StreamReader? _stdout;
    readonly string _workerScriptPath;
    int _nextId;
    bool _disposed;

    public JsTsNavigatorProvider(string workerScriptPath)
    {
        _workerScriptPath = workerScriptPath;
    }

    void EnsureStarted()
    {
        if (_process is not null && !_process.HasExited) return;

        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add(_workerScriptPath);
        _process = Process.Start(psi)!;
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _nextId = 0;
    }

    public object Invoke(string method, string path, string? extra = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureStarted();

        var id = Interlocked.Increment(ref _nextId);
        var content = File.ReadAllText(path);
        var args = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["content"] = content
        };
        if (extra is not null)
            args["symbol"] = extra;

        var request = JsonSerializer.Serialize(new { id, method, args });

        lock (_lock)
        {
            _stdin!.WriteLine(request);
            _stdin!.Flush();

            var line = _stdout!.ReadLine();
            if (line is null)
                throw new InvalidOperationException("Node.js worker process ended unexpectedly");

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
                throw new InvalidOperationException($"Worker error: {errorEl.GetString()}");

            if (root.TryGetProperty("result", out var resultEl))
            {
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, object?>>(resultEl.GetRawText());
                return deserialized ?? new Dictionary<string, object?>();
            }

            return new Dictionary<string, object?>();
        }
    }

    static readonly object _lock = new();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_process is not null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { }
            _process.Dispose();
            _process = null;
        }

        _stdin = null;
        _stdout = null;
    }
}
