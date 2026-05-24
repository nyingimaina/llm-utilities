using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;

namespace NotifierHelper;

public partial class ToastWindow : Window
{
    readonly string _type;
    readonly bool _playSound;
    readonly int _dismissMs;
    bool _closing;

    public ToastWindow(string title, string message, string type, bool playSound, int dismissMs)
    {
        InitializeComponent();

        _type = type;
        _playSound = playSound;
        _dismissMs = dismissMs;

        TitleText.Text = title;
        MessageText.Text = message;

        // Semi-transparent dark glass background (cross-platform)
        Card.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1E, 0x1E, 0x2E));

        ConfigureForType();

        PositionChanged += OnPositionChanged;
        Opened += OnOpened;
    }

    void ConfigureForType()
    {
        if (_type == "error")
        {
            AccentStrip.Background = new SolidColorBrush(Color.Parse("#FF5252"));
            IconText.Foreground = new SolidColorBrush(Color.Parse("#FF5252"));
            IconText.Text = "✕";
        }
        else
        {
            AccentStrip.Background = new SolidColorBrush(Color.Parse("#4A9EFF"));
            IconText.Foreground = new SolidColorBrush(Color.Parse("#4CAF50"));
            IconText.Text = "✓";
        }
    }

    void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        PositionScreen();
    }

    void PositionScreen()
    {
        var screens = Screens;
        if (screens is null || screens.ScreenCount == 0) return;

        var screen = screens.Primary;
        if (screen is null) return;

        var scaling = screen.Scaling;
        var bounds = screen.Bounds;

        // Position at bottom-right with 16px padding
        var x = (int)((bounds.X + bounds.Width) / scaling - Width - 16);
        var y = (int)((bounds.Y + bounds.Height) / scaling - Height - 48);

        Position = new PixelPoint(
            (int)(x * scaling),
            (int)(y * scaling));
    }

    async void OnOpened(object? sender, EventArgs e)
    {
        if (_playSound) PlaySound();

        // Animate: slide in from right with overshoot
        await AnimateSlideIn();

        if (_type == "error")
            await AnimateShake();

        // Show checkmark with pop-in for info/completion
        if (_type != "error")
            await AnimateCheckmark();
    }

    async Task AnimateSlideIn()
    {
        var transform = this.RenderTransform as TranslateTransform;
        if (transform is null) return;

        // Keyframes: 400 → -15 → 0 (overshoot)
        await AnimateProperty(t => transform.X = t, 400, -15, 180);
        await AnimateProperty(t => transform.X = t, -15, 5, 60);
        await AnimateProperty(t => transform.X = t, 5, 0, 40);
    }

    async Task AnimateShake()
    {
        var transform = this.RenderTransform as TranslateTransform;
        if (transform is null) return;

        await AnimateProperty(t => transform.X = t, 0, -10, 40);
        await AnimateProperty(t => transform.X = t, -10, 8, 40);
        await AnimateProperty(t => transform.X = t, 8, -5, 30);
        await AnimateProperty(t => transform.X = t, -5, 4, 30);
        await AnimateProperty(t => transform.X = t, 4, 0, 20);
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

    static async Task AnimateProperty(Action<double> setter, double from, double to, int durationMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            var t = sw.ElapsedMilliseconds / (double)durationMs;
            // Cubic ease-out
            t = 1 - Math.Pow(1 - t, 3);
            var value = from + (to - from) * t;
            setter(value);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(8); // ~120fps
        }
        setter(to);
    }

    void PlaySound()
    {
        // Generate and play a simple sine-wave WAV in memory
        var samples = _type == "error" ? GenerateErrorSound() : GenerateInfoSound();
        PlayWavAsync(samples);
    }

    static short[] GenerateInfoSound()
    {
        // Single tick: 880Hz sine, 50ms, with fade-out
        var sampleRate = 44100;
        var duration = 0.05;
        var count = (int)(sampleRate * duration);
        var result = new short[count];
        for (int i = 0; i < count; i++)
        {
            var t = i / (double)sampleRate;
            var envelope = 1.0 - (i / (double)count); // linear fade
            result[i] = (short)(short.MaxValue * 0.3 * Math.Sin(2 * Math.PI * 880 * t) * envelope);
        }
        return result;
    }

    static short[] GenerateErrorSound()
    {
        // Two descending ticks: 660Hz + 440Hz, 25ms each
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

    async void PlayWavAsync(short[] samples)
    {
        try
        {
            var sampleRate = 44100;
            var bitsPerSample = 16;
            var channels = 1;
            var dataSize = samples.Length * 2;
            var wav = new byte[44 + dataSize];

            WriteWavHeader(wav, sampleRate, bitsPerSample, channels, dataSize);
            Buffer.BlockCopy(samples, 0, wav, 44, dataSize);

            var tempPath = Path.Combine(Path.GetTempPath(), $"notifier_{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(tempPath, wav);

            if (OperatingSystem.IsWindows())
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"(New-Object Media.SoundPlayer '{tempPath.Replace("'", "''")}').PlaySync()\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(3000);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "afplay",
                    Arguments = $"\"{tempPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(2000);
            }
            else if (OperatingSystem.IsLinux())
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "aplay",
                    Arguments = $"\"{tempPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(2000);
            }

            try { File.Delete(tempPath); } catch { }
        }
        catch
        {
        }
    }

    static void WriteWavHeader(byte[] wav, int sampleRate, int bitsPerSample, int channels, int dataSize)
    {
        var fmt = bitsPerSample / 8;
        var byteRate = sampleRate * channels * fmt;
        var blockAlign = channels * fmt;

        // "RIFF"
        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        BitConverter.GetBytes(36 + dataSize).CopyTo(wav, 4);
        // "WAVE"
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
        // "fmt "
        wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
        BitConverter.GetBytes(16).CopyTo(wav, 16);        // chunk size
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);   // PCM
        BitConverter.GetBytes((short)channels).CopyTo(wav, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(byteRate).CopyTo(wav, 28);
        BitConverter.GetBytes((short)blockAlign).CopyTo(wav, 32);
        BitConverter.GetBytes((short)bitsPerSample).CopyTo(wav, 34);
        // "data"
        wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
        BitConverter.GetBytes(dataSize).CopyTo(wav, 40);
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

    async Task AnimateClose()
    {
        // Fade out with upward drift
        var transform = this.RenderTransform as TranslateTransform;
        var startY = transform?.Y ?? 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var duration = 400;

        while (sw.ElapsedMilliseconds < duration)
        {
            var t = sw.ElapsedMilliseconds / (double)duration;
            t = t * t; // ease-in
            Opacity = 1 - t;
            if (transform is not null)
                transform.Y = startY - t * 15;
            await Task.Delay(8);
        }

        Opacity = 0;
        Close();
    }
}
