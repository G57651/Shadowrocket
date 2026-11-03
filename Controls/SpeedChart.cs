using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Globalization;

namespace Shadowrocket.Controls;

/// <summary>
/// 实时速率曲线（设计稿 A · 灵动面积图）：贝塞尔平滑 + 纵向渐变填充 + 末端呼吸点 + 动态量程。
/// 系列值为绝对速率（MB/s），量程随流量自动取整缩放 —— 低速时不再糊成横线，
/// 高速时也不会被固定满量程削顶；整窗无流量时量程归零、标签显示 0 KB/s。
/// 量程标签单位自适应：低于 1 mb/s 用 KB/s，否则用小写 mb/s。
/// 颜色通过 DynamicResource 绑定令牌，跟随主题切换。
/// </summary>
public class SpeedChart : FrameworkElement
{
    public static readonly DependencyProperty DownSeriesProperty = DependencyProperty.Register(
        nameof(DownSeries), typeof(IReadOnlyList<double>), typeof(SpeedChart),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UpSeriesProperty = DependencyProperty.Register(
        nameof(UpSeries), typeof(IReadOnlyList<double>), typeof(SpeedChart),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownColorProperty = DependencyProperty.Register(
        nameof(DownColor), typeof(Color), typeof(SpeedChart),
        new FrameworkPropertyMetadata(Color.FromRgb(0x0A, 0x84, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UpColorProperty = DependencyProperty.Register(
        nameof(UpColor), typeof(Color), typeof(SpeedChart),
        new FrameworkPropertyMetadata(Color.FromRgb(0x32, 0xAD, 0xE6), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridColorProperty = DependencyProperty.Register(
        nameof(GridColor), typeof(Color), typeof(SpeedChart),
        new FrameworkPropertyMetadata(Color.FromRgb(0xE3, 0xED, 0xF9), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextColorProperty = DependencyProperty.Register(
        nameof(TextColor), typeof(Color), typeof(SpeedChart),
        new FrameworkPropertyMetadata(Color.FromRgb(0x8E, 0x8E, 0x93), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>下行序列（MB/s，最新值在末尾）。</summary>
    public IReadOnlyList<double> DownSeries
    {
        get => (IReadOnlyList<double>)GetValue(DownSeriesProperty);
        set => SetValue(DownSeriesProperty, value);
    }

    /// <summary>上行序列（MB/s，最新值在末尾）。</summary>
    public IReadOnlyList<double> UpSeries
    {
        get => (IReadOnlyList<double>)GetValue(UpSeriesProperty);
        set => SetValue(UpSeriesProperty, value);
    }

    public Color DownColor { get => (Color)GetValue(DownColorProperty); set => SetValue(DownColorProperty, value); }
    public Color UpColor { get => (Color)GetValue(UpColorProperty); set => SetValue(UpColorProperty, value); }
    public Color GridColor { get => (Color)GetValue(GridColorProperty); set => SetValue(GridColorProperty, value); }
    public Color TextColor { get => (Color)GetValue(TextColorProperty); set => SetValue(TextColorProperty, value); }

    /// <summary>平滑过渡的当前量程（避免量程随瞬时峰值来回跳）。</summary>
    private double _scaleMax;

    // 绘制边距：顶部留给量程标签，右侧留给末端呼吸点
    private const int PadL = 4, PadR = 8, PadT = 15, PadB = 4;

    protected override void OnRender(DrawingContext dc)
    {
        try
        {
            RenderChart(dc);
        }
        catch (Exception e)
        {
            // 渲染路径的异常会让整个窗口白屏且不触发应用崩溃，写文件便于定位
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "speedchart-error.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + e + Environment.NewLine);
            }
            catch
            {
            }
        }
    }

    private void RenderChart(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var down = DownSeries;
        var up = UpSeries;

        // 动态量程：窗口内峰值向上取整到"好看"的刻度并平滑过渡；
        // 整窗无流量时量程直接归零，标签如实显示 0 KB/s，而不是默认的起步量程
        double peak = 0;
        foreach (var v in down) peak = Math.Max(peak, v);
        foreach (var v in up) peak = Math.Max(peak, v);
        if (peak <= 0)
        {
            _scaleMax = 0;
        }
        else
        {
            var target = NiceCeil(peak * 1.15);
            _scaleMax = _scaleMax <= 0
                ? target
                : _scaleMax + (target - _scaleMax) * 0.25;
            if (Math.Abs(_scaleMax - target) < target * 0.01)
            {
                _scaleMax = target;
            }
        }
        var scale = _scaleMax;   // 可能为 0，仅作量程标签；绘图用 drawScale 兜底
        var drawScale = scale > 0 ? scale : 1;

        // 网格线
        var gridPen = FrozenPen(GridColor, 1);
        var iw = w - PadL - PadR;
        var ih = h - PadT - PadB;
        var baseY = h - PadB;
        for (var i = 1; i <= 3; i++)
        {
            var y = PadT + ih * i / 4.0;
            dc.DrawLine(gridPen, new Point(PadL, y), new Point(w - PadR, y));
        }

        DrawSeries(dc, down, DownColor, 0.30, drawScale, w, h, baseY, iw);
        DrawSeries(dc, up, UpColor, 0.20, drawScale, w, h, baseY, iw);

        // 量程标签（左上）：随量程自适应 —— 无流量如实显示 0；低于 1 mb/s 换算成 KB/s
        var typeface = new Typeface("Cascadia Mono, Consolas");
        string label;
        if (scale <= 0)
        {
            label = "0 KB/s";
        }
        else if (scale < 1)
        {
            label = $"{FormatScale(scale * 1024)} KB/s";
        }
        else
        {
            label = $"{FormatScale(scale)} mb/s";
        }
        var ft = new FormattedText(
            label,
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11,
            new SolidColorBrush(TextColor), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(PadL + 2, 1));
    }

    private void DrawSeries(DrawingContext dc, IReadOnlyList<double>? values, Color color,
        double fillAlpha, double scale, double w, double h, double baseY, double iw)
    {
        if (values is not { Count: > 1 })
        {
            return;
        }

        var ih = h - PadT - PadB;
        Point Pt(int i) => new(
            PadL + iw * i / (values.Count - 1),
            baseY - Math.Min(values[i], scale) / scale * ih);

        var pts = new Point[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            pts[i] = Pt(i);
        }

        // 面积：平滑曲线 + 到底闭合，纵向渐变（顶部实色 → 透明）
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            SmoothTo(ctx, pts);
            ctx.LineTo(new Point(w - PadR, baseY), false, false);
            ctx.LineTo(new Point(PadL, baseY), false, false);
            ctx.Close();
        }
        var minY = pts.Min(p => p.Y);
        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, minY),
            EndPoint = new Point(0, baseY),
            MappingMode = BrushMappingMode.Absolute,
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(fillAlpha * 255), color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0x08, color.R, color.G, color.B), 1),
            },
        };
        dc.DrawGeometry(fill, null, area);

        // 曲线
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            SmoothTo(ctx, pts);
        }
        dc.DrawGeometry(null, FrozenPen(color, 1.8), line);

        // 末端呼吸点：实心点 + 半透明外环
        var last = pts[^1];
        dc.DrawEllipse(new SolidColorBrush(color), null, last, 3.2, 3.2);
        var ring = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));
        dc.DrawEllipse(ring, null, last, 5.5, 5.5);
    }

    /// <summary>二次贝塞尔中点平滑（与设计稿一致）。</summary>
    private static void SmoothTo(StreamGeometryContext ctx, Point[] pts)
    {
        ctx.BeginFigure(pts[0], false, false);
        for (var i = 1; i < pts.Length - 1; i++)
        {
            var mx = (pts[i].X + pts[i + 1].X) / 2;
            var my = (pts[i].Y + pts[i + 1].Y) / 2;
            ctx.QuadraticBezierTo(pts[i], new Point(mx, my), true, true);
        }
        ctx.LineTo(pts[^1], true, true);
    }

    /// <summary>把峰值取整到"好看"的量程：低于 512 KB/s 在 KB 刻度上取整（小流量也有可读分辨率），其余取整到 1/2/5×10ⁿ。</summary>
    private static double NiceCeil(double v)
    {
        var kb = v * 1024;
        foreach (var m in new[] { 32.0, 64.0, 128.0, 256.0, 512.0 })
        {
            if (m >= kb)
            {
                return m / 1024;
            }
        }
        var e = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1.0, 2.0, 5.0, 10.0 })
        {
            if (m * e >= v)
            {
                return m * e;
            }
        }
        return 10 * e;
    }

    private static string FormatScale(double v) =>
        v >= 1 ? v.ToString("0.#") : v.ToString("0.##");

    private static Pen FrozenPen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
