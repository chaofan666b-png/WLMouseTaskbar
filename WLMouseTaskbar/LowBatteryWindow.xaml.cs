using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WLMouseTaskbar;

/// <summary>
/// 低电量提示。外观参考 FluentFlyout 的大写锁定提示（LockWindow）：
/// 一张小圆角卡片，图标 + 两行文字 + 底部强调色指示条，从下往上滑入，
/// 停留几秒后淡出。
///
/// 用独立的置顶窗口而不是任务栏子窗口，因为它要盖在任务栏上方、
/// 而且只在需要时短暂出现；子窗口会跟着任务栏的坐标系走，反而更麻烦。
/// </summary>
public partial class LowBatteryWindow : Window
{
    private const double ShowMs = 260;
    private const double HoldMs = 3200;
    private const double HideMs = 420;

    /// <summary>卡片下沿距离任务栏的上边留出的空隙。</summary>
    private const double GapAboveTaskbar = 12;

    private readonly DispatcherTimer _timer = new();

    public LowBatteryWindow()
    {
        InitializeComponent();

        WindowStartupLocation = WindowStartupLocation.Manual;

        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            FadeOutAndClose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc);

        // 不抢焦点、不进 Alt+Tab，和系统通知一样是"路过"的
        int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
            exStyle | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Native.MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    /// <summary>显示一条提示。重复调用会重新计时，不会叠出多个窗口。</summary>
    public void Show(BatteryState state, int threshold)
    {
        int percent = state.Percent ?? 0;

        // 颜色：<10% 红，其余橙——和任务栏控件的电量分档一致
        Color accent = percent < 10
            ? Color.FromRgb(0xE8, 0x3B, 0x3B)
            : Color.FromRgb(0xE8, 0x9A, 0x1E);

        var accentBrush = new SolidColorBrush(accent);
        accentBrush.Freeze();

        var soft = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
        soft.Freeze();

        TitleText.Text = percent < 10 ? "鼠标电量即将耗尽" : "鼠标电量偏低";
        DetailText.Text = $"剩余 {percent}%，请及时充电";

        Shell.BorderBrush = accentBrush;
        Tip.Fill = accentBrush;
        Fill.Background = accentBrush;

        // 液面按电量，指示条按电量宽度（最低留一点可见）
        const double inner = 30 - 2 * 2;
        Fill.Width = Math.Max(1.5, inner * Math.Clamp(percent, 0, 100) / 100.0);
        Indicator.Fill = accentBrush;
        Indicator.Width = Math.Max(24, 60 * Math.Clamp(percent, 0, 100) / 100.0);

        PlaceAboveTaskbar();

        Opacity = 0;
        Show();

        var slide = new DoubleAnimation(Top + 18, Top, TimeSpan.FromMilliseconds(ShowMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ShowMs));
        BeginAnimation(TopProperty, slide);
        BeginAnimation(OpacityProperty, fade);

        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(ShowMs + HoldMs);
        _timer.Start();
    }

    /// <summary>摆在主屏任务栏上方、靠通知区域那一侧，和提示的来源呼应。</summary>
    private void PlaceAboveTaskbar()
    {
        UpdateLayout();

        Rect work = SystemParameters.WorkArea;
        double width = ActualWidth > 0 ? ActualWidth : Width;

        // WorkArea 是不含任务栏的区域，底部即任务栏上沿
        Left = work.Right - width - 24;
        Top = work.Bottom - Height - GapAboveTaskbar;

        // 别跑出屏幕左边
        if (Left < work.Left + 12) Left = work.Left + 12;
    }

    private void FadeOutAndClose()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(HideMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };

        fade.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Close();
        };

        BeginAnimation(OpacityProperty, fade);
    }
}
