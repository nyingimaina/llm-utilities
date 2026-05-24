using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace NotifierHelper;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

            if (args.Contains("--listen"))
            {
                var anchor = new Window
                {
                    IsVisible = false,
                    Width = 0,
                    Height = 0,
                    ShowInTaskbar = false,
                };
                anchor.Show();
                _ = ListenStdin(desktop, anchor);
            }
            else
            {
                var title = ParseArg(args, "--title") ?? "LLM Utilities";
                var message = ParseArg(args, "--message") ?? "";
                var type = ParseArg(args, "--type") ?? "info";
                var noSound = args.Any(a => a == "--no-sound");
                var dismissMs = int.TryParse(ParseArg(args, "--dismiss-after-ms"), out var d) ? d : 5000;
                var sender = ParseArg(args, "--sender");
                var task = ParseArg(args, "--task");

                var window = new ToastWindow(title, message, type, !noSound, dismissMs, sender, task);
                desktop.MainWindow = window;
                window.Show();

                if (dismissMs > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(dismissMs);
                        await Dispatcher.UIThread.InvokeAsync(() => window.Close());
                    });
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    static async Task ListenStdin(IClassicDesktopStyleApplicationLifetime desktop, Window anchor)
    {
        try
        {
            var stdin = Console.In;
            string? line;
            while ((line = await stdin.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("__shutdown__")) break;
                try
                {
                    var req = JsonSerializer.Deserialize<NotifyRequest>(line);
                    if (req is null) continue;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var win = new ToastWindow(
                            req.Title ?? "LLM Utilities",
                            req.Message ?? "",
                            req.Type ?? "info",
                            req.Sound ?? true,
                            req.DismissAfterMs ?? 5000,
                            req.Sender,
                            req.Task);
                        win.Show();

                        var dismissMs = req.DismissAfterMs ?? 5000;
                        if (dismissMs > 0)
                        {
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(dismissMs);
                                await Dispatcher.UIThread.InvokeAsync(() => win.Close());
                            });
                        }
                    });
                }
                catch { /* skip malformed line */ }
            }
        }
        catch { /* stdin closed */ }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => anchor.Close());
        }
    }

    static string? ParseArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return null;
    }

    record NotifyRequest(
        [property: JsonPropertyName("title")]         string? Title,
        [property: JsonPropertyName("message")]       string? Message,
        [property: JsonPropertyName("type")]          string? Type,
        [property: JsonPropertyName("sound")]         bool? Sound,
        [property: JsonPropertyName("dismissAfterMs")]int?  DismissAfterMs,
        [property: JsonPropertyName("sender")]        string? Sender,
        [property: JsonPropertyName("task")]          string? Task);
}
