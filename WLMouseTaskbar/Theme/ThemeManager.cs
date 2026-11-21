using System.Windows;
using Microsoft.Win32;

namespace WLMouseTaskbar;

/// <summary>在浅色 / 深色两套颜色令牌之间切换。</summary>
public static class ThemeManager
{
    private static readonly Uri LightUri = new("pack://application:,,,/Theme/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/Theme/Dark.xaml");

    private static ResourceDictionary? _current;

    public static bool IsDark { get; private set; }

    /// <summary>调试用：强制指定深浅色（null = 跟随系统）。</summary>
    public static bool? Forced { get; set; }

    /// <summary>按系统「应用主题」（而非任务栏主题）选深浅色。</summary>
    public static bool SystemPrefersDark() => !ReadAppsUseLightTheme();

    private static bool ReadAppsUseLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // 读不到（新装系统 / 被组策略关掉）时按浅色处理，与资源管理器一致
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>应用主题色板。forceDark 用于调试时强制指定深浅色。</summary>
    public static void Apply(bool? forceDark = null)
    {
        bool dark = forceDark ?? Forced ?? SystemPrefersDark();
        IsDark = dark;

        var dictionary = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        IList<ResourceDictionary> merged = System.Windows.Application.Current.Resources.MergedDictionaries;

        if (_current is not null) merged.Remove(_current);
        merged.Insert(0, dictionary);
        _current = dictionary;
    }
}
