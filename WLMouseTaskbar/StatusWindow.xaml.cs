using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WLMouseTaskbar;

/// <summary>详情 + 设置窗口，左键点任务栏控件或托盘图标打开。</summary>
public partial class StatusWindow : Window
{
    private readonly BatteryService _battery;
    private readonly AppSettings _settings;
    private readonly Action? _onSettingsChanged;
    private bool _loading = true;

    public StatusWindow(BatteryService battery, AppSettings settings, Action? onSettingsChanged = null)
    {
        _battery = battery;
        _settings = settings;
        _onSettingsChanged = onSettingsChanged;

        InitializeComponent();

        ShowWidgetCheck.IsChecked = settings.ShowWidget;
        ShowTrayCheck.IsChecked = settings.ShowTrayIcon;
        HideDisconnectedCheck.IsChecked = settings.HideWhenDisconnected;

        OffsetSlider.Value = Math.Clamp(settings.WidgetOffset, OffsetSlider.Minimum, OffsetSlider.Maximum);
        OffsetText.Text = $"{settings.WidgetOffset:0} px";

        foreach (ComboBoxItem item in PositionCombo.Items)
        {
            if (item.Tag is string tag && int.TryParse(tag, out int pos) && pos == settings.WidgetPosition)
            {
                PositionCombo.SelectedItem = item;
                break;
            }
        }

        foreach (ComboBoxItem item in PollCombo.Items)
        {
            if (item.Tag is string tag && int.TryParse(tag, out int seconds) && seconds == settings.PollSeconds)
            {
                PollCombo.SelectedItem = item;
                break;
            }
        }

        _loading = false;

        Apply(battery.State);
    }

    public void Apply(BatteryState state)
    {
        BigPercent.Text = state.Percent is int p ? $"{p}%" : "—";

        if (!state.DevicePresent)
        {
            StatusLine.Text = "未检测到接收器";
            BigPercent.Foreground = Brushes.Gray;
        }
        else if (state.Percent is int percent)
        {
            StatusLine.Text = state.Charging ? "充电中" : "放电中";
            BigPercent.Foreground = percent switch
            {
                >= 50 => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A)),
                >= 20 => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06)),
                _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26)),
            };
        }
        else
        {
            StatusLine.Text = state.Error ?? "读取失败";
            BigPercent.Foreground = Brushes.Gray;
        }

        UpdatedLine.Text = state.Timestamp == DateTime.MinValue
            ? "—"
            : $"更新于 {state.Timestamp:HH:mm:ss}";

        DeviceLine.Text = state.DevicePresent
            ? "WL Mouse 接收器（VID 36A7 / PID A868）"
            : "未检测到";
        MouseFwLine.Text = state.MouseFirmware ?? "—";
        ReceiverFwLine.Text = state.ReceiverFirmware ?? "—";
        RawLine.Text = state.RawHex.Length > 0 ? state.RawHex : "—";

        LogBox.Text = _battery.LogText;
        LogBox.ScrollToEnd();
    }

    private void OnDisplayChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.ShowWidget = ShowWidgetCheck.IsChecked == true;
        _settings.ShowTrayIcon = ShowTrayCheck.IsChecked == true;
        _settings.HideWhenDisconnected = HideDisconnectedCheck.IsChecked == true;
        _settings.Save();
        _onSettingsChanged?.Invoke();
    }

    private void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        _settings.WidgetOffset = Math.Round(e.NewValue);
        OffsetText.Text = $"{_settings.WidgetOffset:0} px";
        _settings.Save();
        _onSettingsChanged?.Invoke();
    }

    private void PositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (PositionCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (!int.TryParse(tag, out int pos)) return;

        _settings.WidgetPosition = pos;
        _settings.Save();
        _onSettingsChanged?.Invoke();
    }

    private void PollCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (PollCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (!int.TryParse(tag, out int seconds)) return;

        _settings.PollSeconds = seconds;
        _settings.Save();
        _battery.PollSeconds = seconds;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _battery.RequestRefresh();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
