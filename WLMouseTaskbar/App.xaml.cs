using System.Windows;
using System.Windows.Threading;

namespace WLMouseTaskbar;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private BatteryService? _battery;
    private TaskbarWidgetWindow? _widget;
    private TrayIcon? _tray;
    private MainWindow? _status;
    private AppSettings _settings = new();
    private bool _openSettingsOnStart;

    private LowBatteryWindow? _lowBattery;
    private bool _lowBatteryArmed = true;
    private DateTime _lastLowBatteryAt = DateTime.MinValue;

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

        // 开发用：--shot-ui <png 路径> [--page device|widget|about] [--dark]
        // 把设置窗口整页渲染成 PNG。屏幕截图在别的置顶窗口盖住时会拍到别的东西，
        // 所以直接让 WPF 自己渲染。
        int shotIndex = Array.IndexOf(e.Args, "--shot-ui");
        if (shotIndex >= 0 && shotIndex + 1 < e.Args.Length)
        {
            int pageIndex = Array.IndexOf(e.Args, "--page");
            string page = pageIndex >= 0 && pageIndex + 1 < e.Args.Length ? e.Args[pageIndex + 1] : "overview";
            bool dark = e.Args.Contains("--dark");
            bool light = e.Args.Contains("--light");
            RunUiSnapshot(e.Args[shotIndex + 1], page, light ? false : dark ? true : (bool?)null);
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

        ThemeManager.Apply();

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

        // 开发用：--test-alert 15 直接弹一次低电量提示，用来检查提示窗外观
        int alertIndex = Array.IndexOf(e.Args, "--test-alert");
        if (alertIndex >= 0)
        {
            int percent = alertIndex + 1 < e.Args.Length
                          && int.TryParse(e.Args[alertIndex + 1], out int p) ? p : 15;

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeManager.Apply();

            var preview = new LowBatteryWindow();
            preview.Closed += (_, _) => Shutdown();
            preview.Show(new BatteryState(true, percent, false, null, null, "", null, DateTime.Now), 20);
            return;
        }

        // 开发用：--fake-percent 88 强制显示某个电量，方便在没有设备 / 鼠标休眠时
        // 检查任务栏上的渲染效果（region 裁剪只有真正挂上任务栏才看得出问题）。
        int fakeIndex = Array.IndexOf(e.Args, "--fake-percent");
        if (fakeIndex >= 0 && fakeIndex + 1 < e.Args.Length
            && int.TryParse(e.Args[fakeIndex + 1], out int fakePercent))
        {
            TaskbarWidgetWindow.DevPercentOverride = fakePercent;
        }

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

    private void RunUiSnapshot(string path, string page, bool? dark)
    {
        try
        {
            ThemeManager.Forced = dark;
            ThemeManager.Apply(dark);

            var settings = new AppSettings();
            using var battery = new BatteryService();

            // 没接鼠标时也给一份像样的假数据，否则所有设置项都会被隐藏，看不出布局
            BatteryState state = battery.State.DevicePresent
                ? battery.State
                : new BatteryState(true, 87, false, "1.0.2.13", "1.0.2.7",
                    "00 A1 00 02 02 00 83 00 57", null, DateTime.Now);

            var window = new MainWindow(battery, settings);
            window.PrepareSnapshot(state, page);
            window.Show();
            window.UpdateLayout();

            window.RenderTo(path);
            window.Close();
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
            _widget?.SyncMenuState();
            MaybeShowLowBattery(state);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 电量跌破阈值时弹一次提示。
    ///
    /// 「弹一次」的判定要防住两种刷屏：电量在阈值附近来回跳（每次都弹），
    /// 以及程序启动时本来就很低（一开机就弹）。所以只在**从健康跨到偏低**时弹，
    /// 并且加一个冷却时间；充上电或回到阈值以上就重新武装。
    /// </summary>
    private void MaybeShowLowBattery(BatteryState state)
    {
        if (!_settings.LowBatteryAlert) return;
        if (!state.DevicePresent || state.Percent is not int percent) return;

        int threshold = Math.Clamp(_settings.LowBatteryThreshold, 1, 99);

        if (state.Charging || percent >= threshold)
        {
            _lowBatteryArmed = true;
            return;
        }

        if (!_lowBatteryArmed) return;
        if (DateTime.Now - _lastLowBatteryAt < TimeSpan.FromMinutes(30)) return;

        _lowBatteryArmed = false;
        _lastLowBatteryAt = DateTime.Now;

        // WPF 关闭过的窗口不能再 Show，所以每次新建一个
        LowBatteryWindow? previous = _lowBattery;
        var window = new LowBatteryWindow();

        window.Closed += (_, _) =>
        {
            // 只清理自己，别把后来新建的那个也抹掉
            if (ReferenceEquals(_lowBattery, window)) _lowBattery = null;
        };

        _lowBattery = window;
        previous?.Close();

        window.Show(state, threshold);
    }

    private void OpenStatusWindow()
    {
        if (_status is null || !_status.IsVisible)
        {
            _status = new MainWindow(_battery!, _settings, OnSettingsChanged);
            _status.Closed += (_, _) => _status = null;
        }

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
