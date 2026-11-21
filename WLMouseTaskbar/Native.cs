using System.Runtime.InteropServices;
using System.Text;

namespace WLMouseTaskbar;

/// <summary>
/// 任务栏嵌入所需的 Win32 API。做法与 FluentFlyout 的 TaskbarWidget 一致：
/// 把窗口的 WS_POPUP 换成 WS_CHILD，SetParent 到 Shell_TrayWnd，
/// 再用 SetWindowPos 以任务栏客户区坐标定位。
/// </summary>
internal static class Native
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const int WS_CHILD = 0x40000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int WM_GETOBJECT = 0x003D;
    public const int WM_SHOWWINDOW = 0x0018;
    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_IME_SETCONTEXT = 0x0281;
    public const int WM_IME_NOTIFY = 0x0282;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_DPICHANGED_AFTERPARENT = 0x02E3;
    public const int WM_MOUSEACTIVATE = 0x0021;

    public const int MA_NOACTIVATE = 3;

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    // CreateRectRgn / CreateRoundRectRgn / GetRgnBox 都是 gdi32 的导出函数。
    // user32 里只有 CreateRoundRectRgn 碰巧能被转发到，CreateRectRgn 会直接
    // 抛 EntryPointNotFoundException。
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
        int widthEllipse, int heightEllipse);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string message);

    /// <summary>主任务栏（Shell_TrayWnd）。</summary>
    public static IntPtr MainTaskbar() => FindWindow("Shell_TrayWnd", null);

    /// <summary>任务栏右侧的通知区域容器，用来把控件贴在它左边。</summary>
    public static IntPtr TrayNotify(IntPtr taskbar) =>
        FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetRgnBox(IntPtr region, out RECT rect);

    /// <summary>取窗口的可见区域包围盒。没有设置 region 或取不到时返回 null。</summary>
    public static RECT? GetRegionBox(IntPtr hwnd)
    {
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) return null;

        try
        {
            // 返回 0 表示没有 region；复杂区域的类型是 2。分层窗口上不可靠，所以
            // 调用方要把「取不到」当成「没有 region」，而不是当成错误。
            int type = GetWindowRgn(hwnd, region);
            if (type == 0) return null;
            if (GetRgnBox(region, out RECT box) == 0) return null;
            return box;
        }
        finally
        {
            DeleteObject(region);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);

    /// <summary>
    /// 任务栏上「别人的挂件」占用的横向区间（物理像素，相对任务栏左边）。
    ///
    /// 任务栏上的挂件（FluentFlyout、各种电量条之类）大多是「窗口铺满整条任务栏 +
    /// region 裁剪」的做法，光看窗口矩形是整条任务栏，只有 region 才是真实可见范围。
    /// 标准部件（通知区域、任务视图、开始按钮、合成桥）在这里跳过。
    /// </summary>
    public static List<(int Left, int Right, string Class)> ForeignWidgetSpans(IntPtr taskbar, IntPtr self)
    {
        var spans = new List<(int, int, string)>();
        if (taskbar == IntPtr.Zero) return spans;

        GetWindowRect(taskbar, out RECT tb);
        int tbWidth = tb.Right - tb.Left;
        if (tbWidth <= 0) return spans;

        var name = new StringBuilder(256);
        IntPtr child = IntPtr.Zero;

        while ((child = FindWindowEx(taskbar, child, null, null)) != IntPtr.Zero)
        {
            if (child == self) continue;

            name.Clear();
            if (GetClassName(child, name, name.Capacity) == 0) continue;
            string cls = name.ToString();

            if (IsStandardTaskbarPart(cls)) continue;

            RECT? box = GetRegionBox(child);
            if (box is not { } b) continue;

            int left = b.Left - tb.Left;
            int right = b.Right - tb.Left;
            int width = right - left;

            // 整条任务栏宽的区域说明它没裁剪，不算「占位」
            if (width <= 0 || width >= tbWidth * 0.9) continue;

            // 只看任务栏右半边的挂件（左边的那些不会和我们抢位置）
            if (left < tbWidth / 2) continue;

            spans.Add((left, right, cls));
        }

        return spans;
    }

    private static bool IsStandardTaskbarPart(string cls) =>
        cls.Contains("TrayNotifyWnd", StringComparison.OrdinalIgnoreCase)
        || cls.Contains("TrayShowDesktopButtonWnd", StringComparison.OrdinalIgnoreCase)
        || cls.Contains("ReBarWindow32", StringComparison.OrdinalIgnoreCase)
        || cls.Contains("MSTaskSwWClass", StringComparison.OrdinalIgnoreCase)
        || cls.Contains("MSTaskListWClass", StringComparison.OrdinalIgnoreCase)
        || cls.Contains("Start", StringComparison.OrdinalIgnoreCase)
        || cls.Contains("Composition.DesktopWindowContentBridge", StringComparison.OrdinalIgnoreCase)
        || cls.Contains("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
        || cls.Contains("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase);

    /// <summary>在任务栏的子窗口里找类名包含指定片段的那个（如 "Start"）。</summary>
    public static IntPtr FindTaskbarChild(IntPtr taskbar, string classFragment)
    {
        var name = new StringBuilder(256);
        IntPtr child = IntPtr.Zero;

        while ((child = FindWindowEx(taskbar, child, null, null)) != IntPtr.Zero)
        {
            name.Clear();
            if (GetClassName(child, name, name.Capacity) == 0) continue;
            if (name.ToString().Contains(classFragment, StringComparison.OrdinalIgnoreCase))
                return child;
        }

        return IntPtr.Zero;
    }

    /// <summary>把窗口变成任务栏的子窗口。</summary>
    public static void MakeChildOf(IntPtr hwnd, IntPtr parent)
    {
        int style = GetWindowLong(hwnd, GWL_STYLE);
        style = (style & ~WS_POPUP) | WS_CHILD;
        SetWindowLong(hwnd, GWL_STYLE, style);

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);

        SetParent(hwnd, parent);
    }
}
