using ClassIsle.Models;
using ClassIsle.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClassIsle;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIconService? _tray;
    private DispatcherQueue? _dispatcher;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Settings = AppSettings.Load();

        // 托盘驻留
        _tray = new TrayIconService
        {
            OnSettingsRequested = OpenSettings,
            OnExitRequested = ExitApp,
            OnLeftClick = () => _mainWindow?.Wake(),
        };
        _tray.Show();

        // 主窗口（灵动岛）
        _mainWindow = new MainWindow(Settings);

        if (!Settings.FirstRunCompleted)
        {
            // 首次启动引导，完成后进入托盘驻留
            var guide = new GuideWindow(() =>
            {
                Settings.FirstRunCompleted = true;
                Settings.Save();
            });
            guide.Activate();
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(Settings, saved =>
        {
            _settingsWindow = null;
            ApplyAutoStart(saved);
            _mainWindow?.ApplySettingsChanged();
        });
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    /// <summary>开机自启（注册表 HKCU Run）</summary>
    private static void ApplyAutoStart(AppSettings settings)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            var exePath = Environment.ProcessPath;
            if (settings.AutoStart && exePath != null)
                key.SetValue("ClassIsle", $"\"{exePath}\"");
            else
                key.DeleteValue("ClassIsle", throwOnMissingValue: false);
        }
        catch { }
    }

    private void ExitApp()
    {
        _tray?.Dispose();
        _mainWindow?.Close();
        Exit();
    }
}
