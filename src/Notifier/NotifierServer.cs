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
            Description = "Send a desktop notification. Works cross-platform (Windows/ macOS/ Linux). Slide-in animation, frosted glass UI, dopamine arrival effect. Use for one-shot alerts or confirmations.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string", description = "Notification title" },
                    message = new { type = "string", description = "Notification body text" },
                    type = new { type = "string", description = "info (default) / error — controls sound and accent colour", @default = "info" },
                    sound = new { type = "boolean", description = "Play notification sound (default true)", @default = true },
                    dismissAfterMs = new { type = "integer", description = "Auto-dismiss after N ms (default 5000). 0 = sticky." },
                    timeoutMs = new { type = "integer", description = "Max wait in ms (max 60000). Default 30000." }
                },
                required = new[] { "title", "message" }
            }
        },
        new McpTool
        {
            Name = "notify_on_complete",
            Description = "Fire-and-forget: run a shell command, notify when done. Returns immediately with _pid. Notification fires on exit with exit code + elapsed time. Spawns child process and waits in background.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "Executable path or name" },
                    args = new { type = "array", items = new { type = "string" }, description = "Command arguments (optional)" },
                    workDir = new { type = "string", description = "Working directory (optional)" },
                    title = new { type = "string", description = "Notification title. Default: 'Command Complete'." },
                    message = new { type = "string", description = "Custom message template. Default: uses exit code + duration." },
                    type = new { type = "string", description = "info (default) / error. Auto-set to error if exit code != 0.", @default = "info" },
                    sound = new { type = "boolean", description = "Play notification sound (default true)", @default = true },
                    timeoutMs = new { type = "integer", description = "Max wait in ms (max 600000 / 10min). Default polling interval." }
                },
                required = new[] { "command" }
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
        var type = GetString(args, "type") ?? "info";
        var sound = GetBool(args, "sound") ?? true;
        var dismissAfterMs = GetInt(args, "dismissAfterMs") ?? 5000;

        var err = SendNotification(title, message, type, sound, dismissAfterMs);
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
                    SendNotification(title, $"Timed out after {timeoutMs}ms", "error", sound, 5000);
                    return;
                }

                var code = proc.ExitCode;
                var elapsed = sw.Elapsed;
                var finalType = code == 0 ? type : "error";
                var finalMsg = messageTemplate
                    ?? $"Exit code: {code} (elapsed: {elapsed.TotalSeconds:F1}s)";
                SendNotification(title, finalMsg, finalType, sound, 5000);
            }
            catch { /* background fire-and-forget — best-effort */ }
        });

        return new { _pid = pid, _status = "running" };
    }

    string? SendNotification(string title, string message, string type, bool sound, int dismissAfterMs)
    {
        var (okType, okSound) = NormalizeType(type);

        if (File.Exists(HelperPath))
        {
            try
            {
                var helperPsi = new ProcessStartInfo
                {
                    FileName = HelperPath,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                };
                helperPsi.ArgumentList.Add("--title");
                helperPsi.ArgumentList.Add(title);
                helperPsi.ArgumentList.Add("--message");
                helperPsi.ArgumentList.Add(message);
                helperPsi.ArgumentList.Add("--type");
                helperPsi.ArgumentList.Add(okType);
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
            UseShellExecute = true,
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
            "notify(title, message, type?, sound?, dismissAfterMs?, timeoutMs?) — one-shot notification",
            "  type: info|error. sound: bool (default true). dismissAfterMs: auto-close (default 5000).",
            "  Returns {_sent:true} or error with _fallback.",
            "",
            "notify_on_complete(command, args?, workDir?, title?, message?, type?, sound?, timeoutMs?)",
            "  Fire-and-forget: spawns command, returns {_pid, _status:'running'} immediately.",
            "  Background task waits for exit, then fires notification with exit code + elapsed time.",
            "  title default: 'Command Complete'. type auto-set to 'error' if exit code != 0.",
            "  timeoutMs: max wait for child process (no default = wait indefinitely).",
            "",
            "=== UI ===",
            "Notifications slide in from bottom-right with frosted-glass acrylic effect.",
            "  Info: blue accent strip, soft tick sound.",
            "  Error: red accent strip, shake animation, two-tone error sound.",
            "  Completion: checkmark pop-in with green accent.",
            "  Auto-dismiss: fade-out + upward drift.",
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
