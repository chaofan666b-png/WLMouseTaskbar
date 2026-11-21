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
    public const int WS_EX_TOOLWINDOW = 0x00000080;

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
    /// 采样屏幕上一小块区域，判断里面有没有「东西」（文字、图标、卡片边缘）。
    ///
    /// 只看窗口 region 是不够的：任务栏挂件（FluentFlyout 之类）在不显示内容时
    /// 窗口和 region 都还在，只是画出来是透明的。实测空任务栏区域相邻像素的
    /// 平均亮度差约 1.2，里面真有控件时是 12 以上，阈值取 3.5 足够区分。
    /// </summary>
    public static bool RegionHasContent(int screenX, int screenY, int width, int height,
        double threshold = 3.5)
    {
        if (width <= 1 || height <= 1) return false;

        try
        {
            using var bitmap = new System.Drawing.Bitmap(width, height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(screenX, screenY, 0, 0,
                    new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);
            }

            System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            double sum = 0;
            long count = 0;

            try
            {
                int stride = data.Stride;
                var buffer = new byte[stride * height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                // 每隔 3 行采一条，够用且快
                for (int y = 0; y < height; y += 3)
                {
                    int row = y * stride;
                    double previous = -1;

                    for (int x = 0; x < width; x++)
                    {
                        int i = row + x * 4;
                        double luma = 0.299 * buffer[i + 2] + 0.587 * buffer[i + 1] + 0.114 * buffer[i];

                        if (previous >= 0)
                        {
                            sum += Math.Abs(luma - previous);
                            count++;
                        }

                        previous = luma;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return count > 0 && sum / count > threshold;
        }
        catch
        {
            // 采不到就当作有内容，宁可让开也不要盖住别人
            return true;
        }
    }

    /// <summary>
    /// 在通知区域里找最左侧那个可见图标（Windows 11 是「显示隐藏的图标」箭头）的左边缘，
    /// 返回相对 <paramref name="screenLeft"/> 的偏移；没找到返回 -1。
    /// Windows 11 的通知区不暴露子窗口，只能靠像素找。
    /// </summary>
    /// <param name="excludeLeft">要跳过的横向区间（屏幕坐标），一般传自己的窗口矩形。</param>
    /// <param name="excludeRight">
    /// 不排除的话会把控件自己画出来的像素当成托盘图标，锚点就会跟着自己跑。
    /// </param>
    public static int FindTrayContentLeft(int screenLeft, int screenTop, int width, int height,
        int excludeLeft = int.MinValue, int excludeRight = int.MinValue)
    {
        if (width <= 4 || height <= 4) return -1;

        try
        {
            using var bitmap = new System.Drawing.Bitmap(width, height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(screenLeft, screenTop, 0, 0,
                    new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);
            }

            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                int stride = data.Stride;
                var buffer = new byte[stride * height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                // 逐列算水平梯度，第一列出现明显边缘的就是图标左沿
                for (int x = 1; x < width; x++)
                {
                    int screenX = screenLeft + x;
                    if (screenX >= excludeLeft && screenX <= excludeRight) continue;

                    double columnGradient = 0;

                    for (int y = 0; y < height; y += 2)
                    {
                        int row = y * stride;
                        int i = row + x * 4;
                        int j = row + (x - 1) * 4;

                        double a = 0.299 * buffer[i + 2] + 0.587 * buffer[i + 1] + 0.114 * buffer[i];
                        double b = 0.299 * buffer[j + 2] + 0.587 * buffer[j + 1] + 0.114 * buffer[j];
                        columnGradient += Math.Abs(a - b);
                    }

                    // 单列累计超过 90 基本就是字形/图标的竖直边
                    if (columnGradient > 90) return x;
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch
        {
            // 采不到就回退到容器边缘
        }

        return -1;
    }

    /// <summary>
    /// 任务栏上「别人的挂件」占用的横向区间（物理像素，相对任务栏左边）。
    ///
    /// 任务栏上的挂件（FluentFlyout、各种电量条之类）大多是「窗口铺满整条任务栏 +
    /// region 裁剪」的做法，光看窗口矩形是整条任务栏，只有 region 才是真实可见范围。
    /// 标准部件（通知区域、任务视图、开始按钮、合成桥）在这里跳过。
    /// </summary>
    /// <param name="selfRect">
    /// 自己的窗口矩形（屏幕坐标）。和某块区域重叠时不再重新采样——采到的会是
    /// 自己画出来的像素，判成「被占用」后让开，让开后采样又变空，控件就会在
    /// 两个位置之间来回跳。这种情况下沿用上一轮的判定。
    /// </param>
    /// <summary>上一轮的「这块区域里到底有没有东西」判定，按窗口类名记。见下面的防抖说明。</summary>
    private static readonly Dictionary<string, bool> _spanCache = new(StringComparer.Ordinal);

    public static List<(int Left, int Right, string Class)> ForeignWidgetSpans(
        IntPtr taskbar, IntPtr self, RECT? selfRect = null)
    {
        var spans = new List<(int, int, string)>();
        if (taskbar == IntPtr.Zero) return spans;

        GetWindowRect(taskbar, out RECT tb);
        int tbWidth = tb.Right - tb.Left;
        int tbHeight = tb.Bottom - tb.Top;
        if (tbWidth <= 0 || tbHeight <= 0) return spans;

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

            // 关键一步：region 还在不代表真的画了东西。FluentFlyout 不显示媒体时
            // 窗口和 region 都保留着，只是内容透明。只有采样到真实像素才算占位，
            // 否则控件会被一块看不见的区域顶开，看着就像没贴着通知区域。
            bool overlapsSelf = selfRect is { } selfBox
                                && selfBox.Right > b.Left && selfBox.Left < b.Right;

            bool occupied;
            if (overlapsSelf && _spanCache.TryGetValue(cls, out bool cached))
            {
                occupied = cached;
            }
            else
            {
                // 采任务栏中间那条带，避开边缘的圆角与阴影
                int sampleHeight = Math.Max(4, Math.Min(b.Bottom - b.Top, tbHeight / 2));
                int sampleY = tb.Top + (tbHeight - sampleHeight) / 2;

                occupied = RegionHasContent(tb.Left + left, sampleY, width, sampleHeight);
                if (!overlapsSelf) _spanCache[cls] = occupied;
            }

            if (!occupied) continue;

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
