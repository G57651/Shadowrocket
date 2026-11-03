using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Shadowrocket.Core;

namespace Shadowrocket.Converters;

/// <summary>bool → Visibility。</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>bool → 反转 Visibility（false 显示）。</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>字符串非空 → Visibility。</summary>
public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type? targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>bool → 主开关徽标文字（运行中 / 未开启）。</summary>
public class RunningTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "运行中" : "未开启";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>bool → 主开关徽标底色（绿 / 灰）。</summary>
public class RunningBadgeConverter : IValueConverter
{
    private static readonly Brush On = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly Brush Off = Frozen(Color.FromRgb(0x8E, 0x8E, 0x93));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? On : Off;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>节点当前态 → 描边颜色。</summary>
public class CurrentNodeBorderConverter : IValueConverter
{
    private static readonly Brush Selected = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly Brush Transparent = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Selected : Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>百分比 → 进度条列宽（星型），进度条由两列 Grid 组成。</summary>
public class PercentToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is double d ? Math.Clamp(d, 0, 1) : 0;
        return new GridLength(Math.Max(percent, 0.001), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>RadioButton.IsChecked ↔ TunnelMode 双向转换（三态主开关）。</summary>
public class EnumEqualConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string s && Enum.TryParse<TunnelMode>(s, out var m) ? m : Binding.DoNothing;
}

/// <summary>字符串 → Color（延迟着色等 XAML 绑定用）。</summary>
public class StringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && System.Windows.Media.ColorConverter.ConvertFromString(s) is Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
