using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ControlPanel.Models;
using LLMUtilities.Commons;

namespace ControlPanel;

public partial class MainWindow : Window
{
    readonly System.Timers.Timer _tailTimer = new(2000) { AutoReset = true };
    bool _tailPaused;

    public MainWindow()
    {
        InitializeComponent();

        var ver = GetType().Assembly.GetName().Version?.ToString() ?? "1.34.0";
        Title = $"LLM Utilities Control Panel v{ver}";
        TxtVersion.Text = $"Version: {ver}";
        TxtRuntime.Text = $".NET Runtime: {RuntimeInformation.FrameworkDescription}";
        TxtInstallDir.Text = $"Install Dir: {AppDir}";
        TxtConfigDir.Text = $"Config Dir: {ConfigPaths.BaseDir}";
        TxtLogDir.Text = $"Log Dir: {ConfigPaths.BaseDir}";

        _tailTimer.Elapsed += (_, _) => Dispatcher.UIThread.Post(RefreshLogTail);

        LoadServers();
        LoadConfigCombo();
        LoadLogCombo();
        LoadRegistrations();
    }

    static string AppDir =>
        Path.GetDirectoryName(Environment.ProcessPath ?? ConfigPaths.BaseDir) ?? ConfigPaths.BaseDir;

    // ── Servers Tab ──────────────────────────────────────────────────────────

    void LoadServers()
    {
        var servers = Service.DetectServers();
        var registrations = Service.DetectRegistrations();

        foreach (var s in servers)
        {
            var lines = new List<string>();
            foreach (var r in registrations)
            {
                var ok = s.Name switch
                {
                    "Rowster" => r.HasRowster,
                    "FReader" => r.HasFReader,
                    "CliSilentProxy" => r.HasCliSilentProxy,
                    "Notifier" => r.HasNotifier,
                    _ => false,
                };
                if (ok) lines.Add(r.LlmName);
            }
            s.RegistrationStatus = lines.Count > 0
                ? $"Registered with: {string.Join(", ", lines)}"
                : "Not registered with any LLM";
        }

        ServerList.ItemsSource = servers;
    }

    void OnRefresh(object? sender, RoutedEventArgs e) => LoadServers();

    void OnTestAll(object? sender, RoutedEventArgs e)
    {
        Task.Run(() =>
        {
            foreach (var s in Service.DetectServers())
                _ = Service.TestConnection(s.Name);
        });
    }

    async void OnTestConnection(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
        {
            btn.IsEnabled = false;
            btn.Content = "Testing...";
            var result = await Task.Run(() => Service.TestConnection(name));
            btn.IsEnabled = true;
            btn.Content = "Test";
            await ShowMessage(result ?? "No result");
        }
    }

    void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string name)
        {
            LogServerCombo.SelectedItem = name;
            if (Parent is TabControl tc) tc.SelectedIndex = 2;
        }
    }

    // ── Configs Tab ──────────────────────────────────────────────────────────

    void LoadConfigCombo()
    {
        var servers = Service.DetectServers().Select(s => s.Name).ToList();
        ConfigServerCombo.ItemsSource = servers;
        if (servers.Count > 0)
            ConfigServerCombo.SelectedIndex = 0;
    }

    void OnConfigServerChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ConfigServerCombo.SelectedItem is string name)
        {
            var path = ConfigPaths.ConfigFile(name);
            try
            {
                if (File.Exists(path))
                    ConfigEditor.Text = File.ReadAllText(path);
                else
                    ConfigEditor.Text = $"// No config file at:\n// {path}\n// Create with fields like:\n// {{ \"autoNotifyThresholdMs\": 15000 }}";
            }
            catch (Exception ex)
            {
                ConfigEditor.Text = $"// Error: {ex.Message}";
            }
        }
    }

    async void OnSaveConfig(object? sender, RoutedEventArgs e)
    {
        if (ConfigServerCombo.SelectedItem is string name && ConfigEditor.Text is string json)
        {
            if (Service.SaveConfig(name, json))
                await ShowMessage("Config saved.");
            else
                await ShowMessage("Invalid JSON. Config not saved.");
        }
    }

    void OnResetConfig(object? sender, RoutedEventArgs e)
    {
        if (ConfigServerCombo.SelectedItem is string name)
        {
            var path = ConfigPaths.ConfigFile(name);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            ConfigEditor.Text = "// Config deleted. Server uses built-in defaults.";
        }
    }

    // ── Logs Tab ─────────────────────────────────────────────────────────────

    void LoadLogCombo()
    {
        var servers = Service.DetectServers().Select(s => s.Name).ToList();
        LogServerCombo.ItemsSource = servers;
        if (servers.Count > 0)
            LogServerCombo.SelectedIndex = 0;
    }

    void OnLogServerChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshLogTail();
        if (!_tailPaused)
            _tailTimer.Start();
    }

    void OnToggleTail(object? sender, RoutedEventArgs e)
    {
        _tailPaused = BtnToggleTail.IsChecked == true;
        BtnToggleTail.Content = _tailPaused ? "▶ Resume" : "⏸ Pause";
        if (_tailPaused) _tailTimer.Stop();
        else _tailTimer.Start();
    }

    void OnClearLog(object? sender, RoutedEventArgs e) => LogViewer.Text = "";

    void RefreshLogTail()
    {
        if (_tailPaused || LogServerCombo.SelectedItem is not string name) return;
        var tail = Service.ReadLogTail(name);
        if (tail != null)
        {
            LogViewer.Text = tail;
            LogViewer.CaretIndex = LogViewer.Text.Length;
        }
        LogStatus.Text = DateTime.Now.ToString("HH:mm:ss");
    }

    // ── Registration Tab ─────────────────────────────────────────────────────

    void LoadRegistrations() =>
        RegistrationList.ItemsSource = Service.DetectRegistrations();

    void OnRefreshRegistration(object? sender, RoutedEventArgs e) => LoadRegistrations();

    // ── Compliance Tab ────────────────────────────────────────────────────────

    static string ComplianceDefaultPath =>
        Path.Combine(AppDir, "code-standards.default.md");

    static string ComplianceEditablePath =>
        Path.Combine(AppDir, "code-standards.md");

    void LoadComplianceRules()
    {
        if (!File.Exists(ComplianceEditablePath))
        {
            if (File.Exists(ComplianceDefaultPath))
                File.Copy(ComplianceDefaultPath, ComplianceEditablePath, overwrite: false);
        }

        ComplianceEditor.Text = File.Exists(ComplianceEditablePath)
            ? File.ReadAllText(ComplianceEditablePath)
            : "// No compliance rules file found.\n// The installed default would be at:\n// " + ComplianceDefaultPath;
    }

    async void OnSaveCompliance(object? sender, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(ComplianceEditablePath, ComplianceEditor.Text);
            await ShowMessage("Compliance rules saved.");
        }
        catch (Exception ex)
        {
            await ShowMessage($"Save failed: {ex.Message}");
        }
    }

    void OnReloadCompliance(object? sender, RoutedEventArgs e) => LoadComplianceRules();

    async void OnRestoreCompliance(object? sender, RoutedEventArgs e)
    {
        if (!File.Exists(ComplianceDefaultPath))
        {
            await ShowMessage("Default file not found: " + ComplianceDefaultPath);
            return;
        }
        File.Copy(ComplianceDefaultPath, ComplianceEditablePath, overwrite: true);
        LoadComplianceRules();
        await ShowMessage("Defaults restored from code-standards.default.md");
    }

    void OnOpenComplianceDir(object? sender, RoutedEventArgs e) => OpenFolder(AppDir);

    // ── About Tab ────────────────────────────────────────────────────────────

    void OnOpenInstallDir(object? sender, RoutedEventArgs e) => OpenFolder(AppDir);

    void OnOpenConfigDir(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ConfigPaths.BaseDir);
        OpenFolder(ConfigPaths.BaseDir);
    }

    void OnOpenLogDir(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ConfigPaths.LogDir("Rowster"));
        OpenFolder(ConfigPaths.BaseDir);
    }

    static void OpenFolder(string path)
    {
        try { Process.Start("explorer.exe", path); } catch { }
    }

    async Task ShowMessage(string msg)
    {
        var dlg = new Window
        {
            Title = "LLM Utilities",
            Content = new TextBlock { Text = msg, Margin = new Avalonia.Thickness(20) },
            Width = 400, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        await dlg.ShowDialog(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tailTimer.Stop();
        _tailTimer.Dispose();
        base.OnClosed(e);
    }
}
