using System.Diagnostics;
using System.Text.Json;

namespace FReader;

sealed class NodeJsWorker : IDisposable
{
    Process? _process;
    StreamWriter? _stdin;
    StreamReader? _stdout;
    readonly string _workerScriptPath;
    readonly string? _nodeModulesPath;
    int _nextId;
    bool _disposed;

    public NodeJsWorker(string workerScriptPath, string? nodeModulesPath = null)
    {
        _workerScriptPath = workerScriptPath;
        _nodeModulesPath = nodeModulesPath;
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

        if (_nodeModulesPath is not null)
            psi.EnvironmentVariables["NODE_PATH"] = _nodeModulesPath;

        psi.ArgumentList.Add(_workerScriptPath);

        _process = Process.Start(psi)!;
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _nextId = 0;
    }

    public JsonElement? SendRequest(string method, JsonElement? args, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureStarted();

        var id = Interlocked.Increment(ref _nextId);
        var request = JsonSerializer.Serialize(new { id, method, args });

        lock (_lock)
        {
            _stdin!.WriteLine(request);
            _stdin!.Flush();

            ct.ThrowIfCancellationRequested();

            var readTask = _stdout!.ReadLineAsync();
            try
            {
                if (!readTask.Wait(TimeSpan.FromMilliseconds(30000), ct))
                {
                    KillProcess();
                    throw new OperationCanceledException(ct);
                }
            }
            catch (OperationCanceledException)
            {
                KillProcess();
                throw;
            }

            var line = readTask.Result;
            if (line is null)
                throw new InvalidOperationException("Node.js worker process ended unexpectedly");

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
            {
                var msg = errorEl.GetString() ?? "Unknown worker error";
                throw new InvalidOperationException($"Worker error: {msg}");
            }

            if (root.TryGetProperty("result", out var resultEl))
                return resultEl.Clone();

            return null;
        }
    }

    void KillProcess()
    {
        if (_process is not null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { }
        }
    }

    readonly object _lock = new();

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
