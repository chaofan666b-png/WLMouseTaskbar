using System.Text.Json;
using Microsoft.Win32;

namespace WLMouseTaskbar;

/// <summary>用户设置，持久化到 %APPDATA%\WLMouseTaskbar\settings.json。</summary>
public sealed class AppSettings
{
    public int PollSeconds { get; set; } = 60;
    public bool ShowWidget { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// 控件在任务栏上的相对位置：
    /// 0 = 紧贴通知区域右侧角落，1 = 任务栏居中，2 = 靠近开始按钮。
    /// </summary>
    public int WidgetPosition { get; set; } = 0;

    /// <summary>控件与锚点之间的间距（DIP）。任务栏上已有别的挂件时调大可以错开。</summary>
    public double WidgetOffset { get; set; } = 6;

    /// <summary>
    /// 自动避让任务栏上的其他挂件（例如 FluentFlyout 的媒体控件）。
    /// 打开后不需要手工调距离，撞上就往左让。
    /// </summary>
    public bool AvoidOtherWidgets { get; set; } = true;

    /// <summary>未连接鼠标时是否隐藏控件。</summary>
    public bool HideWhenDisconnected { get; set; }

    /// <summary>电量跌破阈值时弹出提示卡片。</summary>
    public bool LowBatteryAlert { get; set; } = true;

    /// <summary>低电量提示的触发阈值（百分比）。</summary>
    public int LowBatteryThreshold { get; set; } = 20;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WLMouseTaskbar");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                // 用记事本 / PowerShell 存过的文件可能带 UTF-8 BOM，
                // System.Text.Json 默认不接受，这里剥掉再解析。
                using var reader = new StreamReader(FilePath, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string json = reader.ReadToEnd();
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取设置失败：{ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存设置失败：{ex.Message}");
        }
    }
}

/// <summary>开机自启：写 HKCU Run 键，不需要管理员权限。</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WLMouseTaskbar";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) != null;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enabled)
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return;
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
