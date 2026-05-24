using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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

public partial class ToastWindow : Window
{
    static readonly string StateDir = Path.Combine(Path.GetTempPath(), "LLMUtilities");
    static readonly string StatePath = Path.Combine(StateDir, "notifier-stack.json");
    static readonly Mutex StateMutex = new(false, "LLMUtilities_NotifierStack");
    static readonly int BottomMargin = 48;
    static readonly int Gap = 8;
    static int _nextId;

    readonly string _type;
    readonly bool _playSound;
    readonly int _dismissMs;
    readonly string? _sender;
    readonly int _myId;
    int _mySlot = -1;
    double _myHeight;
    bool _closing;
    IDisposable? _pollTimer;

    // ── Sound: winmm.dll on Windows ───────────────────────────────────────────

    [DllImport("winmm.dll", SetLastError = true)]
    static extern bool PlaySound(byte[] pszSound, IntPtr hmod, uint fdwSound);

    const uint SND_MEMORY = 0x00000004;
    const uint SND_SYNC = 0x00000000;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ToastWindow(string title, string message, string type, bool playSound, int dismissMs,
        string? sender = null, string? task = null)
    {
        InitializeComponent();

        _type = type;
        _playSound = playSound;
        _dismissMs = dismissMs;
        _sender = sender;
        _myId = Interlocked.Increment(ref _nextId);

        if (!string.IsNullOrEmpty(sender))
        {
            SenderLabel.Text = sender;
            SenderLabel.IsVisible = true;
            ViewMoreText.IsVisible = true;
        }

        TitleText.Text = title;
        MessageText.Text = message;

        Card.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1E, 0x1E, 0x2E));

        ConfigureForType();
        _pollTimer = DispatcherTimer.Run(PollReflow, TimeSpan.FromMilliseconds(600));

        PositionChanged += OnScreenChanged;
        Opened += OnOpened;
    }

    void OnCardClicked(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_sender))
            ActivateSender();
        _ = Dispatcher.UIThread.InvokeAsync(() => Close());
    }

    void ActivateSender()
    {
        if (string.IsNullOrEmpty(_sender)) return;

        try
        {
            if (OperatingSystem.IsWindows())
                WindowActivator.ActivateByTitle(_sender);
            else if (OperatingSystem.IsMacOS())
                Process.Start("osascript", $"-e \"tell app \"{_sender}\" to activate\"");
            else if (OperatingSystem.IsLinux())
                Process.Start("wmctrl", $"-a \"{_sender}\"");
        }
        catch { }
    }

    void ConfigureForType()
    {
        if (_type == "error")
        {
            AccentStrip.Background = new SolidColorBrush(Color.Parse("#FF5252"));
            IconText.Foreground = new SolidColorBrush(Color.Parse("#FF5252"));
            IconText.Text = "\u2715";
        }
        else
        {
            AccentStrip.Background = new SolidColorBrush(Color.Parse("#4A9EFF"));
            IconText.Foreground = new SolidColorBrush(Color.Parse("#4CAF50"));
            IconText.Text = "\u2713";
        }
    }

    // ── Stack state ──────────────────────────────────────────────────────────

    record StackEntry(int Id, int Pid, int Slot, double Height);

    static List<StackEntry> ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return [];
            var text = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<List<StackEntry>>(text) ?? [];
        }
        catch { return []; }
    }

    static void WriteState(List<StackEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch { }
    }

    static bool IsProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    int ClaimSlot()
    {
        try { StateMutex.WaitOne(); }
        catch (AbandonedMutexException) { }

        try
        {
            var entries = ReadState();
            entries.RemoveAll(e => !IsProcessAlive(e.Pid));
            var taken = entries.Select(e => e.Slot).ToHashSet();
            int slot = 0;
            while (taken.Contains(slot)) slot++;

            entries.Add(new StackEntry(_myId, Environment.ProcessId, slot, _myHeight));
            WriteState(entries);
            return slot;
        }
        finally { StateMutex.ReleaseMutex(); }
    }

    void ReleaseSlot()
    {
        try { StateMutex.WaitOne(); }
        catch (AbandonedMutexException) { }

        try
        {
            var entries = ReadState();
            entries.RemoveAll(e => e.Id == _myId);
            var sorted = entries.OrderBy(e => e.Slot).ToList();
            for (int i = 0; i < sorted.Count; i++)
                sorted[i] = sorted[i] with { Slot = i };
            WriteState(sorted);
        }
        finally { StateMutex.ReleaseMutex(); }
    }

    static int? GetMySlot(List<StackEntry> entries, int myId) =>
        entries.FirstOrDefault(e => e.Id == myId)?.Slot;

    // ── Positioning ──────────────────────────────────────────────────────────

    void OnScreenChanged(object? sender, PixelPointEventArgs e)
    {
        Reposition(animate: false);
    }

    void Reposition(bool animate)
    {
        var screens = Screens;
        if (screens is null || screens.ScreenCount == 0) return;
        var screen = screens.Primary;
        if (screen is null) return;

        var scaling = screen.Scaling;
        var bounds = screen.Bounds;
        var x = (int)((bounds.X + bounds.Width) / scaling - Width - 16);
        var slot = _mySlot;
        var y = (int)((bounds.Y + bounds.Height) / scaling - BottomMargin - _myHeight - (_myHeight + Gap) * slot);

        var target = new PixelPoint((int)(x * scaling), (int)(y * scaling));

        if (animate && Position != target)
            _ = AnimateFall(target);
        else
            Position = target;
    }

    // ── Reflow ───────────────────────────────────────────────────────────────

    bool PollReflow()
    {
        if (_closing) return false;

        var entries = ReadState();
        var slot = GetMySlot(entries, _myId);

        if (slot.HasValue && slot.Value != _mySlot)
        {
            _mySlot = slot.Value;
            Reposition(animate: true);
        }
        return !_closing;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    async void OnOpened(object? sender, EventArgs e)
    {
        _myHeight = ClientSize.Height;
        _mySlot = ClaimSlot();
        Reposition(animate: false);

        if (_playSound) PlaySound();
        await AnimateSlideIn();

        if (_type == "error")
            await AnimateShake();

        if (_type != "error")
            await AnimateCheckmark();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closing && _dismissMs > 0)
        {
            _closing = true;
            e.Cancel = true;
            _ = AnimateClose();
        }
        base.OnClosing(e);
    }

    // ── Animations ───────────────────────────────────────────────────────────

    async Task AnimateSlideIn()
    {
        var transform = this.RenderTransform as TranslateTransform;
        if (transform is null) return;

        await AnimateProperty(t => transform.X = t, 400, -15, 180);
        await AnimateProperty(t => transform.X = t, -15, 5, 60);
        await AnimateProperty(t => transform.X = t, 5, 0, 40);
    }

    async Task AnimateShake()
    {
        var transform = this.RenderTransform as TranslateTransform;
        if (transform is null) return;

        var origX = transform.X;
        await AnimateProperty(t => transform.X = t, origX, origX - 10, 40);
        await AnimateProperty(t => transform.X = t, origX - 10, origX + 8, 40);
        await AnimateProperty(t => transform.X = t, origX + 8, origX - 5, 30);
        await AnimateProperty(t => transform.X = t, origX - 5, origX + 4, 30);
        await AnimateProperty(t => transform.X = t, origX + 4, origX, 20);
    }

    async Task AnimateCheckmark()
    {
        if (IconBox is null) return;
        await AnimateProperty(t =>
        {
            IconBox.Opacity = t;
            IconBox.RenderTransform = new ScaleTransform(t, t);
        }, 0, 1, 200);
    }

    async Task AnimateFall(PixelPoint target)
    {
        var startX = Position.X;
        var startY = Position.Y;
        var sw = Stopwatch.StartNew();
        var duration = 300;

        while (sw.ElapsedMilliseconds < duration)
        {
            var t = sw.ElapsedMilliseconds / (double)duration;
            t = 1 - Math.Pow(1 - t, 3);
            var x = (int)(startX + (target.X - startX) * t);
            var y = (int)(startY + (target.Y - startY) * t);
            Position = new PixelPoint(x, y);
            await Task.Delay(8);
        }
        Position = target;
    }

    async Task AnimateClose()
    {
        ReleaseSlot();

        var transform = this.RenderTransform as TranslateTransform;
        var startY = transform?.Y ?? 0;
        var sw = Stopwatch.StartNew();
        var duration = 400;

        while (sw.ElapsedMilliseconds < duration)
        {
            var t = sw.ElapsedMilliseconds / (double)duration;
            t = t * t;
            Opacity = 1 - t;
            if (transform is not null)
                transform.Y = startY - t * 15;
            await Task.Delay(8);
        }

        Opacity = 0;
        Close();
    }

    static async Task AnimateProperty(Action<double> setter, double from, double to, int durationMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            var t = sw.ElapsedMilliseconds / (double)durationMs;
            t = 1 - Math.Pow(1 - t, 3);
            var value = from + (to - from) * t;
            setter(value);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(8);
        }
        setter(to);
    }

    // ── Sound ────────────────────────────────────────────────────────────────

    void PlaySound()
    {
        if (!_playSound) return;
        var samples = _type == "error" ? GenerateErrorSound() : GenerateInfoSound();
        var wav = BuildWav(samples);

        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(() =>
            {
                try { PlaySound(wav, IntPtr.Zero, SND_MEMORY | SND_SYNC); }
                catch { }
            });
        }
        else
        {
            PlayViaFile(wav);
        }
    }

    void PlayViaFile(byte[] wav)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"notifier_{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(tempPath, wav);
            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "afplay",
                    Arguments = $"\"{tempPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(2000);
            }
            else if (OperatingSystem.IsLinux())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "aplay",
                    Arguments = $"\"{tempPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(2000);
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    static short[] GenerateInfoSound()
    {
        var sampleRate = 44100;
        var duration = 0.05;
        var count = (int)(sampleRate * duration);
        var result = new short[count];
        for (int i = 0; i < count; i++)
        {
            var t = i / (double)sampleRate;
            var envelope = 1.0 - (i / (double)count);
            result[i] = (short)(short.MaxValue * 0.3 * Math.Sin(2 * Math.PI * 880 * t) * envelope);
        }
        return result;
    }

    static short[] GenerateErrorSound()
    {
        var sampleRate = 44100;
        var tickDuration = 0.025;
        var count = (int)(sampleRate * tickDuration * 2);
        var result = new short[count];
        for (int i = 0; i < count; i++)
        {
            var t = i / (double)sampleRate;
            var envelope = 1.0 - (i / (double)count);
            var freq = i < count / 2 ? 660.0 : 440.0;
            result[i] = (short)(short.MaxValue * 0.25 * Math.Sin(2 * Math.PI * freq * t) * envelope);
        }
        return result;
    }

    static byte[] BuildWav(short[] samples)
    {
        var sampleRate = 44100;
        var bitsPerSample = 16;
        var channels = 1;
        var dataSize = samples.Length * 2;
        var wav = new byte[44 + dataSize];

        var fmt = bitsPerSample / 8;
        var byteRate = sampleRate * channels * fmt;
        var blockAlign = channels * fmt;

        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        BitConverter.GetBytes(36 + dataSize).CopyTo(wav, 4);
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
        wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)channels).CopyTo(wav, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(byteRate).CopyTo(wav, 28);
        BitConverter.GetBytes((short)blockAlign).CopyTo(wav, 32);
        BitConverter.GetBytes((short)bitsPerSample).CopyTo(wav, 34);
        wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
        BitConverter.GetBytes(dataSize).CopyTo(wav, 40);

        Buffer.BlockCopy(samples, 0, wav, 44, dataSize);
        return wav;
    }
}
