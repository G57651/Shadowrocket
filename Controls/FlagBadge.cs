using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shadowrocket.Controls;

/// <summary>
/// 国旗徽标：Windows 不渲染 emoji 国旗，这里用「国家主色圆角块 + 两字母代码」表达，
/// 比 1:1 画国旗更清晰且不歧义（ui-spec §2）。
/// </summary>
public class FlagBadge : Control
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(FlagBadge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(FlagBadge), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    static FlagBadge()
    {
        WidthProperty.OverrideMetadata(typeof(FlagBadge), new FrameworkPropertyMetadata(26.0));
        HeightProperty.OverrideMetadata(typeof(FlagBadge), new FrameworkPropertyMetadata(18.0));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var radius = 4.0;

        dc.DrawRoundedRectangle(Fill ?? Brushes.Gray, null, new Rect(0, 0, w, h), radius, radius);

        // 底部压暗，保证白字在浅色旗色（如日本）上也可读
        var shade = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        shade.Freeze();
        dc.DrawRoundedRectangle(shade, null, new Rect(0, 0, w, h), radius, radius);

        if (string.IsNullOrEmpty(Code))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var size = h * 0.5;
        var text = new FormattedText(
            Code,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            size,
            Brushes.White,
            dpi);

        dc.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2));
    }
}
