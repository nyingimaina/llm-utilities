using System.Diagnostics;
using System.Text.Json;
using LLMUtilities.Commons;
using Serilog;

namespace Notifier;

sealed class NotifierServer : McpServerBase
{
    static readonly string HelperPath = Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "NotifierHelper.exe" : "NotifierHelper");

    public NotifierServer(string? configPath = null) : base(new McpServerConfig
    {
        Name = "Notifier",
        Version = GetEntryVersion(),
        AnnouncementDirective = "Desktop notification service: notify() for toast + notify_on_complete() for process completion alerts. Use for long-running tasks and cognitive work milestones.",
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
                throw new McpErrorException("{\"error\":\"Tool not found: " + name + "\"}");
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
                var exited = proc!.WaitForExit(timeoutMs ?? Timeout.Infinite);
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
                using var proc = Process.Start(helperPsi);
                proc?.WaitForExit(3000);
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

    static Process StartDetached(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi)!;
    }

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
        var psScript = $@"
Add-Type -AssemblyName System.Runtime.WindowsRuntime;
$null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime];
$template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);
$textNodes = $template.GetElementsByTagName('text');
$textNodes.Item(0).AppendChild($template.CreateTextNode('{EscapePs(title)}')) > $null;
$textNodes.Item(1).AppendChild($template.CreateTextNode('{EscapePs(message)}')) > $null;
{(sound ? "$audio = $template.CreateElement('audio'); $audio.SetAttribute('src', if ('{type}' -eq 'error'){{'ms-winsoundevent:Notification.Critical'}}else{{'ms-winsoundevent:Notification.Default'}}); $template.GetElementsByTagName('toast').Item(0).AppendChild($audio);" : "")}
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('LLM Utilities').Show($template);
";
        StartDetached("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "\\\"")}\"");
        return null;
    }

    static string? SendMacNotification(string title, string message, string type, bool sound)
    {
        var script = $"display notification \"{EscapeOsx(message)}\" with title \"{EscapeOsx(title)}\"";
        if (sound)
        {
            var soundFile = type == "error" ? "Basso" : "Ping";
            StartDetached("afplay", $"/System/Library/Sounds/{soundFile}.aiff");
        }
        StartDetached("osascript", $"-e \"{EscapeOsx(script)}\"");
        return null;
    }

    static string? SendLinuxNotification(string title, string message, string type, bool sound)
    {
        var urgency = type == "error" ? "critical" : "normal";
        var args = $"-u {urgency} -t 5000 \"{EscapeShell(title)}\" \"{EscapeShell(message)}\"";
        if (sound)
            args = $"-u {urgency} -t 5000 \"{EscapeShell(title)}\" \"{EscapeShell(message)}\"";
        StartDetached("notify-send", args);
        return null;
    }

    static string EscapePs(string s) => s.Replace("'", "''");
    static string EscapeOsx(string s) => s.Replace("\"", "\\\"").Replace("'", "'\\''");
    static string EscapeShell(string s) => s.Replace("\"", "\\\"");

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
            ],
        };
    }

    static void Main(string[] args)
    {
        string? configPath = null;
        string? logDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mcp": break;
                case "--config-path" when i + 1 < args.Length: configPath = args[++i]; break;
                case "--log-dir" when i + 1 < args.Length: logDir = args[++i]; break;
                default:
                    Console.Error.WriteLine("Usage: Notifier.exe --mcp [--config-path <path>] [--log-dir <dir>]");
                    Environment.Exit(1);
                    return;
            }
        }

        logDir ??= ConfigPaths.LogDir("Notifier");
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDir, "Notifier.log"),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            configPath ??= ConfigPaths.ConfigFile("Notifier");
            new NotifierServer(configPath).Run(Console.In, Console.Out);
        }
        catch (Exception ex) { Log.Fatal(ex, "Unhandled exception"); }
        finally { Log.CloseAndFlush(); }
    }
}
