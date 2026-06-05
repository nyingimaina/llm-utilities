using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace NotifierHelper;

static class WindowActivator
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static void ActivateByTitle(string fragment)
    {
        IntPtr target = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (target != IntPtr.Zero) return false;
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, 512);
            if (IsWindowVisible(hWnd) && sb.Length > 0
                && sb.ToString().IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                target = hWnd;
            return true;
        }, IntPtr.Zero);

        if (target != IntPtr.Zero)
            _ = SetForegroundWindow(target);
    }
}

public record NotificationData
{
    [JsonPropertyName("server")] public string Server { get; init; } = "";
    [JsonPropertyName("llmClient")] public string? LlmClient { get; init; }
    [JsonPropertyName("tool")] public string Tool { get; init; } = "";
    [JsonPropertyName("argsJson")] public string? ArgsJson { get; init; }
    [JsonPropertyName("argsDisplay")] public string? ArgsDisplay { get; init; }
    [JsonPropertyName("project")] public string? Project { get; init; }
    [JsonPropertyName("cwd")] public string? Cwd { get; init; }
    [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; init; }
    [JsonPropertyName("groupCount")] public int? GroupCount { get; init; }
    [JsonPropertyName("grouped")] public List<NotificationData>? Grouped { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("resultHint")] public string? ResultHint { get; init; }
}

public partial class ToastWindow : Window
{
    static readonly string StateDir = Path.Combine(Path.GetTempPath(), "LLMUtilities");
    static readonly string StatePath = Path.Combine(StateDir, "notifier-stack.json");
    static readonly Mutex StateMutex = new(false, "LLMUtilities_NotifierStack");
    static readonly int BottomMargin = 48;
    static readonly int Gap = 8;
    static int _nextId;

    readonly NotificationData? _data;
    readonly string _type;
    readonly bool _playSound;
    readonly int _dismissMs;
    readonly string? _sender;
    readonly int _myId;
    int _mySlot = -1;
    double _myHeight;
    bool _closing;
    IDisposable? _pollTimer;

    static readonly Dictionary<string, Color> LlmColors = new()
    {
        ["Claude Code"] = Color.FromArgb(0xFF, 0x8B, 0x5C, 0xF6),
        ["Gemini CLI"] = Color.FromArgb(0xFF, 0x42, 0x85, 0xF4),
        ["OpenCode"] = Color.FromArgb(0xFF, 0x10, 0xB9, 0x81),
        ["Cursor"] = Color.FromArgb(0xFF, 0x1E, 0x29, 0x3B),
        ["Windsurf"] = Color.FromArgb(0xFF, 0x0E, 0xA5, 0xE9),
        ["Zed"] = Color.FromArgb(0xFF, 0xEA, 0xB3, 0x08),
    };

    static readonly Color DefaultAccent = Color.FromArgb(0xFF, 0x6B, 0x72, 0x80);

    [DllImport("winmm.dll", SetLastError = true)]
    static extern bool PlaySound(byte[] pszSound, IntPtr hmod, uint fdwSound);

    const uint SND_MEMORY = 0x00000004;
    const uint SND_SYNC = 0x00000000;

    // ── Constructors ─────────────────────────────────────────────────────────

    public ToastWindow(string dataJson)
    {
        InitializeComponent();
        NotificationData? parsed = null;
        try { parsed = JsonSerializer.Deserialize<NotificationData>(dataJson); } catch { }

        if (parsed is not null)
        {
            _data = parsed;
            _type = parsed.Ok ? "info" : "error";
            _playSound = true;
            _dismissMs = 8000;
            _sender = parsed.LlmClient ?? parsed.Server;
            _myId = Interlocked.Increment(ref _nextId);

            PostInit();
            RenderFromData();
        }
        else
        {
            // Fallback: parse unknown data as generic notification
            _data = null;
            _type = "info";
            _playSound = true;
            _dismissMs = 8000;
            _sender = "LLM Utilities";
            _myId = Interlocked.Increment(ref _nextId);

            PostInit();
            MessageText.Text = "Notification";
            if (dataJson.Length <= 200)
                MessageText.Text += "\n" + dataJson;
        }

        ScheduleDismiss();
    }

    void ScheduleDismiss()
    {
        if (_dismissMs <= 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(_dismissMs);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_closing) Close();
            });
        });
    }

    // Legacy flat args constructor
    public ToastWindow(string title, string message, string type, bool playSound, int dismissMs,
        string? sender = null, string? task = null)
    {
        InitializeComponent();

        _data = null;
        _type = type;
        _playSound = playSound;
        _dismissMs = dismissMs;
        _sender = sender;
        _myId = Interlocked.Increment(ref _nextId);

        PostInit();

        if (!string.IsNullOrEmpty(sender))
        {
            SenderLabel.Text = sender;
            SenderLabel.IsVisible = true;
            ViewMoreText.IsVisible = true;
        }

        MessageText.Text = title + "\n" + message;
    }

    void PostInit()
    {
        Card.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1E, 0x1E, 0x2E));
        ConfigureForType();
        _pollTimer = DispatcherTimer.Run(PollReflow, TimeSpan.FromMilliseconds(600));
        PositionWindow();
    }

    // ── Render from structured data ─────────────────────────────────────────

    void RenderFromData()
    {
        if (_data is null) return;

        var accent = DefaultAccent;
        if (_data.LlmClient is not null && LlmColors.TryGetValue(_data.LlmClient, out var c))
            accent = c;
        AccentStrip.Background = new SolidColorBrush(accent);

        var senderText = _data.LlmClient is not null
            ? $"{_data.Server}  •  {_data.LlmClient}"
            : _data.Server;
        SenderLabel.Text = senderText;
        SenderLabel.IsVisible = true;

        // Tool line
        var toolDisplay = _data.ArgsDisplay ?? _data.Tool;
        if (!string.IsNullOrEmpty(toolDisplay))
        {
            ToolLine.Text = toolDisplay;
            ToolLine.IsVisible = true;
        }

        // Project line
        if (!string.IsNullOrEmpty(_data.Project))
        {
            ProjectLine.Text = _data.Project;
            ProjectLine.IsVisible = true;
        }

        // Handle grouped vs single
        if (_data.Grouped is { Count: > 0 })
        {
            RenderGrouped();
        }
        else
        {
            RenderSingle();
        }

        ViewMoreText.IsVisible = true;
    }

    void RenderGrouped()
    {
        if (_data?.Grouped is null) return;

        var elapsed = _data.ElapsedMs / 1000.0;
        var status = _data.Ok ? "✓" : "✗";
        var time = elapsed < 60 ? $"{elapsed:F1}s" : $"{elapsed / 60:F1}m";
        MessageText.Text = $"{_data.Summary ?? $"{_data.Server}: {_data.Grouped.Count} tools"}  ({time})";
        MessageText.IsVisible = true;

        // Render grouped items
        foreach (var item in _data.Grouped)
        {
            var itemText = item.ArgsDisplay ?? item.Tool;
            var itemTime = item.ElapsedMs / 1000.0;
            var itemStatus = item.Ok ? "✓" : "✗";
            var timeStr = itemTime < 60 ? $"{itemTime:F1}s" : $"{itemTime / 60:F1}m";

            var tb = new TextBlock
            {
                Text = $"  {itemStatus} {itemText}  ·  {timeStr}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 1, 0, 0),
            };

            if (item.Error is not null)
                tb.Text += $"  — {item.Error}";

            if (!item.Ok)
                tb.Foreground = new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0x6B, 0x6B));

            GroupedList.Children.Add(tb);
        }

        GroupedList.IsVisible = true;
    }

    void RenderSingle()
    {
        if (_data is null) return;

        var elapsed = _data.ElapsedMs / 1000.0;
        var timeStr = elapsed < 60 ? $"{elapsed:F1}s" : $"{elapsed / 60:F1}m";
        var status = _data.Ok ? '✓' : '✗';
        var elapsedText = $"{status} {timeStr}";

        if (_data.ResultHint is not null && _data.ResultHint.Length > 0)
        {
            var hint = _data.ResultHint;
            if (hint.Length > 100) hint = hint[..100] + "...";
            MessageText.Text = $"{elapsedText}  —  {hint}";
        }
        else if (_data.Error is not null)
        {
            var err = _data.Error.Length > 100 ? _data.Error[..100] + "..." : _data.Error;
            MessageText.Text = $"{elapsedText}  —  {err}";
        }
        else
        {
            MessageText.Text = elapsedText;
        }

        MessageText.IsVisible = true;
    }

    // ── Sound generation ────────────────────────────────────────────────────

    void ConfigureForType()
    {
        if (_type == "error")
        {
            AccentStrip.Background ??= new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
        }
        else
        {
            AccentStrip.Background ??= new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x90, 0xD9));
        }

        if (_playSound)
            PlayNotificationSound(_type);
    }

    void PlayNotificationSound(string type)
    {
        try
        {
            int hz1 = type == "error" ? 660 : 880;
            int ms1 = type == "error" ? 25 : 50;
            int hz2 = type == "error" ? 440 : 0;
            int ms2 = type == "error" ? 25 : 0;

            var sampleRate = 44100;
            var totalSamples = sampleRate * (ms1 + ms2) / 1000;
            var wav = new byte[44 + totalSamples * 2];

            // WAV header
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("RIFF"), 0, wav, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(36 + totalSamples * 2), 0, wav, 4, 4);
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("WAVE"), 0, wav, 8, 4);
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("fmt "), 0, wav, 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(16), 0, wav, 16, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((short)1), 0, wav, 20, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((short)1), 0, wav, 22, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(sampleRate), 0, wav, 24, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(sampleRate * 2), 0, wav, 28, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((short)2), 0, wav, 32, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((short)16), 0, wav, 34, 2);
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("data"), 0, wav, 36, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(totalSamples * 2), 0, wav, 40, 4);

            var idx = 44;
            var vol = (int)(short.MaxValue * 0.3);
            var samples1 = sampleRate * ms1 / 1000;
            var samples2 = sampleRate * ms2 / 1000;

            for (int i = 0; i < samples1 + samples2; i++)
            {
                short sample = 0;
                if (i < samples1)
                {
                    var t = (double)i / sampleRate;
                    sample = (short)(vol * Math.Sin(2 * Math.PI * hz1 * t));
                }
                else if (hz2 > 0)
                {
                    var t = (double)(i - samples1) / sampleRate;
                    sample = (short)(vol * Math.Sin(2 * Math.PI * hz2 * t));
                }
                wav[idx++] = (byte)(sample & 0xFF);
                wav[idx++] = (byte)((sample >> 8) & 0xFF);
            }

            if (OperatingSystem.IsWindows())
                PlaySound(wav, IntPtr.Zero, SND_MEMORY | SND_SYNC);
        }
        catch { /* best-effort */ }
    }

    // ── Stack positioning ───────────────────────────────────────────────────

    void PositionWindow()
    {
        try
        {
            var screen = Screens.Primary;
            if (screen is null) return;

            var wa = screen.WorkingArea;
            var x = wa.Right - 400;
            var y = wa.Bottom - BottomMargin;

            // Read stack state
            var slots = ReadSlots();
            _mySlot = slots.Count;

            foreach (var s in slots)
            {
                if (!s.Closed)
                    y -= (int)(s.Height + Gap);
            }

        y = Math.Max(wa.Y + 20, y);

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)(x / screen.Scaling), (int)(y / screen.Scaling));
        }
        catch { /* best-effort positioning */ }
    }

    void OnInitializedCore()
    {
        _myHeight = Bounds.Height;
        WriteSlot(_myId, _mySlot, _myHeight, false);
        AnimateIn();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(OnInitializedCore, DispatcherPriority.Background);
    }

    void AnimateIn()
    {
        // Simplified: just ensure visible at correct position
        Opacity = 1;
    }

    bool PollReflow()
    {
        if (_closing) return false;

        var slots = ReadSlots();
        var myIndex = slots.FindIndex(s => s.Id == _myId);
        if (myIndex < 0) return true;

        // Recalculate my position
        var screen = Screens.Primary;
        if (screen is null) return true;

        var wa = screen.WorkingArea;
        var x = wa.Right - 400;
        var y = wa.Bottom - BottomMargin;

        var earlierHeight = 0.0;
        for (int i = 0; i < myIndex; i++)
        {
            if (!slots[i].Closed)
                earlierHeight += (int)(slots[i].Height + Gap);
        }

        y -= (int)earlierHeight;
        y = Math.Max(wa.Y + 20, y);

        var targetX = (int)(x / screen.Scaling);
        var targetY = (int)(y / screen.Scaling);

        if (Position.X != targetX || Position.Y != targetY)
            Position = new PixelPoint(targetX, targetY);

        // Update my height
        var currentHeight = Bounds.Height;
        if (Math.Abs(currentHeight - _myHeight) > 1)
        {
            _myHeight = currentHeight;
            WriteSlot(_myId, _mySlot, currentHeight, false);
        }

        return true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _closing = true;
        _pollTimer?.Dispose();
        WriteSlot(_myId, _mySlot, _myHeight, true);
        base.OnClosing(e);
    }

    // ── Slot persistence ────────────────────────────────────────────────────

    record SlotInfo(int Id, int Index, double Height, bool Closed);

    List<SlotInfo> ReadSlots()
    {
        try
        {
            StateMutex.WaitOne(3000);
            try
            {
                if (File.Exists(StatePath))
                {
                    var json = File.ReadAllText(StatePath);
                    return JsonSerializer.Deserialize<List<SlotInfo>>(json) ?? [];
                }
            }
            finally { StateMutex.ReleaseMutex(); }
        }
        catch { }
        return [];
    }

    void WriteSlot(int id, int slot, double height, bool closed)
    {
        try
        {
            StateMutex.WaitOne(3000);
            try
            {
                Directory.CreateDirectory(StateDir);
                var slots = File.Exists(StatePath)
                    ? JsonSerializer.Deserialize<List<SlotInfo>>(File.ReadAllText(StatePath)) ?? []
                    : [];
                slots.RemoveAll(s => s.Id == id);
                if (!closed)
                    slots.Add(new SlotInfo(id, slot, height, false));
                File.WriteAllText(StatePath, JsonSerializer.Serialize(slots));
            }
            finally { StateMutex.ReleaseMutex(); }
        }
        catch { }
    }

    // ── Click handler ────────────────────────────────────────────────────────

    void OnCardClicked(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        // Try to activate sender window
        if (_data?.LlmClient is not null)
            WindowActivator.ActivateByTitle(_data.LlmClient);
        else if (_sender is not null)
            WindowActivator.ActivateByTitle(_sender);

        _ = Dispatcher.UIThread.InvokeAsync(() => Close());
    }
}
