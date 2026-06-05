using System.Text.RegularExpressions;
using System.Text.Json;
using LLMUtilities.Commons;

namespace CliSilentProxy;

sealed class CliSilentProxyServer : McpServerBase
{
    protected override bool RequiresTimeoutMs(string toolName) => toolName == "run";

    readonly RunEngine _engine;

    public CliSilentProxyServer(string? configPath = null)
        : base(new McpServerConfig
        {
            Name = "CliSilentProxy",
            Version = GetEntryVersion(),
            InstructionsToolDescription = "MANDATORY FIRST STEP: read critical server instructions before using any other tool. Defines compact fields, token-saving conventions, and usage patterns.",
            AnnouncementDirective = "PREFER over built-in Bash: run() auto-parses _parsed (dotnet, flutter, tsc, eslint, ruff, cargo, test runners) and _warnings on success; _tail + get_log on failure. Saves 90-97% tokens vs raw bash.",
            HarnessInstructions = "!!!MANDATORY!!! Use CliSilentProxy run() for ALL shell commands. PREFERRED over built-in Bash. Auto-extracts _parsed results, suppresses verbose output, provides get_log() for failure details. Call get_instructions for full reference.",
        }.WithOverrides(configPath))
    {
        _engine = new RunEngine();
    }

    protected override McpTool[] RegisterTools() => new[]
    {
        new McpTool { Name = "run", Description = "PREFERRED over built-in Bash. EXECUTE any CLI command. Success returns {_s,_exit,_ms,_cmd}. Auto-notifies on completion when command runs >5s. Known-tool outputs also parsed into _parsed (dotnet build/test, flutter/dart analyze, tsc, eslint, ruff, cargo, npm audit, all test runners). surfaceWarnings=true returns _warnings digest on success. Failure returns compressed tail capped at maxOutputTokens + run ID. extraPaths extends PATH. extractPattern uses .NET regex (?<name>...). Always prefer over raw execution.", InputSchema = new { type = "object", properties = new { command = new { type = "string", description = "Executable name or path. Resolves via PATH if no directory separator." }, args = new { type = "array", items = new { type = "string" }, description = "Command arguments (optional)" }, workDir = new { type = "string", description = "Working directory. Defaults to project root." }, tailLines = new { type = "integer", description = "Max lines in failure log tail. Default 50." }, timeoutMs = new { type = "integer", description = "Process timeout in ms (max 300000). Required." }, filterPattern = new { type = "string", description = "Regex to filter failure tail to matching lines. Useful: '(?i)(error|fail|exception)'" }, filterContext = new { type = "integer", description = "Lines of context before/after each filterPattern match (default 0). Same semantics as grep_in_file context." }, extractPattern = new { type = "string", description = ".NET regex (?<name>...) to extract named groups from first matched line on success. Use (?<name>...) not (?P<name>...)." }, tailMode = new { type = "string", description = "Tail strategy on failure: 'tail' (last N lines, default) or 'first_error' (first error + context + closing summary)." }, surfaceWarnings = new { type = "boolean", description = "On success, return deduplicated warning lines as _warnings array (default false)." }, maxOutputTokens = new { type = "integer", description = "Cap failure tail to ~N tokens. Keeps first error + last lines + omission marker." }, extraPaths = new { type = "array", items = new { type = "string" }, description = "Prepend directories to PATH for this run (version-manager/global-tool resolution)." } }, required = new[] { "command", "timeoutMs" } } },
        new McpTool { Name = "get_log", Description = "Retrieve the complete log for a previously failed command execution. Only the last 10 failed runs are retained.", InputSchema = new { type = "object", properties = new { id = new { type = "integer", description = "Run ID from a failed run response (_id field)" }, raw = new { type = "boolean", description = "Return uncompressed original capture (default false)" }, timeoutMs = new { type = "integer", description = "Max wait in ms (max 120000). Required." } }, required = new[] { "id", "timeoutMs" } } },
    };

    protected override object HandleToolCall(string name, JsonElement? arguments)
    {
        switch (name)
        {
            case "run":
            {
                var command = GetString(arguments, "command") ?? "";
                if (string.IsNullOrWhiteSpace(command))
                    throw new McpErrorException("command is required");

                var args = GetStringArray(arguments, "args") ?? Array.Empty<string>();
                var workDir = GetString(arguments, "workDir");
                var tailLines = GetInt(arguments, "tailLines") ?? 50;
                var timeoutMs = GetInt(arguments, "timeoutMs") ?? 120_000;
                var filterPattern = GetString(arguments, "filterPattern");
                var filterContext = GetInt(arguments, "filterContext") ?? 0;
                var extractPattern = GetString(arguments, "extractPattern");
                var tailMode = GetString(arguments, "tailMode");

                if (tailMode is not null and not "tail" and not "first_error")
                    throw new McpErrorException($"tailMode must be 'tail' or 'first_error', got '{tailMode}'");

                var surfaceWarnings = GetBool(arguments, "surfaceWarnings") ?? false;
                var maxOutputTokens = GetInt(arguments, "maxOutputTokens");
                var extraPaths = GetStringArray(arguments, "extraPaths");

                if (!string.IsNullOrEmpty(extractPattern))
                {
                    if (extractPattern.Contains("(?P<"))
                        throw new McpErrorException(JsonSerializer.Serialize(new { error = "extractPattern uses .NET regex syntax: use (?<name>...) not (?P<name>...)", error_code = "INVALID_REGEX_FLAVOR" }));
                    try { _ = new Regex(extractPattern, RegexOptions.IgnoreCase); }
                    catch (ArgumentException ex) { throw new McpErrorException($"Invalid extractPattern regex: {ex.Message}"); }
                }

                RunResult result;
                try { result = _engine.Run(command, args, workDir, tailLines, timeoutMs, filterPattern, filterContext, extractPattern, tailMode, surfaceWarnings, maxOutputTokens, extraPaths); }
                catch (Exception ex) { throw new McpErrorException($"Failed to start process: {ex.Message}"); }

                if (result.ExitCode == 0 && !result.TimedOut)
                {
                    var successResp = new Dictionary<string, object?>
                    {
                        ["_s"]    = "ok",
                        ["_exit"] = result.ExitCode,
                        ["_ms"]   = result.DurationMs,
                        ["_cmd"]  = result.Cmd
                    };
                    if (result.Extracted is not null)
                        successResp["_extracted"] = result.Extracted;
                    if (result.Parsed is not null)
                        successResp["_parsed"] = result.Parsed;
                    if (result.SurfaceWarnings is not null)
                        successResp["_warnings"] = result.SurfaceWarnings;
                    return successResp;
                }

                var suppressTail = result.Parsed?.ShouldSuppressTail == true;
                var resp = new Dictionary<string, object?>
                {
                    ["_s"]           = result.TimedOut ? "timeout" : "fail",
                    ["_exit"]        = result.ExitCode,
                    ["_ms"]          = result.DurationMs,
                    ["_cmd"]         = result.Cmd,
                    ["_id"]          = result.RunId,
                    ["_tail"]        = suppressTail ? null : result.Tail,
                    ["_tail_lines"]  = suppressTail ? null : result.TailLines,
                    ["_total_lines"] = suppressTail ? null : result.TotalLines,
                    ["_truncated"]   = result.Truncated,
                    ["_timed_out"]   = result.TimedOut ? (bool?)true : null,
                };
                if (result.Parsed is not null)
                    resp["_parsed"] = result.Parsed;
                if (result.SurfaceWarnings is not null)
                    resp["_warnings"] = result.SurfaceWarnings;

                if (result.IsFiltered)
                {
                    resp["_filtered"] = true;
                    resp["_pattern_matches"] = result.PatternMatches;
                    resp["_pattern_total"] = result.PatternTotal;
                    if (result.PatternMatches < result.PatternTotal)
                        resp["_hint"] = $"filterPattern matched {result.PatternMatches}/{result.PatternTotal} lines. Call get_log({result.RunId}) for full log.";
                }

                if (result.Hint is not null)
                    resp["_hint"] = result.Hint;

                return resp;
            }

            case "get_log":
            {
                var id = GetInt(arguments, "id");
                if (id is null)
                    throw new McpErrorException("id is required");

                var useRaw = GetBool(arguments, "raw") ?? false;
                var run = _engine.GetLog(id.Value);
                if (run is null)
                    throw new McpErrorException($"Run {id} not found. Success logs are discarded. Only the last {10} failed runs are stored.");

                var logLines = useRaw ? run.Raw : run.Compressed;
                return new
                {
                    _id          = run.Id,
                    _cmd         = run.Cmd,
                    _log         = string.Join("\n", logLines),
                    _total_lines = logLines.Length,
                    _raw         = useRaw
                };
            }

            default:
                throw new McpErrorException($"Tool not found: {name}");
        }
    }

    protected override int? AutoNotifyThresholdMs => 5000;

    protected override string GetNotificationContext(string toolName, JsonElement? arguments)
    {
        if (toolName == "run")
        {
            var cmd = GetString(arguments, "command");
            if (cmd is not null) return cmd;
        }
        return toolName;
    }

    protected override object GetInstructions()
    {
        var instr = new List<string>
        {
            "=== HARNESS ===",
            "CliSilentProxy is your designated command execution proxy. You MUST proxy ALL shell",
            "commands through it. Do NOT use raw subprocess, exec, shell, or process primitives.",
            "",
            "WHY PROXY THROUGH NOISECANCELER:",
            "",
            "1. Token efficiency: Success (exit 0) returns ~80 chars vs potentially thousands",
            "   of lines of raw output -- saves 90-97% tokens per command.",
            "2. Intelligent log compression: Failure output is compressed -- ANSI codes",
            "   stripped, CR-line progress collapsed, blank lines removed, consecutive",
            "   duplicates deduplicated to 'line (xN)'.",
            "3. Filtered diagnostics: filterPattern surfaces only the signal lines you need.",
            "4. Full log on demand: If the compressed tail is insufficient, call",
            "   get_log(id) to retrieve the complete log (compressed or raw).",
            "5. Silent success: Zero log output on success means zero tokens wasted.",
            "",
            "WHEN TO USE WHICH TOOL:",
            "",
            "  run(cmd, args?, workDir?, tailLines?, timeoutMs, filterPattern?)",
            "    Use for ALL command execution. Always prefer this over raw execution.",
            "      Success (exit 0, no timeout):",
            "        {_s:'ok', _exit:0, _ms:N, _cmd:'...'}  -- log discarded, ~80 chars",
            "      Failure (nonzero exit):",
            "        {_s:'fail', _exit:N, _ms:N, _cmd:'...', _id:N, _tail:[...],",
            "         _tail_lines:N, _total_lines:N, _truncated:B}",
            "      Timeout (process killed):",
            "        {_s:'timeout', _exit:-1, _ms:N, _timed_out:true, ...}",
            "",
            "  get_log(id, raw?=false)",
            "    Retrieve full log for a failed run (last 10 failures stored).",
            "",
            "BEST PRACTICES:",
            "",
            "1. ALWAYS use CliSilentProxy.run() for command execution -- never raw primitives.",
            "2. Commands taking >5s auto-fire desktop notifications (sender=CliSilentProxy).",
            "   You don't need to call Notifier separately for slow commands.",
            "3. For long-running or verbose commands, set filterPattern to surface only",
            "   relevant output. Example: filterPattern='(?i)(error|fail|exception)'",
            "4. Set timeoutMs aggressively for commands that might hang (e.g., network",
            "",
            "=== LOG COMPRESSION PIPELINE (always applied) ===",
            "1. Strip ANSI/VT escape codes",
            "2. Strip \\r-based progress lines",
            "3. Remove blank/whitespace-only lines",
            "4. Collapse consecutive identical lines",
            "5. Normalize absolute paths to relative",
            "",
            "",
            "=== OUTPUT PARSERS (auto-detected, adds _parsed to response) ===",
            "dotnet build/msbuild   → { issues[], warning_count, error_count, built_projects[], succeeded }",
            "dotnet test            → { outcome, total, passed, failed, skipped, failed_tests[], passed_tests[] }",
            "flutter/dart analyze   → { issues[], error_count, warning_count, info_count }",
            "tsc                    → { errors[], warnings[], error_count, warning_count }",
            "eslint                 → { issues[], error_count, warning_count }",
            "ruff                   → { issues[], total }",
            "jest/pytest/cargo/go/flutter-dart test  → { outcome, total, passed, failed, failed_tests[] }",
            "cargo build/check/clippy  → { issues[], error_count, warning_count, succeeded }",
            "npm audit              → { vulnerabilities[], total }",
            "mypy                   → { issues[], error_count, note_count }",
            "go vet                 → { issues[], total }",
            "When _parsed is present with ShouldSuppressTail, _tail fields are null (parsed data replaces raw tail).",
            "",
            "=== COMPACT FIELD REFERENCE ===",
            "_s  = status   _exit = exit code  _ms = duration",
            "_cmd = command  _id = run ID  _tail = log lines  _parsed = structured output",
    };
        instr.Add("=== TIMEOUT ===");
        instr.Add("timeoutMs required (notify if >5000ms).");
        instr.Add("");
        instr.Add("=== NOTIFICATION ===");
        instr.Add("Commands >5s auto-notify with the command name in the title.");
        instr.Add("For better messages, pass _meta:{canNotify:true} in params and call");
        instr.Add("Notifier.notify() yourself with a human-friendly description of what");
        instr.Add("was done (e.g. 'dotnet build succeeded (12s)' not 'CliSilentProxy: 8.2s').");
    instr.Add("");
    return new { _h = instr.ToArray() };
    }
}
