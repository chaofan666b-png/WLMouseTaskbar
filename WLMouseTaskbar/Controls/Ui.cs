using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WLMouseTaskbar;

/// <summary>
/// 用代码搭 Fluent 式设置页的小工具。
///
/// 设置项是「按设备能力动态出现」的——有的鼠标没有抬升高度、有的没有 turbo 模式，
/// 所以页面不适合写死在 XAML 里。这里把常用的几种行封装成方法，页面代码读起来
/// 就像在描述界面本身。
/// </summary>
internal static class Ui
{
    public static Brush Res(string key) => (Brush)System.Windows.Application.Current.FindResource(key);

    // ---- 文本 ----

    public static TextBlock Title(string text) => new()
    {
        Text = text,
        FontSize = 28,
        FontWeight = FontWeights.SemiBold,
        Foreground = Res("TextPrimary"),
        Margin = new Thickness(0, 0, 0, 6),
    };

    public static TextBlock Subtitle(string text) => new()
    {
        Text = text,
        FontSize = 13,
        Foreground = Res("TextSecondary"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 20),
    };

    /// <summary>卡片组之间的小节标题，对应 WinUI 设置页的分组名。</summary>
    public static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = Res("TextSecondary"),
        Margin = new Thickness(2, 18, 0, 8),
    };

    private static TextBlock RowLabel(string text) => new()
    {
        Text = text,
        FontSize = 14,
        Foreground = Res("TextPrimary"),
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock RowHint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Res("TextSecondary"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0),
    };

    // ---- 卡片 ----

    public static Border Card(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(Divider());
            stack.Children.Add(rows[i]);
        }

        return new Border
        {
            Background = Res("CardBackground"),
            BorderBrush = Res("CardStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 0, 4),
            Child = stack,
        };
    }

    private static Border Divider() => new()
    {
        Height = 1,
        Background = Res("CardStroke"),
        Margin = new Thickness(0, 0, 0, 0),
    };

    /// <summary>一行设置：左侧标题与说明，右侧控件。</summary>
    public static Grid Row(string title, string? description, FrameworkElement control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(RowLabel(title));
        if (!string.IsNullOrEmpty(description)) text.Children.Add(RowHint(description));

        var grid = new Grid { MinHeight = 46, Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(20, 0, 0, 0);
        grid.Children.Add(control);

        return grid;
    }

    /// <summary>一行，下方还有一个占满整行宽度的控件（滑杆、档位表）。</summary>
    public static StackPanel RowWithTrailing(string title, string? description,
        FrameworkElement trailing, FrameworkElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
        panel.Children.Add(Row(title, description, trailing));
        control.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>纯展示的一行（只读信息）。</summary>
    public static Grid InfoRow(string label, out TextBlock value)
    {
        value = new TextBlock
        {
            FontSize = 14,
            Foreground = Res("TextPrimary"),
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new TextBlock
        {
            Text = label,
            FontSize = 14,
            Foreground = Res("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid { MinHeight = 40, Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(name);
        Grid.SetColumn(value, 1);
        value.Margin = new Thickness(20, 0, 0, 0);
        grid.Children.Add(value);
        return grid;
    }

    // ---- 控件 ----

    public static CheckBox Toggle(bool isChecked) => new()
    {
        IsChecked = isChecked,
        Style = (Style)System.Windows.Application.Current.FindResource("FluentToggleSwitch"),
    };

    public static Button Button(string text, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)System.Windows.Application.Current.FindResource("FluentButton"),
        };
        button.Click += onClick;
        return button;
    }

    public static Button AccentButton(string text, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)System.Windows.Application.Current.FindResource("AccentButton"),
        };
        button.Click += onClick;
        return button;
    }

    public static ComboBox Combo<T>(IEnumerable<T> items, T selected, Action<T> onChanged,
        Func<T, string>? label = null)
    {
        var combo = new ComboBox
        {
            Style = (Style)System.Windows.Application.Current.FindResource("FluentComboBox"),
        };

        foreach (T item in items)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = label?.Invoke(item) ?? item?.ToString() ?? "",
                Tag = item,
            });
        }

        foreach (ComboBoxItem candidate in combo.Items)
        {
            if (Equals(candidate.Tag, selected))
            {
                combo.SelectedItem = candidate;
                break;
            }
        }

        bool loading = true;
        combo.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            if (combo.SelectedItem is ComboBoxItem { Tag: T value }) onChanged(value);
        };
        loading = false;

        return combo;
    }

    public static Slider Slider(double min, double max, double value, double tick,
        RoutedPropertyChangedEventHandler<double> onChanged)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickFrequency = tick,
            IsSnapToTickEnabled = true,
            Style = (Style)System.Windows.Application.Current.FindResource("FluentSlider"),
        };
        slider.ValueChanged += onChanged;
        return slider;
    }

    /// <summary>数字输入：一个滑杆 + 右侧数值，用于 DPI 这类需要精确值的场景。</summary>
    public static StackPanel NumberRow(string title, string? description, int min, int max, int step,
        int value, Action<int> onChanged)
    {
        var valueText = new TextBlock
        {
            Text = value.ToString(),
            FontSize = 13,
            Foreground = Res("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 64,
            TextAlignment = TextAlignment.Right,
        };

        var slider = Slider(min, max, value, step, (_, e) =>
        {
            valueText.Text = ((int)Math.Round(e.NewValue)).ToString();
        });

        // 只在松手时提交，避免拖动过程中往 HID 狂发命令
        slider.AddHandler(
            System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) =>
                onChanged((int)Math.Round(slider.Value))));

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(RowLabel(title));
        Grid.SetColumn(slider, 1);
        slider.Margin = new Thickness(20, 0, 12, 0);
        grid.Children.Add(slider);
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(valueText);

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(grid);
        if (!string.IsNullOrEmpty(description)) panel.Children.Add(RowHint(description));
        return panel;
    }

    /// <summary>空状态提示（没插接收器 / 鼠标休眠时用）。</summary>
    public static Border Empty(string title, string detail) => new()
    {
        Background = Res("CardBackground"),
        BorderBrush = Res("CardStroke"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(24, 28, 24, 28),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res("TextPrimary"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = detail,
                    FontSize = 12.5,
                    Foreground = Res("TextSecondary"),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0),
                },
            },
        },
    };
}
