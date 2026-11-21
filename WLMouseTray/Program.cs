using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WLMouseTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\WLMouseTray.SingleInstance", out bool isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();

        bool showDetails = args.Any(a => a is "--details" or "/details" or "-d");
        Application.Run(new TrayContext(showDetails));
    }
}

/// <summary>一次采样得到的设备状态。</summary>
internal sealed record Snapshot(
    bool Connected,
    int? Percent,
    bool Charging,
    string? MouseFirmware,
    string? ReceiverFirmware,
    string RawHex,
    string? Error,
    DateTime Timestamp);

/// <summary>托盘程序主体。</summary>
internal sealed class TrayContext : ApplicationContext
{
    private const int PollSeconds = 60;

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<string> _log = new();
    private readonly ToolStripMenuItem _batteryItem;
    private readonly ToolStripMenuItem _deviceItem;
    private readonly ToolStripMenuItem _mouseFwItem;
    private readonly ToolStripMenuItem _receiverFwItem;
    private readonly ToolStripMenuItem _autostartItem;

    private WlMouseDevice? _device;
    private Icon? _icon;
    private Snapshot _snapshot = new(false, null, false, null, null, "", null, DateTime.MinValue);
    private StatusForm? _statusForm;
    private bool _busy;

    public TrayContext(bool showDetailsOnStart = false)
    {
        _batteryItem = new ToolStripMenuItem("正在读取…") { Enabled = false };
        _deviceItem = new ToolStripMenuItem("设备：—") { Enabled = false };
        _mouseFwItem = new ToolStripMenuItem("鼠标固件：—") { Enabled = false };
        _receiverFwItem = new ToolStripMenuItem("接收器固件：—") { Enabled = false };

        var refreshItem = new ToolStripMenuItem("刷新", null, (_, _) => RefreshAsync(manual: true));
        var detailsItem = new ToolStripMenuItem("查看详情…", null, (_, _) => ShowDetails());
        _autostartItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutostart())
        {
            Checked = IsAutostartEnabled(),
        };
        var exitItem = new ToolStripMenuItem("退出", null, (_, _) => ExitApp());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _batteryItem,
            new ToolStripSeparator(),
            _deviceItem,
            _mouseFwItem,
            _receiverFwItem,
            new ToolStripSeparator(),
            refreshItem,
            detailsItem,
            _autostartItem,
            new ToolStripSeparator(),
            exitItem,
        });
        _menu.Opening += (_, _) => RefreshAsync(manual: false);

        _tray = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
            Text = "WL Mouse：正在读取…",
            Icon = IconRenderer.Create(null, false, false),
        };
        _tray.DoubleClick += (_, _) => ShowDetails();

        UpdateVisuals();

        _timer = new System.Windows.Forms.Timer { Interval = PollSeconds * 1000 };
        _timer.Tick += (_, _) => RefreshAsync(manual: false);
        _timer.Start();

        RefreshAsync(manual: false);

        if (showDetailsOnStart)
        {
            ShowDetails();
        }
    }

    private async void RefreshAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;

        try
        {
            Snapshot snapshot = await Task.Run(Poll);
            _snapshot = snapshot;
            UpdateVisuals();
            if (manual) _tray.ShowBalloonTip(1500, "WL Mouse", DescribeForBalloon(snapshot), ToolTipIcon.None);
        }
        catch (Exception ex)
        {
            Log($"刷新异常：{ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private Snapshot Poll()
    {
        if (_device == null)
        {
            _device = WlMouseDevice.TryOpen(_log);
            if (_device == null)
            {
                return new Snapshot(false, null, false, null, null, "", "未找到接收器", DateTime.Now);
            }
        }

        try
        {
            var (charging, percent, ok) = _device.ReadBattery();
            if (!ok)
            {
                // 收不到响应：可能是鼠标休眠或接收器被重新插拔，丢弃句柄下次重开
                string raw = WlMouseDevice.Hex(_device.LastResponse);
                Log($"电量为空，回包 {raw}");
                _device.Dispose();
                _device = null;
                return new Snapshot(true, null, false, null, null, raw, "鼠标无响应（可能已休眠）", DateTime.Now);
            }

            string? mouseFw = _device.ReadFirmwareVersion();
            string? receiverFw = _device.ReadReceiverFirmwareVersion();

            return new Snapshot(true, percent, charging == true, mouseFw, receiverFw,
                WlMouseDevice.Hex(_device.LastResponse), null, DateTime.Now);
        }
        catch (Exception ex)
        {
            Log($"通信异常：{ex.Message}");
            _device?.Dispose();
            _device = null;
            return new Snapshot(false, null, false, null, null, "", ex.Message, DateTime.Now);
        }
    }

    private void UpdateVisuals()
    {
        Snapshot s = _snapshot;
        bool charging = s.Charging;

        Icon newIcon = IconRenderer.Create(s.Percent, charging, s.Connected);
        _tray.Icon = newIcon;
        _icon?.Dispose();
        _icon = newIcon;

        _tray.Text = BuildTooltip(s);

        _batteryItem.Text = !s.Connected
            ? "电量：未连接"
            : s.Percent is int p
                ? $"电量：{p}%" + (charging ? "（充电中）" : "")
                : "电量：读取失败";

        _deviceItem.Text = $"{WlMouseDevice.DefaultVid:X4}:{(s.Connected ? "接收器已识别" : "未检测到")}";
        _mouseFwItem.Text = $"鼠标固件：{s.MouseFirmware ?? "—"}";
        _receiverFwItem.Text = $"接收器固件：{s.ReceiverFirmware ?? "—"}";

        _statusForm?.Apply(s);
    }

    private static string BuildTooltip(Snapshot s)
    {
        string text = !s.Connected
            ? "WL Mouse：未检测到接收器"
            : s.Percent is int p
                ? $"WL Mouse：{p}%" + (s.Charging ? " 充电中" : "")
                : "WL Mouse：鼠标未连接";
        return text.Length > 63 ? text[..63] : text;
    }

    private static string DescribeForBalloon(Snapshot s) =>
        s.Percent is int p
            ? $"当前电量 {p}%" + (s.Charging ? "，正在充电" : "")
            : s.Error ?? "无法读取电量";

    private void ShowDetails()
    {
        if (_statusForm == null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm(() => RefreshAsync(manual: true), BuildLogText);
        }

        _statusForm.Apply(_snapshot);
        _statusForm.Show();
        _statusForm.BringToFront();
        _statusForm.Activate();
    }

    private void Log(string message)
    {
        lock (_log)
        {
            _log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            if (_log.Count > 300) _log.RemoveRange(300, _log.Count - 300);
        }
    }

    private string BuildLogText()
    {
        lock (_log)
        {
            return string.Join(Environment.NewLine, _log);
        }
    }

    private void ToggleAutostart()
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;

        if (IsAutostartEnabled())
        {
            key.DeleteValue("WLMouseTray", throwOnMissingValue: false);
        }
        else
        {
            string exe = Environment.ProcessPath!;
            key.SetValue("WLMouseTray", $"\"{exe}\"");
        }

        _autostartItem.Checked = IsAutostartEnabled();
        Log($"开机自启：{(_autostartItem.Checked ? "已启用" : "已关闭")}");
    }

    private static bool IsAutostartEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
        return key?.GetValue("WLMouseTray") != null;
    }

    private void ExitApp()
    {
        _timer.Stop();
        _tray.Visible = false;
        _device?.Dispose();
        _statusForm?.Close();
        _tray.Dispose();
        _menu.Dispose();
        ExitThread();
    }
}

/// <summary>把电量画成托盘图标。</summary>
internal static class IconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(int? percent, bool charging, bool connected)
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
}

/// <summary>详情窗口：显示状态、回包和调试日志。</summary>
internal sealed class StatusForm : Form
{
    private readonly Label _title;
    private readonly Label _detail;
    private readonly TextBox _logBox;
    private Func<string> _logProvider;

    public StatusForm(Action onRefresh, Func<string> logProvider)
    {
        _logProvider = logProvider;

        Text = "WL Mouse 电量";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 420);
        MinimumSize = new Size(420, 320);
        Font = new Font("Microsoft YaHei UI", 9f);

        _title = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0),
            Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),
            Text = "读取中…",
        };

        _detail = new Label
        {
            Dock = DockStyle.Top,
            Height = 92,
            Padding = new Padding(16, 4, 16, 4),
            Text = "",
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(12, 0, 0, 0),
            FlowDirection = FlowDirection.LeftToRight,
        };
        var refreshButton = new Button { Text = "刷新", Width = 90, Height = 28 };
        refreshButton.Click += (_, _) =>
        {
            onRefresh();
            RefreshLog();
        };
        buttons.Controls.Add(refreshButton);

        var logLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = "  调试日志（最近在上）",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.Gray,
        };

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(250, 250, 250),
        };

        Controls.Add(_logBox);
        Controls.Add(logLabel);
        Controls.Add(buttons);
        Controls.Add(_detail);
        Controls.Add(_title);

        RefreshLog();
    }

    public void Apply(Snapshot s)
    {
        _title.Text = s.Percent is int p
            ? $"{p}%{(s.Charging ? " · 充电中" : "")}"
            : s.Connected ? "未连接鼠标" : "未检测到接收器";

        _detail.Text = string.Join(Environment.NewLine,
            $"设备：WL Mouse（VID {WlMouseDevice.DefaultVid:X4}）",
            $"鼠标固件：{s.MouseFirmware ?? "—"}    接收器固件：{s.ReceiverFirmware ?? "—"}",
            s.RawHex.Length > 0 ? $"最近回包：{s.RawHex}" : "最近回包：—",
            s.Error is null ? $"更新时间：{s.Timestamp:HH:mm:ss}" : $"状态：{s.Error}（{s.Timestamp:HH:mm:ss}）");

        RefreshLog();
    }

    private void RefreshLog() => _logBox.Text = _logProvider();
}
