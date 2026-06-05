using System.Diagnostics;
using System.Text.Json;
using LLMUtilities.Commons;
using Serilog;

namespace Notifier;

public sealed class NotifierServer : McpServerBase
{
    static readonly string HelperPath = Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "NotifierHelper.exe" : "NotifierHelper");

    public NotifierServer(string? configPath = null) : base(new McpServerConfig
    {
        Name = "Notifier",
        Version = GetEntryVersion(),
        AnnouncementDirective = "Desktop notification service: notify() for toast + notify_on_complete() for process completion alerts. Use for long-running tasks and cognitive work milestones. Call get_instructions first.",
        HarnessInstructions = "Use Notifier for desktop notifications on completions, errors, and long-running tasks. notify() for instant toasts, notify_on_complete() to wrap a process.",
        InstructionsToolDescription = "MANDATORY FIRST STEP: read critical server instructions before using any other tool. Defines notification patterns and usage conventions.",
    }.WithOverrides(configPath))
    {
    }

    protected override bool RequiresTimeoutMs(string toolName) => false;

    protected override int? DefaultTimeoutMs(string toolName) =>
        toolName == "notify_on_complete" ? 60000 : 30000;

    protected override McpTool[] RegisterTools() =>
    [
        new McpTool
        {
            Name = "notify",
            Description = "Send a desktop toast. PREFERRED over bash-based notifications. Use for cognitive work completions (analysis, review, reasoning) and opted-in slow ops. DO NOT use for steps the user sees in your reply.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string", description = "What completed. Be specific." },
                    message = new { type = "string", description = "Outcome + key metrics (errors, duration). Write for a human." },
                    sender = new { type = "string", description = "Your LLM name (e.g. 'Claude', 'Gemini'). MANDATORY." },
                    task = new { type = "string", description = "What you were doing (e.g. 'Code review', 'Build fix-123')." },
                    type = new { type = "string", description = "info (default) / error", @default = "info" },
                    sound = new { type = "boolean", description = "Play sound (default true)", @default = true },
                    dismissAfterMs = new { type = "integer", description = "Auto-dismiss ms (default 5000). 0=sticky. Use sticky for errors, failures, plan-complete waiting-for-input." },
                    timeoutMs = new { type = "integer", description = "Max wait ms (max 60000). Default 30000." }
                },
                required = new[] { "title", "message", "sender" }
            }
        },
        new McpTool
        {
            Name = "notify_on_complete",
            Description = "PREFERRED over bash-based process monitoring. Use when you need a custom notification message for a shell command, or when running commands outside CliSilentProxy (background scripts, direct invocations). Returns immediately with _pid; fires toast when the process exits. Not needed for CliSilentProxy.run() commands — those auto-notify unless you want a custom message.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "Executable path or name" },
                    args = new { type = "array", items = new { type = "string" }, description = "Args (optional)" },
                    workDir = new { type = "string", description = "Working directory (optional)" },
                    title = new { type = "string", description = "Title. Default: 'Command Complete'." },
                    message = new { type = "string", description = "Custom message. Default: exit code + duration." },
                    sender = new { type = "string", description = "Your LLM name. MANDATORY." },
                    task = new { type = "string", description = "What you were doing." },
                    type = new { type = "string", description = "info (default) / error. Auto-set to error if exit != 0.", @default = "info" },
                    sound = new { type = "boolean", description = "Play sound (default true)", @default = true },
                    timeoutMs = new { type = "integer", description = "Max wait ms (no default = waits indefinitely)" }
                },
                required = new[] { "command", "sender" }
            }
        },
    ];

    protected override object HandleToolCall(string name, JsonElement? arguments)
    {
        switch (name)
        {
            case "notify":
                return HandleNotify(arguments);

            case "notify_on_complete":
                return HandleNotifyOnComplete(arguments);

            default:
                throw new McpErrorException(JsonSerializer.Serialize(new { error = $"Tool not found: {name}" }));
        }
    }

    object HandleNotify(JsonElement? args)
    {
        var title = GetString(args, "title") ?? "";
        var message = GetString(args, "message") ?? "";
        var sender = GetString(args, "sender");
        var task = GetString(args, "task");
        var type = GetString(args, "type") ?? "info";
        var sound = GetBool(args, "sound") ?? true;
        var dismissAfterMs = GetInt(args, "dismissAfterMs") ?? 5000;

        var err = SendNotification(title, message, type, sound, dismissAfterMs, sender, task);
        if (err is not null)
            return new { _sent = false, _error = err, _fallback = "Print the notification in your reply instead." };

        return new { _sent = true };
    }

    object HandleNotifyOnComplete(JsonElement? args)
    {
        var command = GetString(args, "command") ?? "";
        var cmdArgs = GetStringArray(args, "args") ?? [];
        var workDir = GetString(args, "workDir");
        var title = GetString(args, "title") ?? "Command Complete";
        var messageTemplate = GetString(args, "message");
        var sender = GetString(args, "sender");
        var task = GetString(args, "task");
        var type = GetString(args, "type") ?? "info";
        var sound = GetBool(args, "sound") ?? true;
        var timeoutMs = GetInt(args, "timeoutMs");

        var psi = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in cmdArgs)
            psi.ArgumentList.Add(a);

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex)
            { return new { _status = "failed", _error = $"Cannot start process: {ex.Message}" }; }

        var pid = proc!.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                const int MaxWaitMs = 3_600_000; // 1-hour cap prevents indefinite process leak
                var exited = proc!.WaitForExit(timeoutMs ?? MaxWaitMs);
                sw.Stop();

                if (!exited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    SendNotification(title, $"Timed out after {timeoutMs}ms", "error", sound, 5000, sender, task);
                    return;
                }

                var code = proc.ExitCode;
                var elapsed = sw.Elapsed;
                var finalType = code == 0 ? type : "error";
                var finalMsg = messageTemplate
                    ?? $"Exit code: {code} (elapsed: {elapsed.TotalSeconds:F1}s)";
                SendNotification(title, finalMsg, finalType, sound, 5000, sender, task);
            }
            catch { /* background fire-and-forget — best-effort */ }
        });

        return new { _pid = pid, _status = "running" };
    }

    string? SendNotification(string title, string message, string type, bool sound, int dismissAfterMs,
        string? sender = null, string? task = null)
    {
        var (okType, okSound) = NormalizeType(type);

        if (File.Exists(HelperPath))
        {
            try
            {
                var helperPsi = new ProcessStartInfo
                {
                    FileName = HelperPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                helperPsi.ArgumentList.Add("--title");
                helperPsi.ArgumentList.Add(title);
                helperPsi.ArgumentList.Add("--message");
                helperPsi.ArgumentList.Add(message);
                helperPsi.ArgumentList.Add("--type");
                helperPsi.ArgumentList.Add(okType);
                if (!string.IsNullOrEmpty(sender)) { helperPsi.ArgumentList.Add("--sender"); helperPsi.ArgumentList.Add(sender); }
                if (!string.IsNullOrEmpty(task))   { helperPsi.ArgumentList.Add("--task");   helperPsi.ArgumentList.Add(task); }
                if (!sound) helperPsi.ArgumentList.Add("--no-sound");
                helperPsi.ArgumentList.Add("--dismiss-after-ms");
                helperPsi.ArgumentList.Add(dismissAfterMs.ToString());
                Process.Start(helperPsi);
                return null;
            }
            catch { /* fall through to OS-native */ }
        }

        return SendNativeNotification(title, message, okType, okSound);
    }

    static (string Type, bool Sound) NormalizeType(string type) =>
        type.ToLowerInvariant() switch
        {
            "error" => ("error", true),
            _ => ("info", true),
        };

    string? SendNativeNotification(string title, string message, string type, bool sound)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return SendWindowsNotification(title, message, type, sound);
            if (OperatingSystem.IsMacOS())
                return SendMacNotification(title, message, type, sound);
            if (OperatingSystem.IsLinux())
                return SendLinuxNotification(title, message, type, sound);
        }
        catch (Exception ex)
        {
            return $"Failed: {ex.Message}";
        }

        return "UNSUPPORTED_PLATFORM";
    }

    static string? SendWindowsNotification(string title, string message, string type, bool sound)
    {
        // Title and message are Base64-encoded so no PowerShell metacharacter can
        // escape the string context and execute injected code.
        var titleB64  = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(title));
        var messageB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(message));
        var soundSrc  = type == "error"
            ? "ms-winsoundevent:Notification.Critical"
            : "ms-winsoundevent:Notification.Default";

        var psScript = $@"
Add-Type -AssemblyName System.Runtime.WindowsRuntime;
$null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime];
$t = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{titleB64}'));
$m = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{messageB64}'));
$template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);
$nodes = $template.GetElementsByTagName('text');
$nodes.Item(0).AppendChild($template.CreateTextNode($t)) > $null;
$nodes.Item(1).AppendChild($template.CreateTextNode($m)) > $null;
{(sound ? $"$audio = $template.CreateElement('audio'); $audio.SetAttribute('src', '{soundSrc}'); $template.GetElementsByTagName('toast').Item(0).AppendChild($audio) > $null;" : "")}
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('LLM Utilities').Show($template);
";
        var psi = new ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(psScript);
        Process.Start(psi);
        return null;
    }

    static string? SendMacNotification(string title, string message, string type, bool sound)
    {
        // Use ArgumentList so the OS passes arguments verbatim — no shell expansion.
        // The AppleScript string still requires its own escaping for " and \.
        var safeTitle   = title.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var safeMessage = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var script      = $"display notification \"{safeMessage}\" with title \"{safeTitle}\"";

        if (sound)
        {
            var soundFile = type == "error" ? "Basso" : "Ping";
            var afplay = new ProcessStartInfo("afplay") { UseShellExecute = false, CreateNoWindow = true };
            afplay.ArgumentList.Add($"/System/Library/Sounds/{soundFile}.aiff");
            Process.Start(afplay);
        }

        var psi = new ProcessStartInfo("osascript") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);
        Process.Start(psi);
        return null;
    }

    static string? SendLinuxNotification(string title, string message, string type, bool sound)
    {
        // ArgumentList bypasses shell parsing — title/message are passed verbatim.
        var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(type == "error" ? "critical" : "normal");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("5000");
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add(message);
        Process.Start(psi);
        return null;
    }

    protected override string? GetHarnessInstructions() => null;

    protected override object GetInstructions()
    {
        return new
        {
            _h = (string[])
            [
                "=== Notifier v1.2 ===",
                "Sends desktop toast notifications. Your job: ensure the user is informed when",
                "important work completes, especially when they've tabbed away.",
                "",
                "=== QUICK CONTRACT ===",
                "1. OPT-IN FOR QUALITY: On any tool call, pass _meta:{canNotify:true} to receive",
                "   _shouldNotify:true when the op exceeds the threshold. When you see it, call",
                "   notify() with a specific human-readable message. Without opt-in, a generic",
                "   mechanical notification fires automatically (\"CliSilentProxy: 8.2s\").",
                "2. COGNITIVE WORK: For analysis, code review, reasoning, or plan completion —",
                "   always call notify() explicitly. There is no auto-trigger for LLM thinking.",
                "3. SENDER: Always set sender to your LLM name (e.g. \"Claude\"). The user runs",
                "   multiple LLMs and needs to know who sent this.",
                "4. TASK: Always set task (e.g. \"Code review\", \"Build fix-123\"). Enables instant",
                "   context-switching.",
                "",
                "=== WHEN TO NOTIFY ===",
                "Auto-notification fires (no action needed) when:",
                "  - CLI commands via CliSilentProxy run > 5s",
                "  - Search/query ops run > 10s",
                "  (These produce mechanical messages unless you opted in with _meta:{canNotify:true})",
                "",
                "You MUST call notify() explicitly for:",
                "  - Any analysis, review, summary, or reasoning result the user should act on",
                "  - Plan complete / waiting for user approval -> use dismissAfterMs:0 (sticky)",
                "  - Error conditions you detected logically (not via a slow tool)",
                "  - Any async result the user should know about regardless of tool runtime",
                "",
                "DO NOT call notify() for:",
                "  - Synchronous steps the user sees in your reply in real time",
                "  - Trivial read-only lookups (ls, grep, ping, file reads)",
                "  - Every micro-step of a multi-step task -- see AGGREGATION",
                "",
                "=== NOTIFICATION QUALITY ===",
                "Write for a human who just woke up their screen after 10 minutes away.",
                "  GOOD: \"dotnet build succeeded (12s, 0 errors, 3 warnings)\"",
                "  GOOD: \"3 tests failed, 47 passed (8s) -- NullRef in UserService.cs:142\"",
                "  GOOD: \"Code review complete -- 2 correctness bugs found, 1 security issue\"",
                "  BAD:  \"CliSilentProxy: run completed in 8.2s\"",
                "  BAD:  \"Tool execution finished\"",
                "  BAD:  \"exit code 0\"",
                "",
                "Include: what was done, outcome, duration, key metrics (error count, test results).",
                "Exclude: internal tool names, raw exit codes, MCP server names.",
                "",
                "=== AGGREGATION ===",
                "Multi-step tasks (build -> test -> lint -> deploy): notify ONCE at the end.",
                "  \"Build + test + lint complete (43s): build succeeded, 12 tests passed, lint clean\"",
                "",
                "Exception: notify IMMEDIATELY on failure. Don't wait for later steps -- the user",
                "should know right away so they can decide whether to abort.",
                "  \"Build failed (6s) -- CS0246: type 'ITempoWork' not found in WorkQueue.cs:18\"",
                "",
                "Plan complete / waiting for input: notify immediately with dismissAfterMs:0 (sticky)",
                "so the alert persists until they return.",
                "",
                "=== RATE LIMITING ===",
                "Max 3 notifications per 60 seconds. When rate-limited, the response includes",
                "_notify_cooldown_ms (ms until next notification is allowed).",
                "If rate-limited: aggregate pending results into one notification rather than",
                "dropping silently. \"Completed 3 tasks: build succeeded, tests passed, lint clean.\"",
                "Do NOT silently drop a notification without checking for _notify_cooldown_ms.",
                "",
                "=== TOOLS ===",
                "notify(title, message, sender, task?, type?, sound?, dismissAfterMs?, timeoutMs?)",
                "  Send a desktop toast. Use for cognitive work completions and opted-in slow ops.",
                "",
                "notify_on_complete(command, args?, workDir?, title?, message?, sender, task?, type?, sound?, timeoutMs?)",
                "  Use when you need a custom message for a shell command, or when running commands",
                "  outside CliSilentProxy (background scripts, direct invocations). Returns instantly",
                "  with _pid; fires toast when the process exits. Not needed for CliSilentProxy.run()",
                "  commands -- those auto-notify unless you want a custom message.",
                "  WARNING: This is fire-and-forget. After calling it, you CANNOT check if the process",
                "  succeeded or failed. If you need the exit code or output, use CliSilentProxy.run()",
                "  instead, which blocks until completion and returns structured results.",
                "  Use notify_on_complete only for processes you want to launch independently (e.g.,",
                "  a background script you don't need to monitor from this session).",
                "",
                "=== TYPE SELECTION ===",
                "type:\"error\"  -- build failures, test failures, unexpected errors, crash",
                "type:\"info\"   -- success, completion, status updates (default)",
                "",
                "=== PARAMETERS ===",
                "dismissAfterMs:",
                "  0 (sticky) -- errors, failures, plan-complete / waiting-for-input",
                "  default (5000) -- success, informational completions",
                "sound: true (default) -- set false only for silent/background environments",
                "",
                "=== PLATFORM NOTES ===",
                "Windows: NotifierHelper (Avalonia) or PowerShell WinRT fallback",
                "macOS: osascript + afplay (Ping.aiff / Basso.aiff)",
                "Linux: notify-send",
                "Notifications are fire-and-forget. Check _sent in response. If false, print the",
                "message as fallback text.",
                "",
                "=== COMPACT FIELDS ===",
                "_sent=notification delivered  _pid=child process ID  _status=running|failed",
                "_fallback=suggestion when notification fails  _notify_cooldown_ms=ms until next allowed",
                "_shouldNotify=true when opt-in threshold was exceeded (requires _meta:{canNotify:true})",
                "",
                "=== _meta PARAMS (any tool call) ===",
                "Pass _meta in the tool call params object to opt in to advanced features:",
                "  _meta:{canNotify:true}  — server injects _shouldNotify:true when threshold exceeded;",
                "                            YOU then call notify() with a human message.",
                "  _meta:{sender:\"Claude\"} — LLM identity for auto-notifications (default: server name).",
                "  _meta:{project:\"name\"}  — project name surfaced in auto-notification body.",
                "  _meta:{progressToken:N} — enables $/progress notifications during long ops.",
            ],
        };
    }

}
