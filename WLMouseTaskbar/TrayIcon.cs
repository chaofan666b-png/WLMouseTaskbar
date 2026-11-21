using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WLMouseTaskbar;

/// <summary>
/// 通知区域（传统托盘）图标。和任务栏控件并存，可分别开关。
/// 用 WinForms 的 NotifyIcon 而不是自己 P/Invoke Shell_NotifyIcon，
/// 它会自动处理 explorer.exe 重启后重新注册图标。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _batteryItem = new("电量：—") { Enabled = false };
    private readonly ToolStripMenuItem _widgetItem = new("在任务栏显示电量");
    private readonly ToolStripMenuItem _autoStartItem = new("开机自启");
    private readonly AppSettings _settings;

    private Icon? _current;

    public event Action? OpenSettingsRequested;
    public event Action? RefreshRequested;
    public event Action? WidgetVisibilityChanged;
    public event Action? TrayVisibilityChanged;

    public TrayIcon(AppSettings settings)
    {
        _settings = settings;

        _widgetItem.CheckOnClick = false;
        _widgetItem.Checked = settings.ShowWidget;
        _widgetItem.Click += (_, _) =>
        {
            _settings.ShowWidget = !_settings.ShowWidget;
            _settings.Save();
            _widgetItem.Checked = _settings.ShowWidget;
            WidgetVisibilityChanged?.Invoke();
        };

        _autoStartItem.CheckOnClick = false;
        _autoStartItem.Checked = AutoStart.IsEnabled;
        _autoStartItem.Click += (_, _) =>
        {
            AutoStart.Set(!AutoStart.IsEnabled);
            _autoStartItem.Checked = AutoStart.IsEnabled;
        };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _batteryItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("打开设置…", null, (_, _) => OpenSettingsRequested?.Invoke()),
            new ToolStripMenuItem("立即刷新", null, (_, _) => RefreshRequested?.Invoke()),
            new ToolStripSeparator(),
            _widgetItem,
            new ToolStripMenuItem("隐藏通知区图标", null, (_, _) =>
            {
                _settings.ShowTrayIcon = false;
                _settings.Save();
                TrayVisibilityChanged?.Invoke();
            }),
            _autoStartItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("退出", null, (_, _) => System.Windows.Application.Current.Shutdown()),
        });

        _icon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = settings.ShowTrayIcon,
            Text = "WL Mouse：正在读取…",
            Icon = CreateIcon(null, false, false),
        };
        _icon.DoubleClick += (_, _) => OpenSettingsRequested?.Invoke();
    }

    public void Update(BatteryState state)
    {
        string text = !state.DevicePresent
            ? "WL Mouse：未检测到接收器"
            : state.Percent is int p
                ? $"WL Mouse：{p}%" + (state.Charging ? " 充电中" : "")
                : "WL Mouse：鼠标未连接";

        _icon.Text = text.Length > 63 ? text[..63] : text;

        Icon next = CreateIcon(state.Percent, state.Charging, state.DevicePresent);
        _icon.Icon = next;
        _current?.Dispose();
        _current = next;

        _batteryItem.Text = !state.DevicePresent
            ? "电量：未连接"
            : state.Percent is int percent
                ? $"电量：{percent}%" + (state.Charging ? "（充电中）" : "")
                : "电量：读取失败";

        _widgetItem.Checked = _settings.ShowWidget;
        _autoStartItem.Checked = AutoStart.IsEnabled;
        _icon.Visible = _settings.ShowTrayIcon;
    }

    public void SyncState()
    {
        _widgetItem.Checked = _settings.ShowWidget;
        _autoStartItem.Checked = AutoStart.IsEnabled;
        _icon.Visible = _settings.ShowTrayIcon;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon CreateIcon(int? percent, bool charging, bool connected)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            Color color = !connected
                ? Color.FromArgb(150, 150, 150)
                : charging
                    ? Color.FromArgb(0, 150, 255)
                    : percent >= 50
                        ? Color.FromArgb(70, 200, 90)
                        : percent >= 20
                            ? Color.FromArgb(240, 175, 20)
                            : Color.FromArgb(230, 70, 70);

            string text = percent is int p ? p.ToString() : connected ? "—" : "?";
            float fontSize = text.Length >= 3 ? 12f : 15f;

            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(text, font, brush, new RectangleF(-1, 1, size + 2, size), format);

            if (charging)
            {
                using var bolt = new SolidBrush(Color.FromArgb(255, 220, 60));
                PointF[] points =
                {
                    new(22f, 17f), new(28f, 17f), new(24f, 23f), new(27f, 23f),
                    new(19f, 31f), new(23f, 23f), new(19f, 23f),
                };
                g.FillPolygon(bolt, points);
            }
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
