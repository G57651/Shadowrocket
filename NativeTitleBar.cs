using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Shadowrocket;

/// <summary>
/// 原生标题栏的 Windows 深度适配：
/// · 深浅主题 → DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 1809+，深色主题下原生标题栏/边框变暗）
/// · Win11 → DWMWA_CAPTION_COLOR / BORDER_COLOR / TEXT_COLOR，标题栏精确取应用主题色
/// 旧系统不支持的属性会返回错误码并被忽略，无需版本分支；标题栏颜色与页面背景同源，
/// 视觉上与窗口连成一体，不再固定白色。
/// </summary>
public static class NativeTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int UseImmersiveDarkMode = 20;         // Win10 20H1+
    private const int UseImmersiveDarkModeLegacy = 19;   // Win10 1809-1909
    private const int BorderColor = 34;                  // Win11
    private const int CaptionColor = 35;                 // Win11
    private const int TextColor = 36;                    // Win11

    /// <summary>把窗口的原生标题栏/边框刷成指定主题色。</summary>
    public static void Apply(Window? window, bool dark, Color caption, Color border, Color text)
    {
        if (window == null)
        {
            return;
        }
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        var on = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeLegacy, ref on, sizeof(int));
        SetColor(hwnd, CaptionColor, caption);
        SetColor(hwnd, BorderColor, border);
        SetColor(hwnd, TextColor, text);
    }

    private static void SetColor(IntPtr hwnd, int attribute, Color color)
    {
        // COLORREF 字节序为 0x00BBGGRR，与 WPF 的 R/G/B 相反
        var colorref = color.R | (color.G << 8) | (color.B << 16);
        _ = DwmSetWindowAttribute(hwnd, attribute, ref colorref, sizeof(int));
    }
}
