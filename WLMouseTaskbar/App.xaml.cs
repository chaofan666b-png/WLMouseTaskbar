using System.Windows;
using System.Windows.Threading;

namespace WLMouseTaskbar;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private BatteryService? _battery;
    private TaskbarWidgetWindow? _widget;
    private TrayIcon? _tray;
    private StatusWindow? _status;
    private AppSettings _settings = new();
    private bool _openSettingsOnStart;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _openSettingsOnStart = e.Args.Contains("--settings");

        // 开发用：--snapshot <png 路径> 把控件渲染成图片后退出
        int snapshotIndex = Array.IndexOf(e.Args, "--snapshot");
        if (snapshotIndex >= 0 && snapshotIndex + 1 < e.Args.Length)
        {
            bool forceLight = e.Args.Contains("--light");
            RunSnapshot(e.Args[snapshotIndex + 1], forceLight);
            return;
        }

        _instanceMutex = new Mutex(true, @"Local\WLMouseTaskbar.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        // 托盘 + 任务栏控件都是常驻的，关掉窗口不退出
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _settings = AppSettings.Load();
        // 首次运行落一份默认配置，用户想改也能直接找到文件
        _settings.Save();
        _battery = new BatteryService { PollSeconds = _settings.PollSeconds };

        // 任务栏控件
        _widget = new TaskbarWidgetWindow(_battery, _settings)
        {
            RequestOpenSettings = OpenStatusWindow,
        };
        _widget.TrayToggleRequested += () => _tray?.SyncState();
        _widget.Show();

        // 通知区图标
        _tray = new TrayIcon(_settings);
        _tray.OpenSettingsRequested += OpenStatusWindow;
        _tray.RefreshRequested += () => _battery.RequestRefresh();
        _tray.WidgetVisibilityChanged += () =>
        {
            _widget.SyncMenuState();
            _widget.RefreshNow();
        };
        _tray.TrayVisibilityChanged += () => _tray.SyncState();

        _battery.Updated += OnBatteryUpdated;

        // 首次启动就有数据，不用等一个轮询周期
        _battery.RequestRefresh();

        if (_openSettingsOnStart)
            Dispatcher.BeginInvoke(OpenStatusWindow, DispatcherPriority.ApplicationIdle);
    }

    private void RunSnapshot(string path, bool forceLight)
    {
        try
        {
            if (forceLight) ThemeHelper.OverrideLight = true;

            var settings = new AppSettings();
            using var battery = new BatteryService();
            battery.RequestRefresh();

            BatteryState state = battery.State;
            if (!state.DevicePresent || state.Percent is null)
                state = state with { DevicePresent = true, Percent = 96 };

            var widget = new TaskbarWidgetWindow(battery, settings);
            widget.Show();
            widget.ApplySnapshotState(state);

            widget.RenderSnapshot(path, !ThemeHelper.IsLightTaskbar());
            widget.Close();
            Shutdown();
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".error.txt", ex.ToString());
            Shutdown(-1);
        }
    }

    private void OnBatteryUpdated(BatteryState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _tray?.Update(state);
            _status?.Apply(state);
            _widget?.SyncMenuState();
        }, DispatcherPriority.Background);
    }

    private void OpenStatusWindow()
    {
        if (_status is null || !_status.IsVisible)
        {
            _status = new StatusWindow(_battery!, _settings, OnSettingsChanged);
            _status.Closed += (_, _) => _status = null;
        }

        _status.Apply(_battery!.State);
        _status.Show();
        _status.Activate();
        _status.Topmost = true;
        _status.Topmost = false;
    }

    private void OnSettingsChanged()
    {
        if (_battery is not null) _battery.PollSeconds = _settings.PollSeconds;
        _widget?.SyncMenuState();
        _tray?.SyncState();
        _widget?.RefreshNow();
        _widget?.RequestReposition();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WLMouseTaskbar");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {e.Exception}{Environment.NewLine}");
        }
        catch
        {
            // 写日志失败就算了，别再抛
        }

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _battery?.Dispose();
        _tray?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
