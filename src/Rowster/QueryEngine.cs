using MySql.Data.MySqlClient;

namespace Rowster;

static class QueryEngine
{
    public static (object? Result, bool Truncated) Execute(string sql, MySqlConnection conn, MySqlTransaction? tx, int maxRows = 200)
    {
        if (string.IsNullOrWhiteSpace(sql)) return (new { Error = "Empty query" }, false);
        try
        {
            using var cmd = new MySqlCommand(sql, conn, tx);
            var trimmed = sql.TrimStart();
            bool isRead = trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("SHOW",   StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("DESCRIBE", StringComparison.OrdinalIgnoreCase)
                       || trimmed.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase);

            if (isRead)
            {
                using var rdr = cmd.ExecuteReader();
                var rows = new List<Dictionary<string, object?>>();
                int count = 0;
                while (rdr.Read())
                {
                    if (count >= maxRows) return (rows, true);
                    var row = new Dictionary<string, object?>();
                    for (int i = 0; i < rdr.FieldCount; i++)
                        row[rdr.GetName(i)] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                    rows.Add(row);
                    count++;
                }
                return (rows, false);
            }
            else
            {
                return (new { RowsAffected = cmd.ExecuteNonQuery(), Status = "Success" }, false);
            }
        }
        catch (Exception ex) { return (new { Error = ex.Message, Query = sql }, false); }
    }

    public static List<string> SplitStatements(string sql)
    {
        var parts = new List<string>();
        foreach (var p in sql.Split(';'))
        {
            var t = p.Trim();
            if (!string.IsNullOrEmpty(t)) parts.Add(t);
        }
        return parts.Count > 0 ? parts : [sql];
    }

    public static object ListTables(MySqlConnection conn, string? pattern)
    {
        try
        {
            var sql = pattern is not null
                ? $"SHOW TABLES LIKE '{pattern.Replace("'", "''")}'"
                : "SHOW TABLES";
            using var cmd = new MySqlCommand(sql, conn);
            using var rdr = cmd.ExecuteReader();
            var tables = new List<string>();
            while (rdr.Read()) tables.Add(rdr.GetString(0));
            return tables;
        }
        catch (Exception ex) { return new { Error = ex.Message }; }
    }

    public static object DescribeTable(MySqlConnection conn, string table)
    {
        try
        {
            using var cmd = new MySqlCommand($"DESCRIBE `{table.Replace("`", "``")}`", conn);
            using var rdr = cmd.ExecuteReader();
            var cols = new List<Dictionary<string, object?>>();
            while (rdr.Read())
            {
                cols.Add(new Dictionary<string, object?>
                {
                    ["Field"] = rdr.GetString(0),
                    ["Type"] = rdr.GetString(1),
                    ["Null"] = rdr.GetString(2),
                    ["Key"] = rdr.GetString(3),
                    ["Default"] = rdr.IsDBNull(4) ? null : rdr.GetValue(4),
                    ["Extra"] = rdr.GetString(5)
                });
            }
            return cols;
        }
        catch (Exception ex) { return new { Error = ex.Message }; }
    }

    public static object ListDatabases(MySqlConnection conn)
    {
        try
        {
            using var cmd = new MySqlCommand("SHOW DATABASES", conn);
            using var rdr = cmd.ExecuteReader();
            var dbs = new List<string>();
            while (rdr.Read()) dbs.Add(rdr.GetString(0));
            return dbs;
        }
        catch (Exception ex) { return new { Error = ex.Message }; }
    }

    public static object Ping(MySqlConnection conn)
    {
        try
        {
            using var cmd = new MySqlCommand("SELECT 1", conn);
            cmd.ExecuteScalar();
            return new { Status = "Ok" };
        }
        catch (Exception ex) { return new { Error = ex.Message }; }
    }

    // ── New compact helpers ────────────────────────────────────────────────

    public static object Count(MySqlConnection conn, string table, string? where)
    {
        try
        {
            var sql = $"SELECT COUNT(*) FROM `{table.Replace("`", "``")}`";
            if (!string.IsNullOrEmpty(where)) sql += $" WHERE {where}";
            using var cmd = new MySqlCommand(sql, conn);
            return new { Count = Convert.ToInt64(cmd.ExecuteScalar()) };
        }
        catch (Exception ex) { return new { Error = ex.Message }; }
    }

    public static object Sample(MySqlConnection conn, string table, int n, string? where)
    {
        var sql = $"SELECT * FROM `{table.Replace("`", "``")}`";
        if (!string.IsNullOrEmpty(where)) sql += $" WHERE {where}";
        sql += $" ORDER BY RAND() LIMIT {Math.Max(1, Math.Min(n, 1000))}";
        try
        {
            using var cmd = new MySqlCommand(sql, conn);
            using var rdr = cmd.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (rdr.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < rdr.FieldCount; i++)
                    row[rdr.GetName(i)] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        catch (Exception ex) { return new { Error = ex.Message, Query = sql }; }
    }
}
