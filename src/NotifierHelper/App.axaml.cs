using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

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
            var title = ParseArg(args, "--title") ?? "LLM Utilities";
            var message = ParseArg(args, "--message") ?? "";
            var type = ParseArg(args, "--type") ?? "info";
            var noSound = args.Any(a => a == "--no-sound");
            var dismissMs = int.TryParse(ParseArg(args, "--dismiss-after-ms"), out var d) ? d : 5000;

            var window = new ToastWindow(title, message, type, !noSound, dismissMs);
            desktop.MainWindow = window;
            window.Show();

            // Close after dismiss if non-sticky
            if (dismissMs > 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(dismissMs);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => window.Close());
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    static string? ParseArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return null;
    }
}
