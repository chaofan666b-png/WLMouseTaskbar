using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WLMouseTaskbar;

/// <summary>
/// 嵌进任务栏的电量控件。
///
/// 实现方式照搬 FluentFlyout 的 Taskbar Widget：
///   1. 窗口风格 WS_POPUP → WS_CHILD，SetParent 到 Shell_TrayWnd
///   2. SetWindowPos 用「任务栏客户区坐标」定位（窗口父子关系改变后坐标系也变了）
///   3. SetWindowRgn 把窗口裁成圆角胶囊，透明区域不挡任务栏点击
///   4. WS_EX_NOACTIVATE + WM_MOUSEACTIVATE → MA_NOACTIVATE，点它不抢焦点
///
/// Windows 8 起允许 layered 子窗口，所以 AllowsTransparency 与 WS_CHILD 可以共存。
/// </summary>
public partial class TaskbarWidgetWindow : Window
{
    private static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(1200);
    private const double CornerRadiusDip = 7;

    /// <summary>和锚点之间固定的最小间距（DIP），约等于系统托盘图标之间的空隙。</summary>
    private const double BaseGapDip = 7;

    /// <summary>
    /// 电量液面能占的最大宽度（DIP）。= 电池外壳宽度 22 - 左右内缩各 1.9，
    /// 必须和 XAML 里 Fill 的 Margin 对得上，否则满电时也填不满。
    /// </summary>
    private const double FillWidthBase = 22 - 1.9 * 2;

    private readonly BatteryService _battery;
    private readonly AppSettings _settings;

    // 菜单里 DPI / 回报率项是动态生成的，用一次性的 async 处理器
    private readonly DispatcherTimer _timer;

    private IntPtr _hwnd;
    private IntPtr _taskbar;

    /// <summary>上一轮胶囊的屏幕矩形，用于在像素采样时排除自己画出来的像素。</summary>
    private Native.RECT _pillScreenRect;
    private uint _taskbarCreatedMessage;
    private bool _positioning;
    private bool _closed;

    public Action? RequestOpenSettings { get; set; }

    public TaskbarWidgetWindow(BatteryService battery, AppSettings settings)
    {
        _battery = battery;
        _settings = settings;

        InitializeComponent();

        WindowStartupLocation = WindowStartupLocation.Manual;
        // 首次定位前放到屏幕外，避免闪一下
        Left = -20000;
        Top = -20000;

        _battery.Updated += OnBatteryUpdated;

        _timer = new DispatcherTimer { Interval = PositionInterval };
        _timer.Tick += (_, _) =>
        {
            EnsureAttached();
            UpdatePosition();
        };
        _timer.Start();

        Apply(BatteryState.Initial);
        ApplyMenuTexts();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        _taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 点击控件时不要激活窗口，否则会抢走任务栏/开始菜单的焦点
        if (msg == Native.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Native.MA_NOACTIVATE);
        }

        if (msg == Native.WM_DPICHANGED || msg == Native.WM_DPICHANGED_AFTERPARENT)
        {
            Dispatcher.BeginInvoke(() =>
            {
                InvalidateMeasure();
                InvalidateArrange();
                UpdateLayout();
                UpdatePosition();
            }, DispatcherPriority.Loaded);
        }

        // explorer.exe 重启后任务栏窗口会重建，需要重新挂载
        if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            _taskbar = IntPtr.Zero;
            Dispatcher.BeginInvoke(() =>
            {
                EnsureAttached();
                UpdatePosition();
            }, DispatcherPriority.Background);
        }

        if (msg == 0x001A) // WM_SETTINGCHANGE：主题 / 缩放变化时更新配色
        {
            Dispatcher.BeginInvoke(() => Apply(_battery.State), DispatcherPriority.Background);
        }

        // 这些消息会引发任务栏的 UI Automation / 布局风暴，直接吞掉（FluentFlyout 同款处理）
        switch (msg)
        {
            case Native.WM_GETOBJECT:
            case Native.WM_SHOWWINDOW:
            case Native.WM_WINDOWPOSCHANGING:
            case Native.WM_NCCALCSIZE:
            case Native.WM_IME_SETCONTEXT:
            case Native.WM_IME_NOTIFY:
                handled = true;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>确保窗口是当前任务栏的子窗口（任务栏重建 / 首次启动都会走到这里）。</summary>
    private void EnsureAttached()
    {
        if (_closed || _hwnd == IntPtr.Zero) return;

        IntPtr taskbar = Native.MainTaskbar();
        if (taskbar == IntPtr.Zero)
        {
            _taskbar = IntPtr.Zero;
            return;
        }

        if (_taskbar == taskbar && Native.GetParent(_hwnd) == taskbar) return;

        _taskbar = taskbar;
        Native.MakeChildOf(_hwnd, taskbar);
    }

    private void UpdatePosition()
    {
        if (_closed || _positioning || _hwnd == IntPtr.Zero) return;
        _positioning = true;

        try
        {
            bool hideForNoMouse = _settings.HideWhenDisconnected && !_battery.State.DevicePresent;
            if (!_settings.ShowWidget || hideForNoMouse)
            {
                if (Visibility != Visibility.Collapsed) Visibility = Visibility.Collapsed;
                return;
            }

            IntPtr taskbar = Native.MainTaskbar();
            if (taskbar == IntPtr.Zero) return;
            if (Native.GetParent(_hwnd) != taskbar) return;

            Native.GetWindowRect(taskbar, out Native.RECT tb);
            int tbWidth = tb.Right - tb.Left;
            int tbHeight = tb.Bottom - tb.Top;
            if (tbWidth <= 0 || tbHeight <= 0) return;

            // 竖排任务栏暂不支持（布局需要旋转，后续再做）
            if (tbHeight > tbWidth)
            {
                if (Visibility != Visibility.Collapsed) Visibility = Visibility.Collapsed;
                return;
            }

            double dpi = Native.GetDpiForWindow(taskbar) / 96.0;
            if (dpi <= 0) dpi = 1;

            // 这个窗口作为任务栏子窗口运行时，WPF 常按 96 DPI 布局（窗口创建时还没有父窗口）。
            // 用 LayoutTransform 补上 任务栏DPI / 窗口DPI 的差值，让控件和原生任务栏元素等高。
            DpiScale windowDpi = VisualTreeHelper.GetDpi(this);
            double scale = dpi / (windowDpi.DpiScaleX <= 0 ? 1 : windowDpi.DpiScaleX);
            Diag($"taskbarDpi={dpi:0.###} windowDpi={windowDpi.DpiScaleX:0.###} scale={scale:0.###}");
            if (Math.Abs(scale - 1) > 0.01)
            {
                var transform = new ScaleTransform(scale, scale);
                if (Pill.LayoutTransform is not ScaleTransform current ||
                    Math.Abs(current.ScaleX - scale) > 0.01)
                {
                    Pill.LayoutTransform = transform;
                }
            }
            else if (Pill.LayoutTransform is not null)
            {
                Pill.LayoutTransform = null;
            }

            // 量出 Pill 的内容尺寸（逻辑坐标，已含 LayoutTransform）
            Pill.Width = double.NaN;
            Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // 留 2 DIP 余量：DesiredSize 正好等于内容宽度时，乘上 1.25 的缩放再取整，
            // 最后一两个像素会被 SetWindowRgn 裁掉，末尾的字（例如「%」）就没了。
            double pillW = Math.Ceiling(Pill.DesiredSize.Width) + 2;
            double pillH = Math.Ceiling(Pill.DesiredSize.Height);
            if (pillW <= 0 || pillH <= 0) return;
            Pill.Width = pillW;

            if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;

            // 窗口铺满整条任务栏。WPF 逻辑坐标 = 物理 px / 窗口缩放。
            double windowScale = windowDpi.DpiScaleX <= 0 ? 1 : windowDpi.DpiScaleX;
            double tbLogicalW = tbWidth / windowScale;
            double tbLogicalH = tbHeight / windowScale;
            Root.Width = tbLogicalW;
            Root.Height = tbLogicalH;
            Width = tbLogicalW;
            Height = tbLogicalH;
            UpdateLayout();

            // 任务栏客户区原点（多显示器 / 竖排时不一定为 0,0）
            var origin = new Native.POINT { X = tb.Left, Y = tb.Top };
            Native.ScreenToClient(taskbar, ref origin);

            // 计算水平位置（物理像素）
            int widgetWidth = (int)Math.Round(pillW * windowScale);
            int widgetHeight = (int)Math.Round(pillH * windowScale);
            // 基础间距保证不会和锚点贴到重叠；WidgetOffset 是用户额外加的余量
            int gap = (int)((_settings.WidgetOffset + BaseGapDip) * dpi);

            // 排除「自己」时要用胶囊的屏幕矩形。窗口本身铺满整条任务栏，
            // 拿它当排除范围等于把所有列都跳过。
            Native.RECT selfRect = _pillScreenRect;

            int anchorX = tbWidth;   // 默认任务栏右端
            IntPtr tray = Native.TrayNotify(taskbar);
            if (tray != IntPtr.Zero)
            {
                Native.GetWindowRect(tray, out Native.RECT trayRect);
                var trayPoint = new Native.POINT { X = trayRect.Left, Y = trayRect.Top };
                Native.ScreenToClient(taskbar, ref trayPoint);
                if (trayPoint.X > 0 && trayPoint.X < tbWidth) anchorX = trayPoint.X;

                // 通知区域这个容器的左边缘比第一个图标（隐藏图标箭头）要左不少，
                // 直接贴着容器会留一条几十像素的空隙。Windows 11 的通知区不暴露
                // 子窗口，所以采样像素找第一列有内容的边缘，再退一点点。
                int trayWidth = trayRect.Right - trayRect.Left;
                int searchWidth = Math.Min(trayWidth, (int)(180 * dpi));
                int sampleHeight = Math.Max(6, tbHeight / 2);
                int sampleTop = tb.Top + (tbHeight - sampleHeight) / 2;

                // 排除自己的窗口：它贴着通知区域，很可能已经压进扫描范围里，
                // 不排除就会把自己的右边缘当成第一个托盘图标，锚点自己追自己。
                int contentLeft = Native.FindTrayContentLeft(
                    trayRect.Left, sampleTop, searchWidth, sampleHeight,
                    selfRect.Left, selfRect.Right);

                if (contentLeft > 0)
                    anchorX = trayPoint.X + contentLeft - (int)(2 * dpi);

                Diag($"tray anchor={anchorX} contentLeft={contentLeft} trayLeft={trayPoint.X}");
            }

            int startX = 0;          // 开始按钮右边缘
            IntPtr start = Native.FindTaskbarChild(taskbar, "Start");
            if (start != IntPtr.Zero)
            {
                Native.GetWindowRect(start, out Native.RECT startRect);
                var startPoint = new Native.POINT { X = startRect.Right, Y = startRect.Top };
                Native.ScreenToClient(taskbar, ref startPoint);
                if (startPoint.X > 0 && startPoint.X < tbWidth) startX = startPoint.X;
            }

            // 任务栏上其他挂件（FluentFlyout 的媒体控件等）占住的横向区间。
            // 把自己当前的矩形传进去，让采样避开自画像素造成的反馈。
            var spans = _settings.AvoidOtherWidgets
                ? Native.ForeignWidgetSpans(taskbar, _hwnd, selfRect)
                : new List<(int Left, int Right, string Class)>();

            int xPhys = _settings.WidgetPosition switch
            {
                1 => (tbWidth - widgetWidth) / 2,
                2 => startX + gap,
                _ => anchorX - widgetWidth - gap,
            };

            // 和别人的挂件撞上了就往左让，直到空出来
            if (_settings.WidgetPosition != 2)
            {
                for (int guard = 0; guard < 8; guard++)
                {
                    var hit = spans.FirstOrDefault(s => xPhys < s.Right + gap && xPhys + widgetWidth > s.Left - gap);
                    if (hit.Class is null) break;
                    xPhys = hit.Left - widgetWidth - gap;
                }
            }

            // 别跑出任务栏
            xPhys = Math.Clamp(xPhys, 0, Math.Max(0, tbWidth - widgetWidth));

            int yPhys = (tbHeight - widgetHeight) / 2;

            // 窗口覆盖整个任务栏，位置固定在客户区原点
            Native.SetWindowPos(_hwnd, Native.HWND_TOP, origin.X, origin.Y, tbWidth, tbHeight,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

            // Pill 在窗口内按逻辑坐标摆放
            Canvas.SetLeft(Pill, xPhys / windowScale);
            Canvas.SetTop(Pill, yPhys / windowScale);

            // 记下胶囊的屏幕矩形，下一轮采样时把自己排除掉
            _pillScreenRect = new Native.RECT
            {
                Left = tb.Left + xPhys,
                Top = tb.Top + yPhys,
                Right = tb.Left + xPhys + widgetWidth,
                Bottom = tb.Top + yPhys + widgetHeight,
            };

            Diag($"tb={tbWidth}x{tbHeight} pill={widgetWidth}x{widgetHeight} at {xPhys},{yPhys} " +
                 $"pos={_settings.WidgetPosition} gap={gap} anchorX={anchorX} startX={startX} " +
                 $"trayHwnd={tray != IntPtr.Zero} spans={string.Join(";", spans.Select(s => $"{s.Left}..{s.Right}"))}");

            // Region 只保留 Pill 那块，其余区域不拦截任务栏点击
            ApplyRegion(xPhys, yPhys, widgetWidth, widgetHeight, dpi);
        }
        catch (Exception ex)
        {
            Diag($"定位失败：{ex.GetType().Name} {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"TaskbarWidget 定位失败：{ex}");
        }
        finally
        {
            _positioning = false;
        }
    }

    internal static void Diag(string message)
    {
        if (Environment.GetEnvironmentVariable("WLMOUSE_DIAG") != "1") return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "wlmouse-diag.txt"),
                $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志写不进去就算了
        }
    }

    private void ApplyRegion(int x, int y, int width, int height, double dpi)
    {
        int radius = (int)(CornerRadiusDip * 2 * dpi);
        IntPtr region = Native.CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, radius, radius);
        if (region == IntPtr.Zero) return;

        // SetWindowRgn 成功时由系统接管 region 的所有权
        if (!Native.SetWindowRgn(_hwnd, region, true))
            Native.DeleteObject(region);
    }

    private void OnBatteryUpdated(BatteryState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Apply(state);
            UpdatePosition();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 开发用：强制显示指定电量（--fake-percent），方便鼠标休眠或没接设备时
    /// 检查任务栏上的实际渲染效果。null 表示照常显示真实读数。
    /// </summary>
    public static int? DevPercentOverride { get; set; }

    /// <summary>把电量状态画到控件上。</summary>
    private void Apply(BatteryState state)
    {
        if (DevPercentOverride is int fake)
            state = state with { Percent = fake, DevicePresent = true, Charging = false };

        Palette palette = Palette.For(ThemeHelper.IsLightTaskbar());

        // 透明底，不画任何底板：和系统自带的电量图标一样浮在任务栏上
        Pill.Background = Brushes.Transparent;
        Pill.BorderThickness = new Thickness(0);

        var foreground = new SolidColorBrush(palette.TextPrimary);
        foreground.Freeze();

        Shell.BorderBrush = foreground;
        Fill.Background = foreground;
        Tip.Fill = foreground;
        Bolt.Fill = foreground;
        PercentText.Foreground = foreground;

        string text = state.Percent is int p ? $"{p}%" : "—";
        if (PercentText.Text != text)
        {
            PercentText.Text = text;
            // 宽度会变，位置下一轮重算
            UpdatePosition();
        }

        // 电量液面宽度：基数 = 外壳宽度 22 - 左右各 1.9 的内缩
        double ratio = state.Percent is int percent ? Math.Clamp(percent, 0, 100) / 100.0 : 0;
        Fill.Width = FillWidthBase * ratio;

        // 充电时把液面换成闪电，和 Windows 的处理一致
        bool charging = state.Charging && state.Percent is not null;
        Fill.Visibility = charging ? Visibility.Collapsed : Visibility.Visible;
        Bolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;

        // 没有设备时整体淡下去，不喧宾夺主
        Pill.Opacity = state.DevicePresent && state.Percent is not null ? 1.0 : 0.45;

        Pill.ToolTip = state.DevicePresent
            ? state.Percent is int pp
                ? $"WL Mouse  {pp}%{(state.Charging ? "  充电中" : "")}\n" +
                  $"更新于 {state.Timestamp:HH:mm:ss}\n左键查看详情 · 右键更多"
                : $"WL Mouse  {state.Error ?? "读取中"}"
            : "WL Mouse：未检测到接收器";

        ApplyMenuTexts();
    }

    private void ApplyMenuTexts()
    {
        BatteryState s = _battery.State;
        MenuBattery.Header = s.DevicePresent
            ? s.Percent is int p
                ? $"电量：{p}%" + (s.Charging ? "（充电中）" : "")
                : "电量：读取失败"
            : "电量：未连接";
    }

    public void RefreshNow() => _battery.RequestRefresh();

    /// <summary>开发用：直接把给定状态画到控件上。</summary>
    public void ApplySnapshotState(BatteryState state) => Apply(state);

    /// <summary>把控件渲染成 PNG，用于开发期核对外观（--snapshot 模式）。</summary>
    public void RenderSnapshot(string path, bool darkTaskbar, double scale = 4)
    {
        Pill.LayoutTransform = null;
        Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Pill.Arrange(new Rect(Pill.DesiredSize));
        Pill.UpdateLayout();

        double w = Pill.ActualWidth;
        double h = Pill.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale,
            PixelFormats.Pbgra32);

        // 先在离线容器里合成，避免直接渲染受窗口位置影响
        var host = new Grid
        {
            Width = w,
            Height = h,
            Background = new SolidColorBrush(darkTaskbar
                ? Color.FromRgb(0x20, 0x20, 0x20)
                : Color.FromRgb(0xF3, 0xF3, 0xF3)),
        };
        host.Children.Add(new Border
        {
            Width = w,
            Height = h,
            Background = new VisualBrush(Pill)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                Viewbox = new Rect(0, 0, w, h),
                ViewboxUnits = BrushMappingMode.Absolute,
            },
        });
        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        host.UpdateLayout();

        target.Render(host);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
        using FileStream fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>设置变化后重新测量、重新定位。</summary>
    public void RequestReposition() =>
        Dispatcher.BeginInvoke(() =>
        {
            InvalidateMeasure();
            UpdateLayout();
            UpdatePosition();
        }, DispatcherPriority.Loaded);

    // ---- 交互 ----

    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        RequestOpenSettings?.Invoke();
        e.Handled = true;
    }

    private void Pill_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 右键菜单需要窗口短暂获得焦点才能正确关闭，
        // child + NOACTIVATE 窗口下用鼠标捕获的方式代替
        if (Pill.ContextMenu is { } menu)
        {
            menu.PlacementTarget = Pill;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        if (Pill.Opacity > 0.5) Pill.Opacity = 1.0;
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        // 未连接时整体是淡的，别把「淡」当成常态透明度覆盖掉
        if (Pill.Opacity > 0.5) Pill.Opacity = 0.92;
    }

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        ApplyMenuTexts();
        MenuTrayToggle.IsChecked = _settings.ShowTrayIcon;
        RebuildQuickMenus();
    }

    /// <summary>
    /// 右键菜单里的 DPI / 回报率子菜单。数据来自最近一次读到的配置，
    /// 点选后直接写进鼠标，界面下一轮刷新。
    /// </summary>
    private void RebuildQuickMenus()
    {
        MouseConfig config = _battery.Config;

        MenuDpi.Items.Clear();
        if (config.DpiStages.Length == 0)
        {
            MenuDpi.Items.Add(new MenuItem { Header = "暂不可用", IsEnabled = false });
        }
        else
        {
            for (int i = 0; i < config.DpiStages.Length; i++)
            {
                int index = i;
                var item = new MenuItem
                {
                    Header = $"第 {i + 1} 档 · {config.DpiStages[i]}",
                    IsCheckable = true,
                    IsChecked = i == config.ActiveStage,
                };
                item.Click += async (_, _) =>
                    await _battery.ChangeConfigAsync(c => c.SetActiveDpiStage(index));
                MenuDpi.Items.Add(item);
            }
        }

        MenuPolling.Items.Clear();
        int maxHz = PollingRates.MaxFor(config.ProductId);
        int[] rates = PollingRates.All.Where(hz => hz <= maxHz).ToArray();
        if (rates.Length == 0) rates = PollingRates.All;

        foreach (int hz in rates)
        {
            int value = hz;
            var item = new MenuItem
            {
                Header = $"{hz} Hz",
                IsCheckable = true,
                IsChecked = config.PollingRateHz == hz,
            };
            item.Click += async (_, _) =>
                await _battery.ChangeConfigAsync(c => c.SetPollingRate(value));
            MenuPolling.Items.Add(item);
        }

        Diag($"menu dpi={MenuDpi.Items.Count} polling={MenuPolling.Items.Count} " +
             $"rate={config.PollingRateHz} stage={config.ActiveStage}");
    }

    private void MenuRefresh_Click(object sender, RoutedEventArgs e) => _battery.RequestRefresh();

    private void MenuSettings_Click(object sender, RoutedEventArgs e) => RequestOpenSettings?.Invoke();

    private void MenuTrayToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowTrayIcon = MenuTrayToggle.IsChecked;
        _settings.Save();
        TrayToggleRequested?.Invoke();
    }

    public event Action? TrayToggleRequested;

    public void SyncMenuState()
    {
        MenuTrayToggle.IsChecked = _settings.ShowTrayIcon;
        ApplyMenuTexts();
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) =>
        System.Windows.Application.Current.Shutdown();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        _battery.Updated -= OnBatteryUpdated;
        base.OnClosed(e);
    }
}

/// <summary>
/// 控件配色。
///
/// 透明底之后只剩前景一种颜色：任务栏是深色就用白色，浅色就用近黑——和系统
/// 托盘里那几个自带的图标（音量、网络、电池）是同一套规则。电量高低不再变色，
/// 因为笔记本电脑的电量图标本身也不变色，变色反而和系统图标不协调。
/// </summary>
internal readonly record struct Palette(Color TextPrimary)
{
    public static Palette For(bool lightTaskbar) => new(lightTaskbar
        ? Color.FromArgb(0xE4, 0x00, 0x00, 0x00)
        : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
}

/// <summary>从注册表读取任务栏（系统）深浅色。</summary>
internal static class ThemeHelper
{
    /// <summary>开发用：强制指定深浅色（null = 读注册表）。</summary>
    public static bool? OverrideLight { get; set; }

    public static bool IsLightTaskbar()
    {
        if (OverrideLight is bool forced) return forced;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }
}
