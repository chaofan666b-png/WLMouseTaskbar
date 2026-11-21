using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace WLMouseTaskbar;

/// <summary>
/// 设置 / 详情主窗口。布局参考 FluentFlyout 与 Windows 11「设置」应用：
/// 左侧导航 + 右侧内容页，内容页由卡片组成。
///
/// 页面用代码搭（见 <see cref="Ui"/>），因为设置项要按设备实际支持的能力增减，
/// 写死在 XAML 里会出现「有这个开关但鼠标不认」的假控件。
/// </summary>
public partial class MainWindow : Window
{
    private readonly BatteryService _battery;
    private readonly AppSettings _settings;
    private readonly Action? _onSettingsChanged;

    private string _page = "overview";
    private bool _loading = true;

    public MainWindow(BatteryService battery, AppSettings settings, Action? onSettingsChanged = null)
    {
        _battery = battery;
        _settings = settings;
        _onSettingsChanged = onSettingsChanged;

        InitializeComponent();
        ApplyTheme();

        AutoStartCheck.IsChecked = AutoStart.IsEnabled;
        _loading = false;

        _battery.Updated += OnBatteryUpdated;
        _battery.ConfigUpdated += OnConfigUpdated;

        BuildPage();
        Apply(battery.State);

        // 打开窗口时顺手把配置读一遍（鼠标休眠时这一步不会有结果）
        _ = _battery.RefreshConfigAsync();
    }

    private void ApplyTheme() => ThemeManager.Apply();

    // ---- 状态同步 ----

    private void OnBatteryUpdated(BatteryState state) => Dispatcher.BeginInvoke(() =>
    {
        Apply(state);
        if (_page is "overview" or "device") BuildPage();
    });

    private void OnConfigUpdated(MouseConfig config) => Dispatcher.BeginInvoke(() =>
    {
        if (_page is "overview" or "device") BuildPage();
    });

    private void Apply(BatteryState state)
    {
        bool connected = state.DevicePresent && state.Percent is not null;
        Color accent = Palette.For(state, !ThemeManager.IsDark).Accent;
        var brush = new SolidColorBrush(accent);
        brush.Freeze();

        NavBatteryText.Text = state.Percent is int p ? p.ToString() : "—";
        NavBatteryText.Foreground = brush;
        NavBatteryShell.BorderBrush = new SolidColorBrush(Color.FromArgb(
            0x99, accent.R, accent.G, accent.B));

        NavDeviceName.Text = connected ? "WL Mouse" : "未连接";
        NavDeviceStatus.Text = state.Timestamp == DateTime.MinValue
            ? "正在读取…"
            : $"{state.Percent}% · {state.Timestamp:HH:mm:ss}";

        StatusBarText.Text = state.DevicePresent
            ? state.Error is null
                ? $"已连接 · 上次更新 {state.Timestamp:HH:mm:ss}"
                : state.Error
            : "未检测到接收器";
    }

    // ---- 开发用：整页渲染成 PNG ----

    /// <summary>把给定状态与配置铺到指定页面上，供 --shot-ui 生成截图。</summary>
    public void PrepareSnapshot(BatteryState state, string page)
    {
        var config = new MouseConfig
        {
            Profile = 1,
            DpiStages = new[] { 800, 1600, 2400, 3200 },
            ActiveStage = 2,
            PollingRateHz = 4000,
            LiftOff = LiftOffDistance.Low,
            DebounceMs = 4,
            SleepSeconds = 600,
            AngleSnapping = false,
            MotionSync = true,
        };

        Apply(state);

        // 页面渲染读的是 _battery 的实时字段，这里临时换成快照数据
        _snapshotState = state;
        _snapshotConfig = config;

        _page = page;
        foreach (RadioButton nav in new[] { NavOverview, NavDevice, NavWidget, NavAbout })
        {
            if (nav.Tag as string == page) nav.IsChecked = true;
        }

        BuildPage();
    }

    private BatteryState? _snapshotState;
    private MouseConfig? _snapshotConfig;

    private BatteryState CurrentState => _snapshotState ?? _battery.State;
    private MouseConfig CurrentConfig => _snapshotConfig ?? _battery.Config;

    /// <summary>用 RenderTargetBitmap 渲染整个窗口内容。</summary>
    public void RenderTo(string path, double scale = 1.5)
    {
        if (Content is not FrameworkElement root) return;

        root.Measure(new Size(ActualWidth, ActualHeight));
        root.Arrange(new Rect(0, 0, ActualWidth, ActualHeight));
        root.UpdateLayout();

        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(ActualWidth * scale), (int)(ActualHeight * scale),
            96 * scale, 96 * scale,
            PixelFormats.Pbgra32);

        target.Render(root);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
        using FileStream fs = File.Create(path);
        encoder.Save(fs);
    }

    // ---- 页面切换 ----

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        _page = tag;
        BuildPage();
    }

    private void BuildPage()
    {
        PageHost.Children.Clear();

        UIElement page = _page switch
        {
            "device" => BuildDevicePage(),
            "widget" => BuildWidgetPage(),
            "about" => BuildAboutPage(),
            _ => BuildOverviewPage(),
        };

        PageHost.Children.Add(page);
        PageScroller.ScrollToVerticalOffset(0);
    }

    // ---- 概览 ----

    private UIElement BuildOverviewPage()
    {
        BatteryState state = CurrentState;
        MouseConfig config = CurrentConfig;

        var panel = new StackPanel();
        panel.Children.Add(Ui.Title("概览"));
        panel.Children.Add(Ui.Subtitle("WLmouse 无线鼠标的电量与当前配置。"));

        if (!state.DevicePresent)
        {
            panel.Children.Add(Ui.Empty("未检测到接收器",
                "插上 WLmouse 的 USB 接收器，或唤醒鼠标后再试。"));
            return panel;
        }

        if (state.Percent is not int percent)
        {
            panel.Children.Add(Ui.Empty("鼠标没有响应",
                "接收器已经识别，但鼠标可能处于休眠。动一下鼠标，然后点右下角的「立即刷新」。"));
            return panel;
        }

        // 电量卡：大号数字 + 电池图形 + 状态
        var batteryPanel = new Grid();
        batteryPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        batteryPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Color accent = Palette.For(state, !ThemeManager.IsDark).Accent;

        var big = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        big.Children.Add(new TextBlock
        {
            Text = $"{percent}%",
            FontSize = 52,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
            LineHeight = 56,
        });

        var status = new TextBlock
        {
            Text = state.Charging ? "充电中" : "放电中",
            FontSize = 14,
            Foreground = Ui.Res("TextPrimary"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        big.Children.Add(status);

        batteryPanel.Children.Add(big);

        var right = new StackPanel
        {
            Margin = new Thickness(28, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(BuildBatteryGraphic(percent, accent));
        right.Children.Add(new TextBlock
        {
            Text = state.Timestamp == DateTime.MinValue ? "" : $"更新于 {state.Timestamp:HH:mm:ss}",
            FontSize = 12,
            Foreground = Ui.Res("TextSecondary"),
            Margin = new Thickness(0, 10, 0, 0),
        });
        Grid.SetColumn(right, 1);
        batteryPanel.Children.Add(right);

        panel.Children.Add(WrapCard(batteryPanel, 20));

        // 当前配置一栏
        panel.Children.Add(Ui.Section("当前配置"));

        var configRows = new List<UIElement>();
        configRows.Add(Info("DPI", config.DpiStages.Length > 0
            ? $"{config.CurrentDpi}（第 {config.ActiveStage + 1} / {config.DpiStages.Length} 档）"
            : "—"));
        configRows.Add(Info("回报率", config.PollingRateHz is int hz ? $"{hz} Hz" : "—"));
        configRows.Add(Info("抬升高度", config.LiftOff?.ToString() ?? "—"));
        configRows.Add(Info("角度吸附", config.AngleSnapping is bool snap ? (snap ? "开启" : "关闭") : "—"));
        configRows.Add(Info("Motion Sync", config.MotionSync is bool sync ? (sync ? "开启" : "关闭") : "—"));

        panel.Children.Add(Ui.Card(configRows.ToArray()));

        panel.Children.Add(Ui.Section("设备"));
        panel.Children.Add(Ui.Card(
            Info("鼠标固件", state.MouseFirmware ?? "—"),
            Info("接收器固件", state.ReceiverFirmware ?? "—")));

        return panel;
    }

    /// <summary>横向电池图形，高度 60，随电量填充。</summary>
    private static UIElement BuildBatteryGraphic(int percent, Color accent)
    {
        const double bodyW = 150, bodyH = 60, tipW = 6;

        var fill = new Border
        {
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4),
            Background = new SolidColorBrush(accent),
            Width = (bodyW - 8) * Math.Clamp(percent, 0, 100) / 100.0,
        };

        var body = new Grid { Width = bodyW, Height = bodyH };
        body.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, accent.R, accent.G, accent.B)),
        });
        body.Children.Add(fill);

        var tip = new System.Windows.Shapes.Rectangle
        {
            Width = tipW,
            Height = 24,
            RadiusX = 3,
            RadiusY = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0),
            Fill = new SolidColorBrush(Color.FromArgb(0x99, accent.R, accent.G, accent.B)),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(body);
        row.Children.Add(tip);
        return row;
    }

    private static Border WrapCard(UIElement content, double padding) => new()
    {
        Background = Ui.Res("CardBackground"),
        BorderBrush = Ui.Res("CardStroke"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(padding),
        Child = content,
    };

    private static UIElement Info(string label, string value)
    {
        Grid row = Ui.InfoRow(label, out TextBlock text);
        text.Text = value;
        return row;
    }

    // ---- 设备 ----

    private UIElement BuildDevicePage()
    {
        MouseConfig config = CurrentConfig;
        var panel = new StackPanel();

        panel.Children.Add(Ui.Title("设备"));
        panel.Children.Add(Ui.Subtitle(
            "这些是直接写进鼠标板载内存的设置，改完立刻生效，拔掉接收器也会保留。"));

        if (!CurrentState.DevicePresent)
        {
            panel.Children.Add(Ui.Empty("未检测到设备", "插上接收器后才能修改配置。"));
            return panel;
        }

        if (config.DpiStages.Length == 0)
        {
            panel.Children.Add(Ui.Empty("暂时读不到配置",
                "接收器已识别，但鼠标没有回应。动一下鼠标唤醒它，然后点「立即刷新」。"));
            return panel;
        }

        // ---- DPI ----
        panel.Children.Add(Ui.Section("指针速度"));

        var dpiRows = new List<UIElement>();
        for (int i = 0; i < config.DpiStages.Length; i++)
        {
            int index = i;
            int value = config.DpiStages[i];
            string title = i == config.ActiveStage ? $"第 {i + 1} 档（当前）" : $"第 {i + 1} 档";

            dpiRows.Add(Ui.NumberRow(title, null, 50, 30000, 50, value, dpi =>
                Change(c => c.SetDpiStage(index, dpi), $"DPI 第 {index + 1} 档设为 {dpi}")));
        }

        dpiRows.Add(Ui.Row("当前档位", "左右切换 DPI 时用的板载档位。",
            Ui.Combo(
                Enumerable.Range(0, config.DpiStages.Length),
                config.ActiveStage,
                index => Change(c => c.SetActiveDpiStage(index), $"切换到第 {index + 1} 档"),
                index => $"第 {index + 1} 档 · {config.DpiStages[index]}")));

        panel.Children.Add(Ui.Card(dpiRows.ToArray()));

        // ---- 回报率 ----
        panel.Children.Add(Ui.Section("回报率"));

        // 只列出接收器实际能跑的档位：8K 接收器才有 4000/8000
        int[] rates = PollingRates.All.Where(hz => hz <= MaxPollingFor(config)).ToArray();
        if (rates.Length == 0) rates = PollingRates.All;

        panel.Children.Add(Ui.Card(
            Ui.Row("回报率", "每秒向电脑报告位置的次数。数值越高越跟手，也越费电。",
                Ui.Combo(rates, config.PollingRateHz ?? rates[^1],
                    hz => Change(c => c.SetPollingRate(hz), $"回报率设为 {hz} Hz"),
                    hz => $"{hz} Hz"))));

        // ---- 传感器 ----
        panel.Children.Add(Ui.Section("传感器"));

        var sensorRows = new List<UIElement>();

        if (config.LiftOff is LiftOffDistance lod)
        {
            sensorRows.Add(Ui.Row("抬升高度", "鼠标离开桌面多高就停止追踪。",
                Ui.Combo(Enum.GetValues<LiftOffDistance>(), lod,
                    value => Change(c => c.SetLiftOff(value), $"抬升高度设为 {value}"),
                    value => value switch
                    {
                        LiftOffDistance.Low => "低",
                        LiftOffDistance.Medium => "中",
                        _ => "高",
                    })));
        }

        if (config.AngleSnapping is bool snapping)
        {
            sensorRows.Add(Ui.Row("角度吸附", "把接近水平 / 垂直的移动修正成直线。",
                Toggle(snapping, on => Change(c => c.SetAngleSnapping(on),
                    $"角度吸附{(on ? "开启" : "关闭")}"))));
        }

        if (config.MotionSync is bool motionSync)
        {
            sensorRows.Add(Ui.Row("Motion Sync", "让传感器采样与回报节拍对齐，移动更平滑。",
                Toggle(motionSync, on => Change(c => c.SetMotionSync(on),
                    $"Motion Sync {(on ? "开启" : "关闭")}"))));
        }

        if (sensorRows.Count > 0) panel.Children.Add(Ui.Card(sensorRows.ToArray()));

        // ---- 按键 ----
        panel.Children.Add(Ui.Section("按键"));

        var keyRows = new List<UIElement>();

        if (config.DebounceMs is int debounce)
        {
            keyRows.Add(Ui.Row("去抖时间", "过滤按下时的机械抖动，越低响应越快。",
                Ui.Combo(Enumerable.Range(0, 16), Math.Clamp(debounce, 0, 15),
                    ms => Change(c => c.SetDebounce(ms), $"去抖设为 {ms} ms"),
                    ms => $"{ms} ms")));
        }

        int? sleep = config.SleepSeconds;
        int[] sleepOptions = { 30, 60, 120, 300, 600, 1800 };
        keyRows.Add(Ui.Row("休眠时间", "静止多久后进入省电休眠。",
            Ui.Combo(sleepOptions, sleep ?? 600,
                seconds => Change(c => c.SetSleep(seconds), $"休眠设为 {seconds} s"),
                seconds => seconds >= 60 ? $"{seconds / 60} 分钟" : $"{seconds} 秒")));

        panel.Children.Add(Ui.Card(keyRows.ToArray()));

        return panel;
    }

    /// <summary>按接收器型号猜最大回报率。读不到时按 8000 放开，由鼠标自己拒绝。</summary>
    private static int MaxPollingFor(MouseConfig config) =>
        config.PollingRateHz is int hz && hz > 0 ? Math.Max(hz, 1000) : 8000;

    // ---- 任务栏显示 ----

    private UIElement BuildWidgetPage()
    {
        var panel = new StackPanel();

        panel.Children.Add(Ui.Title("任务栏显示"));
        panel.Children.Add(Ui.Subtitle("控制电量胶囊在任务栏上的位置与外观。"));

        panel.Children.Add(Ui.Section("显示"));

        panel.Children.Add(Ui.Card(
            Ui.Row("在任务栏显示电量", "把电量胶囊贴在任务栏通知区域左侧。",
                Toggle(_settings.ShowWidget, on => UpdateSettings(s =>
                {
                    s.ShowWidget = on;
                }))),
            Ui.Row("在通知区域显示图标", "保留一个 16×16 的托盘图标作为备用入口。",
                Toggle(_settings.ShowTrayIcon, on => UpdateSettings(s =>
                {
                    s.ShowTrayIcon = on;
                }))),
            Ui.Row("未连接时隐藏", "鼠标没连上时不在任务栏上占位。",
                Toggle(_settings.HideWhenDisconnected, on => UpdateSettings(s =>
                {
                    s.HideWhenDisconnected = on;
                })))));

        panel.Children.Add(Ui.Section("位置"));

        string[] positionNames = { "紧贴通知区域", "任务栏居中", "靠近开始按钮" };

        panel.Children.Add(Ui.Card(
            Ui.Row("显示位置", "任务栏上已经有别的挂件时，换个位置避免重叠。",
                Ui.Combo(Enumerable.Range(0, 3), Math.Clamp(_settings.WidgetPosition, 0, 2),
                    pos => UpdateSettings(s => s.WidgetPosition = pos),
                    pos => positionNames[Math.Clamp(pos, 0, 2)])),
            Ui.Row("自动避让其他挂件",
                "检测任务栏上别的挂件（例如 FluentFlyout 的媒体控件）的实际可见范围，撞上就往左让。",
                Toggle(_settings.AvoidOtherWidgets, on => UpdateSettings(s => s.AvoidOtherWidgets = on)))));

        // 距离滑杆
        var offsetLabel = new TextBlock
        {
            Text = $"{_settings.WidgetOffset:0} px",
            FontSize = 13,
            Foreground = Ui.Res("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Slider offsetSlider = Ui.Slider(0, 600, _settings.WidgetOffset, 2, (_, e) =>
        {
            offsetLabel.Text = $"{Math.Round(e.NewValue):0} px";
            _settings.WidgetOffset = Math.Round(e.NewValue);
            _onSettingsChanged?.Invoke();
        });
        offsetSlider.ValueChanged += (_, _) => _settings.Save();

        panel.Children.Add(Ui.Card(Ui.RowWithTrailing(
            "与锚点的距离",
            "任务栏上别的挂件（例如 FluentFlyout 的媒体控件）会占住通知区域左侧，调大这个值把本控件往左错开。",
            offsetLabel,
            offsetSlider)));

        panel.Children.Add(Ui.Section("刷新"));

        int[] pollOptions = { 30, 60, 120, 300, 600 };
        panel.Children.Add(Ui.Card(
            Ui.Row("电量刷新间隔", "电量变化很慢，间隔短只会更费电。",
                Ui.Combo(pollOptions, pollOptions.Contains(_settings.PollSeconds) ? _settings.PollSeconds : 60,
                    seconds => UpdateSettings(s => s.PollSeconds = seconds),
                    seconds => seconds >= 60 ? $"{seconds / 60} 分钟" : $"{seconds} 秒"))));

        return panel;
    }

    // ---- 关于 ----

    private static UIElement BuildAboutPage()
    {
        var panel = new StackPanel();

        panel.Children.Add(Ui.Title("关于"));
        panel.Children.Add(Ui.Subtitle(
            "一个替代官方 WL MOUSE HUB 的小工具。官方那个是 Electron 应用，" +
            "122 MB 安装包、常驻几百 MB，只为了看一个电量数字。"));

        panel.Children.Add(Ui.Section("实现"));
        panel.Children.Add(Ui.Card(
            Info("界面", "WPF + 手写的 Fluent 样式"),
            Info("任务栏挂件", "SetParent 到 Shell_TrayWnd"),
            Info("电量协议", "WLmouse 私有 HID（厂商页 0xFFFF）")));

        panel.Children.Add(Ui.Section("说明"));
        panel.Children.Add(Ui.Card(
            new TextBlock
            {
                Text = "电量、固件通过私有 Feature Report 读取；DPI、回报率等配置" +
                       "写在鼠标板载内存里，改完立刻生效。\n\n" +
                       "本程序不是官方软件，协议来自对官方驱动的逆向分析，" +
                       "固件更新后可能失效。",
                FontSize = 13,
                Foreground = Ui.Res("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8),
            }));

        return panel;
    }

    // ---- 交互 ----

    private static CheckBox Toggle(bool value, Action<bool> onChanged)
    {
        CheckBox box = Ui.Toggle(value);
        box.Checked += (_, _) => onChanged(true);
        box.Unchecked += (_, _) => onChanged(false);
        return box;
    }

    /// <summary>执行一次配置写入，并在失败时提示。</summary>
    private async void Change(Func<MouseConfigClient, bool> action, string description)
    {
        if (_snapshotState is not null) return;

        StatusBarText.Text = $"正在写入：{description}…";

        bool ok = await _battery.ChangeConfigAsync(action);

        StatusBarText.Text = ok
            ? $"已保存：{description}"
            : $"{description} 失败——鼠标可能休眠，或该型号不支持这项设置";
    }

    private void UpdateSettings(Action<AppSettings> mutate)
    {
        // --shot-ui 模式下窗口用的是临时设置对象，绝不能写回用户的配置文件：
        // WPF 在控件挂到可视化树时会补发一次 Checked/ValueChanged，
        // 不拦住就会把默认值覆盖上去。
        if (_snapshotState is not null) return;

        mutate(_settings);
        _settings.Save();
        _onSettingsChanged?.Invoke();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        StatusBarText.Text = "正在刷新…";
        _battery.RequestRefresh();
        _ = _battery.RefreshConfigAsync();
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AutoStart.Set(AutoStartCheck.IsChecked == true);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _battery.Updated -= OnBatteryUpdated;
        _battery.ConfigUpdated -= OnConfigUpdated;
        base.OnClosed(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // 最大化时窗口边框会被系统压到屏幕外，补一圈内边距免得内容贴边
        RootBorder.Padding = WindowState == WindowState.Maximized
            ? new Thickness(7)
            : new Thickness(0);
    }
}
