using System.Text.Json;
using Rowster;

var connStr = "";
var query = "";
var interactive = false;
var useTransaction = false;
var pretty = false;
var timeoutMins = 5;
var mcpMode = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--connection" when i + 1 < args.Length: connStr = args[++i]; break;
        case "--query"      when i + 1 < args.Length: query   = args[++i]; break;
        case "--mcp":           mcpMode         = true; break;
        case "--interactive":   interactive     = true; break;
        case "--transaction":   useTransaction  = true; break;
        case "--pretty":        pretty          = true; break;
        case "--timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out var t):
            timeoutMins = t; i++; break;
    }
}

if (mcpMode)
{
    using var cm = new ConnectionManager();
    var server = new McpServer(cm, connStr);
    server.Run(Console.In, Console.Out);
    return 0;
}

// ── Legacy CLI mode ────────────────────────────────────────────────────────

var opts = new JsonSerializerOptions { WriteIndented = pretty };
void Out(object data) => Console.WriteLine(JsonSerializer.Serialize(data, opts));
void Err(object data) => Console.Error.WriteLine(JsonSerializer.Serialize(data, opts));

if (string.IsNullOrEmpty(connStr)) { Err(new { Error = "--connection is required" }); return 1; }

using var conn = new MySql.Data.MySqlClient.MySqlConnection(connStr);
conn.Open();

if (interactive)
{
    System.Timers.Timer? timer = null;
    var lck = new object();

    if (timeoutMins > 0)
    {
        timer = new System.Timers.Timer(timeoutMins * 60_000) { AutoReset = false };
        timer.Elapsed += (_, _) => { lock (lck) { Out(new { Status = "Timeout" }); Environment.Exit(0); } };
        timer.Start();
    }

    Out(new { Status = "Ready", Mode = "Interactive" });

    while (true)
    {
        var line = Console.ReadLine();
        timer?.Stop(); timer?.Start();
        if (string.IsNullOrEmpty(line) || line.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
        Out(QueryEngine.Execute(line.Trim(), conn, null));
        Console.WriteLine("---END---");
    }
}
else if (!string.IsNullOrEmpty(query))
{
    var stmts = QueryEngine.SplitStatements(query);
    if (useTransaction && stmts.Count > 1)
    {
        using var tx = conn.BeginTransaction();
        try   { foreach (var s in stmts) Out(QueryEngine.Execute(s, conn, tx)); tx.Commit(); }
        catch { tx.Rollback(); throw; }
    }
    else
    {
        foreach (var s in stmts) Out(QueryEngine.Execute(s, conn, null));
    }
}
else
{
    Console.WriteLine("Usage: Rowster --connection \"...\" [--query \"...\"] [--transaction] [--interactive] [--timeout 5] [--pretty]");
    Console.WriteLine("       Rowster --mcp [--connection \"...\"]");
}

return 0;
