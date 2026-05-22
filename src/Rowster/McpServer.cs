using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LLMUtilities.Commons;

namespace Rowster;

sealed class McpServer : McpServerBase, IDisposable
{
    protected override bool RequiresTimeoutMs(string toolName) => toolName != "ping";

    readonly ConnectionManager _cm;
    readonly JsonSerializerOptions _localJson;
    string? _defaultConnStr;

    public McpServer(ConnectionManager cm, string? defaultConnStr)
        : base(new McpServerConfig
        {
            Name = "Rowster",
            Version = GetEntryVersion(),
            InstructionsToolDescription = "Call this first. Returns compact field-name mapping and token-saving conventions. Cache the result.",
            EnableBenchmarking = false,
        })
    {
        _cm = cm;
        _defaultConnStr = defaultConnStr;
        _localJson = GetJsonOptions();
    }

    protected override JsonSerializerOptions GetJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected override McpTool[] RegisterTools() => new[]
    {
        new McpTool { Name = "query", Description = "Execute SQL. Default format: table (pipe-delimited markdown, ~40% fewer tokens). Use format='json' for columnar. Auto-truncates at maxRows (default 200). Requires timeoutMs.", InputSchema = new { type = "object", properties = new { sql = new { type = "string", description = "SQL statement" }, connection = new { type = "string", description = "MySQL connection string (optional if set)" }, format = new { type = "string", description = "Output format: 'table' (markdown pipe table, default) or 'json' (columnar)" }, maxRows = new { type = "integer", description = "Max rows to return (default 200). Truncated results include _truncated: true." }, timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Required." } }, required = new[] { "sql", "timeoutMs" } } },
        new McpTool { Name = "count", Description = "Return row count for a table. Requires timeoutMs.", InputSchema = new { type = "object", properties = new { connection = new { type = "string", description = "MySQL connection string" }, table = new { type = "string", description = "Table name" }, where = new { type = "string", description = "Optional WHERE clause" }, timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Required." } }, required = new[] { "connection", "table", "timeoutMs" } } },
        new McpTool { Name = "sample", Description = "Return N random rows from a table. Default format: table (pipe-delimited markdown, ~40% fewer tokens). Use format='json' for columnar. Requires timeoutMs.", InputSchema = new { type = "object", properties = new { connection = new { type = "string", description = "MySQL connection string" }, table = new { type = "string", description = "Table name" }, n = new { type = "integer", description = "Row count (max 1000)" }, where = new { type = "string", description = "Optional WHERE clause" }, format = new { type = "string", description = "Output format: 'table' (markdown pipe table, default) or 'json' (columnar)" }, timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Required." } }, required = new[] { "connection", "table", "timeoutMs" } } },
        new McpTool { Name = "list_tables", Description = "List tables matching an optional LIKE pattern. Returns a simple string array. Requires timeoutMs.", InputSchema = new { type = "object", properties = new { connection = new { type = "string", description = "MySQL connection string" }, pattern = new { type = "string", description = "Optional LIKE pattern" }, timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Required." } }, required = new[] { "connection", "timeoutMs" } } },
        new McpTool { Name = "describe_table", Description = "Describe columns. Returns columnar with compact headers. Requires timeoutMs.", InputSchema = new { type = "object", properties = new { connection = new { type = "string", description = "MySQL connection string" }, table = new { type = "string", description = "Table name" }, timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Required." } }, required = new[] { "connection", "table", "timeoutMs" } } },
        new McpTool { Name = "list_databases", Description = "List available databases. Returns a simple string array. Requires timeoutMs.", InputSchema = new { type = "object", properties = new { connection = new { type = "string", description = "MySQL connection string" }, timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Required." } }, required = new[] { "connection", "timeoutMs" } } },
        new McpTool { Name = "connect", Description = "Set the default connection string for subsequent tool calls. Requires timeoutMs.", InputSchema = new { type = "object", properties = new { connection = new { type = "string", description = "MySQL connection string" }, timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Required." } }, required = new[] { "connection", "timeoutMs" } } },
        new McpTool { Name = "ping", Description = "Health check. Returns {\"_s\":\"ok\"} when alive.", InputSchema = new { type = "object", properties = new { }, required = new string[0] } },
    };

    protected override object HandleToolCall(string name, JsonElement? arguments)
    {
        string? connStr;

        switch (name)
        {
            case "connect":
            {
                var cs = GetString(arguments, "connection");
                if (string.IsNullOrEmpty(cs))
                    throw new McpErrorException("{\"_e\":\"connection is required\"}");
                _defaultConnStr = cs;
                try { _cm.GetOrCreate(cs); }
                catch (Exception ex)
                {
                    _defaultConnStr = null;
                    throw new McpErrorException($"{{\"_e\":\"{ex.Message.Replace("\"", "'")}\"}}");
                }
                return new { _s = "connected" };
            }

            case "query":
            {
                var sql = GetString(arguments, "sql") ?? "";
                connStr = TryGetConnStr(arguments) ?? _defaultConnStr;
                if (string.IsNullOrEmpty(connStr)) MissingConnection();
                var maxRows = GetInt(arguments, "maxRows") ?? 200;
                var format = GetString(arguments, "format") ?? "table";
                var (result, truncated) = QueryEngine.Execute(sql, _cm.GetOrCreate(connStr!), null, maxRows);
                if (result is List<Dictionary<string, object?>> rows)
                {
                    if (format == "table")
                        return new { _table = RowsToTable(rows), _truncated = truncated ? true : (bool?)null };
                    string[] headers = rows.Count > 0 ? rows[0].Keys.ToArray() : Array.Empty<string>();
                    var data = new object?[rows.Count][];
                    for (int r = 0; r < rows.Count; r++)
                    {
                        data[r] = new object?[headers.Length];
                        for (int c = 0; c < headers.Length; c++)
                            data[r][c] = rows[r][headers[c]];
                    }
                    var dict = new Dictionary<string, object?> { ["_h"] = headers, ["_r"] = data };
                    if (truncated) dict["_truncated"] = true;
                    return dict;
                }
                return FormatMcpResult(result!);
            }

            case "count":
            {
                connStr = TryGetConnStr(arguments);
                if (string.IsNullOrEmpty(connStr)) MissingConnection();
                var table = GetString(arguments, "table") ?? "";
                var where = GetString(arguments, "where");
                return FormatMcpResult(QueryEngine.Count(_cm.GetOrCreate(connStr!), table, where));
            }

            case "sample":
            {
                connStr = TryGetConnStr(arguments);
                if (string.IsNullOrEmpty(connStr)) MissingConnection();
                var tbl = GetString(arguments, "table") ?? "";
                var n = GetInt(arguments, "n") ?? 5;
                var wh = GetString(arguments, "where");
                var fmt = GetString(arguments, "format") ?? "table";
                var sampleResult = QueryEngine.Sample(_cm.GetOrCreate(connStr!), tbl, n, wh);
                if (fmt == "table" && sampleResult is List<Dictionary<string, object?>> srows)
                    return new { _table = RowsToTable(srows) };
                return FormatMcpResult(sampleResult);
            }

            case "list_tables":
            {
                connStr = TryGetConnStr(arguments);
                if (string.IsNullOrEmpty(connStr)) MissingConnection();
                var pattern = GetString(arguments, "pattern");
                return FormatMcpResult(QueryEngine.ListTables(_cm.GetOrCreate(connStr!), pattern));
            }

            case "describe_table":
            {
                connStr = TryGetConnStr(arguments);
                if (string.IsNullOrEmpty(connStr)) MissingConnection();
                var tblName = GetString(arguments, "table") ?? "";
                return FormatMcpResult(QueryEngine.DescribeTable(_cm.GetOrCreate(connStr!), tblName));
            }

            case "list_databases":
            {
                connStr = TryGetConnStr(arguments);
                if (string.IsNullOrEmpty(connStr)) MissingConnection();
                return FormatMcpResult(QueryEngine.ListDatabases(_cm.GetOrCreate(connStr!)));
            }

            case "ping":
            {
                if (!string.IsNullOrEmpty(_defaultConnStr))
                {
                    try { return FormatMcpResult(QueryEngine.Ping(_cm.GetOrCreate(_defaultConnStr))); }
                    catch (Exception ex) { return new { _e = ex.Message }; }
                }
                return new { _s = "ok", _note = "No connection" };
            }

            default:
                throw new McpErrorException($"{{\"_e\":\"Tool not found: {name}\"}}");
        }
    }

    protected override string? GetHarnessInstructions() => null;
    protected override object GetInstructions()
    {
        var instr = new List<string>
        {
            "=== SERVER IDENTITY ===",
            "Rowster is a MySQL database query server. Use it for any task involving MySQL databases:",
            "- Schema exploration: list_tables, describe_table, list_databases",
            "- Data extraction: query (always add LIMIT), count, sample",
            "- Health checks: ping",
            "",
            "=== CONNECTION ===",
            "Call connect(connection) once -- all subsequent calls reuse the same connection.",
            "Connection string format: Server=HOST;Database=DB;User ID=USER;Password=PWD;ConvertZeroDateTime=True",
            "Critical: always append ;ConvertZeroDateTime=True to avoid zero-date exceptions.",
            "",
            "=== COMPACT FIELD NAMES ===",
            "_ra = RowsAffected    _cnt = count result",
            "_s  = Status           _e  = Error",
            "_q  = Query            _f  = Field",
            "_t  = Type             _n  = Null",
            "_k  = Key              _d  = Default",
            "_x  = Extra            _h  = column headers",
            "_r  = data rows        _truncated = results limited",
            "_note = additional info",
            "",
            "=== FORMAT CONVENTIONS ===",
            "- query() & sample(): columnar  {_h:[cols], _r:[[vals],...]}",
            "- list_tables, list_databases: simple array [\"a\",\"b\"]",
            "- write result: {\"_ra\":5, \"_s\":\"ok\"}",
            "- count result: {\"_cnt\":42}",
            "- error: {\"_e\":\"msg\", \"_q\":\"sql\"}",
            "- ping: {\"_s\":\"ok\"}",
            "",
            "=== TOKEN-SAVING TIPS ===",
            "1. Use count(t, ?where) instead of SELECT COUNT(*)",
            "2. Use sample(t, n, ?where) instead of SELECT * LIMIT N",
            "3. query() auto-truncates at maxRows (default 200); _truncated: true if limited",
            "4. Use describe_table instead of query(\"DESCRIBE t\")",
            "5. If you forget the mapping, call get_instructions again",
            "",
        };
        instr.AddRange(GetResiliencySection());
        instr.Add("");
        instr.AddRange(GetSelfImprovementSection());
        return new { _h = instr.ToArray() };
    }

    string? TryGetConnStr(JsonElement? arguments)
    {
        var cs = GetString(arguments, "connection");
        return !string.IsNullOrEmpty(cs) ? cs : _defaultConnStr;
    }

    void MissingConnection() =>
        throw new McpErrorException("{\"_e\":\"No connection. Use connect tool or --connection arg.\"}");

    object FormatMcpResult(object raw)
    {
        if (raw is List<Dictionary<string, object?>> rows)
            return ToColumnar(rows);
        if (raw is List<string> strList)
            return strList;
        var je = JsonSerializer.SerializeToElement(raw, _localJson);
        if (je.ValueKind != JsonValueKind.Object) return raw;
        var dict = new Dictionary<string, object?>();
        foreach (var prop in je.EnumerateObject())
        {
            dict[CompactKey(prop.Name)] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => l,
                JsonValueKind.Number when prop.Value.TryGetDouble(out var d) => d,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText()
            };
        }
        return dict;
    }

    object ToColumnar(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new { _h = Array.Empty<string>(), _r = Array.Empty<object?[]>() };
        var headers = rows[0].Keys.ToArray();
        var data = new object?[rows.Count][];
        for (int r = 0; r < rows.Count; r++)
        {
            data[r] = new object?[headers.Length];
            for (int c = 0; c < headers.Length; c++)
                data[r][c] = rows[r][headers[c]];
        }
        return new { _h = headers, _r = data };
    }

    static string CompactKey(string name) => name switch
    {
        "rowsAffected" or "RowsAffected" => "_ra",
        "status" or "Status" => "_s",
        "error" or "Error" => "_e",
        "query" or "Query" => "_q",
        "field" or "Field" => "_f",
        "type" or "Type" => "_t",
        "null" or "Null" => "_n",
        "key" or "Key" => "_k",
        "default" or "Default" => "_d",
        "extra" or "Extra" => "_x",
        "count" or "Count" => "_cnt",
        "note" or "Note" => "_note",
        _ => name
    };

    static string RowsToTable(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return "(no rows)";
        var headers = rows[0].Keys.ToArray();
        var sb = new System.Text.StringBuilder();
        sb.Append('|'); foreach (var h in headers) { sb.Append(' '); sb.Append(h); sb.Append(" |"); }
        sb.AppendLine();
        sb.Append('|'); foreach (var h in headers) { sb.Append(" --- |"); }
        sb.AppendLine();
        foreach (var row in rows)
        {
            sb.Append('|');
            foreach (var h in headers)
            {
                sb.Append(' ');
                var val = row[h];
                if (val is null) sb.Append("NULL");
                else if (val is string s) sb.Append(s.Length > 80 ? s[..77] + "..." : s);
                else sb.Append(val);
                sb.Append(" |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public void Dispose() => _cm.Dispose();
}
