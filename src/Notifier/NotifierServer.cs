using System.Diagnostics;
using System.Text.Json;
using LLMUtilities.Commons;

namespace Notifier;

sealed class NotifierServer : McpServerBase
{
    static readonly string HelperPath = Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "NotifierHelper.exe" : "NotifierHelper");

    public NotifierServer() : base(new McpServerConfig
    {
        Name = "Notifier",
        Version = GetEntryVersion(),
        InstructionsToolDescription = "Returns compact instructions for desktop notifications. Cache in session context.",
    })
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
            Description = "MANDATORY: Call this to alert the user about results YOU produced that aren't covered by auto-notification (e.g. code review done, analysis complete, important logical error). Auto-notification already handles slow CLI commands (>5s), searches, and queries. Use notify for YOUR cognitive work — analysis, review, reasoning results the user should know about. DO NOT notify for every trivial synchronous step or micro-operation the user can see happening in real time.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string", description = "Notification title. Be specific about what completed." },
                    message = new { type = "string", description = "Notification body. Include outcome (e.g. '3 tests failed, 12 passed')." },
                    sender = new { type = "string", description = "Your identity (e.g. 'Claude', 'Gemini', 'Cursor'). MANDATORY: always identify yourself so the user knows which LLM sent this." },
                    task = new { type = "string", description = "What you were working on (e.g. 'Code review', 'Build fix-123', 'Refactoring auth'). Helps user context-switch instantly." },
                    type = new { type = "string", description = "info (default) / error — controls accent colour and sound", @default = "info" },
                    sound = new { type = "boolean", description = "Play notification sound (default true)", @default = true },
                    dismissAfterMs = new { type = "integer", description = "Auto-dismiss after N ms (default 5000). 0 = sticky." },
                    timeoutMs = new { type = "integer", description = "Max wait in ms (max 60000). Default 30000." }
                },
                required = new[] { "title", "message", "sender" }
            }
        },
        new McpTool
        {
            Name = "notify_on_complete",
            Description = "PREFERRED over CliSilentProxy.run alone: wraps a shell command so a notification fires when it finishes. Use for ANY subprocess that takes >3s: builds, tests, deploys, batch operations, install scripts. Returns immediately so you keep working. Type auto-sets to 'error' when exit code != 0. MANDATORY: always use this instead of bare run() when the command runs unattended.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "Executable path or name" },
                    args = new { type = "array", items = new { type = "string" }, description = "Command arguments (optional)" },
                    workDir = new { type = "string", description = "Working directory (optional)" },
                    title = new { type = "string", description = "Notification title. Default: 'Command Complete'. Make it specific like 'Build complete' or 'Tests done'." },
                    message = new { type = "string", description = "Custom message template. Default: uses exit code + duration." },
                    sender = new { type = "string", description = "Your identity (e.g. 'Claude'). MANDATORY: always identify yourself." },
                    task = new { type = "string", description = "What you were doing (e.g. 'Running tests'). Helps user context-switch." },
                    type = new { type = "string", description = "info (default) / error. Auto-set to error if exit code != 0.", @default = "info" },
                    sound = new { type = "boolean", description = "Play notification sound (default true)", @default = true },
                    timeoutMs = new { type = "integer", description = "Max wait in ms (max 600000 / 10min). No default = waits indefinitely." }
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
        var instr = new List<string>
        {
            "=== SERVER ===",
            "Notifier — cross-platform desktop notification MCP server",
            "",
            "=== TOOLS ===",
            "notify(title, message, sender, task?, type?, sound?, dismissAfterMs?, timeoutMs?)",
            "  Send a desktop notification (slide-in toast with frosted glass).",
            "  Use for: non-server tasks you completed (code review, analysis), important errors",
            "  you discovered logically, async results the user should know about.",
            "  NOTE: Slow server ops (CLI >5s, search/query >10s) auto-fire notifications",
            "  via Commons — you don't need to call notify for those. Call notify explicitly",
            "  for YOUR OWN work (analysis, review, reasoning) that the user should be aware of.",
            "  MANDATORY: always provide 'sender' (your name) so the user knows which LLM.",
            "",
            "notify_on_complete(command, args?, workDir?, title?, message?, sender, task?, type?, sound?, timeoutMs?)",
            "  Fire-and-forget: wraps command execution + notification on exit.",
            "  Returns {_pid, _status:'running'} instantly. Background thread fires toast when done.",
            "  NOTE: For most CLI commands, CliSilentProxy.run() already auto-notifies (>5s).",
            "  Use notify_on_complete when you need a custom message or when running commands",
            "  through a mechanism that bypasses CliSilentProxy.",
            "  MANDATORY: always provide 'sender' (your name).",
            "",
            "=== WHEN TO NOTIFY (MANDATORY RULES) ===",
            "  NOTIFICATIONS ARE AUTO-FIRED for slow server operations (>5s CLI, >10s search/query).",
            "  You do NOT need to call notify for those — Commons handles it transparently.",
            "",
            "  However, you STILL must explicitly call notify for:",
            "  - Non-server tasks you completed (code review, analysis, summary prepared)",
            "  - Any async result the user should know about even if no tool took >5s",
            "  - Error conditions you detected logically (not via server timeout)",
            "",
            "  IDENTIFY YOURSELF — always provide 'sender' (e.g. 'Claude', 'Gemini', 'OpenCode').",
            "  The user runs multiple LLMs side-by-side and needs to know who is notifying.",
            "  Include 'task' to describe what you were doing for instant context-switching.",
            "",
            "  DO NOT notify when:",
            "  - The operation completes synchronously and the user sees the result in your reply",
            "  - For every micro-step of a larger task (aggregate notifications only)",
            "  - The user is actively reading your response (they'll see the text)",
            "  - Trivial read-only lookups (ls, grep, ping)",
            "",
            "=== TYPE SELECTION ===",
            "  type: 'error' — build failures, test failures, unexpected errors, crash",
            "  type: 'info' — success, completion, status updates",
            "  notify_on_complete auto-sets type to 'error' when exit code != 0.",
            "",
            "=== UI ===",
            "Notifications stack upward from bottom-right. When a lower one dismisses,",
            "  those above animate down to fill the gap (reflow). Each notification",
            "  auto-dismisses after dismissAfterMs (default 5000ms, 0 = sticky).",
            "  Fade-out: 400ms ease-in opacity drop with upward drift.",
            "",
            "=== SOUND ===",
            "Windows: WinRT toast chime (info) / critical alert (error)",
            "macOS: Ping.aiff (info) / Basso.aiff (error)",
            "Linux: notify-send default sound (info) / critical (error)",
            "Custom embedded WAVs when NotifierHelper.exe is available.",
            "",
            "=== CROSS-PLATFORM ===",
            "Windows: NotifierHelper (Avalonia) or PowerShell WinRT fallback",
            "macOS: osascript + afplay",
            "Linux: notify-send",
            "",
            "=== BEST EFFORT ===",
            "Notifications are fire-and-forget. Check _sent field in response.",
            "If notification fails, _fallback suggests printing the message instead.",
            "",
        };
        instr.AddRange(GetResiliencySection());
        instr.Add("");
        instr.AddRange(GetSelfImprovementSection());
        instr.Add("");
        instr.Add("=== COMPACT FIELDS ===");
        instr.Add("_sent = notification delivered   _pid = child process ID   _status = running|failed");
        instr.Add("_fallback = suggestion when notification fails");
        return new { _h = instr.ToArray() };
    }

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--mcp")
        {
            new NotifierServer().Run(Console.In, Console.Out);
            return;
        }
        Console.Error.WriteLine("Usage: Notifier.exe --mcp");
        Environment.Exit(1);
    }
}
