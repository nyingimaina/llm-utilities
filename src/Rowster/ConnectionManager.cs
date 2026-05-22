using System.Collections.Concurrent;
using MySql.Data.MySqlClient;

namespace Rowster;

sealed class ConnectionManager : IDisposable
{
    readonly ConcurrentDictionary<string, CachedConnection> _pool = new();
    readonly object _lock = new();

    public MySqlConnection GetOrCreate(string connStr)
    {
        var hash = Normalize(connStr);
        if (_pool.TryGetValue(hash, out var cached) && cached.Conn.State == System.Data.ConnectionState.Open)
        {
            cached.LastUsed = DateTime.UtcNow;
            return cached.Conn;
        }

        lock (_lock)
        {
            if (_pool.TryGetValue(hash, out cached))
            {
                if (cached.Conn.State == System.Data.ConnectionState.Open)
                {
                    cached.LastUsed = DateTime.UtcNow;
                    return cached.Conn;
                }
                cached.Conn.Dispose();
                _pool.TryRemove(hash, out _);
            }

            var conn = new MySqlConnection(connStr);
            conn.Open();
            _pool[hash] = new CachedConnection(conn);
            return conn;
        }
    }

    public string? UseDatabase(string connStr, string db)
    {
        var conn = GetOrCreate(connStr);
        try
        {
            using var cmd = new MySqlCommand($"USE `{db.Replace("`", "``")}`", conn);
            cmd.ExecuteNonQuery();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public void CloseIdle(TimeSpan maxIdle)
    {
        var cutoff = DateTime.UtcNow - maxIdle;
        foreach (var (hash, cached) in _pool)
        {
            if (cached.LastUsed < cutoff)
            {
                lock (_lock)
                {
                    if (_pool.TryGetValue(hash, out var c) && c.LastUsed < cutoff)
                    {
                        c.Conn.Dispose();
                        _pool.TryRemove(hash, out _);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var (_, cached) in _pool)
            cached.Conn.Dispose();
        _pool.Clear();
    }

    static string Normalize(string cs)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(cs.Trim()));
        return Convert.ToHexString(bytes);
    }

    sealed record CachedConnection(MySqlConnection Conn)
    {
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }
}
