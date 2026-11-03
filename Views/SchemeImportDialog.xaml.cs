using System.Windows;

namespace Shadowrocket.Views;

/// <summary>
/// 一键导入接收方选择：每次点击导入链接时询问交给谁处理。
/// ChoseExternal = true 表示转发给系统里该协议的真实客户端（如 Clash Verge）。
/// </summary>
public partial class SchemeImportDialog : Window
{
    public bool ChoseExternal { get; private set; }
    public bool RememberChoice => RememberBox.IsChecked == true;

    public SchemeImportDialog(string url, string? externalLabel, string? rememberedHint = null)
    {
        InitializeComponent();
        UrlText.Text = url;
        UrlText.ToolTip = url;
        if (externalLabel == null)
        {
            // 没找到可转发的客户端：只保留"导入本软件/取消"
            ForwardButton.Visibility = Visibility.Collapsed;
            HintText.Text = "未检测到其他可转发的客户端。";
        }
        else
        {
            ForwardButton.Content = $"转发给 {externalLabel}";
            HintText.Text = "选择由哪个客户端接收这条订阅。";
        }
        if (rememberedHint != null)
        {
            // 共识修复第 10 条："记住"只作为弹窗预选提示，导入仍需用户当面确认一次
            HintText.Text = rememberedHint;
        }
    }

    private void Self_Click(object sender, RoutedEventArgs e)
    {
        ChoseExternal = false;
        DialogResult = true;
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        ChoseExternal = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
