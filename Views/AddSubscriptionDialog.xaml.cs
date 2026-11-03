using System.Windows;
using System.Windows.Controls;

namespace Shadowrocket.Views;

/// <summary>添加订阅：远程 URL / 剪贴板 / 本地 YAML 文件（ui-spec §3.2）。</summary>
public partial class AddSubscriptionDialog : Window
{
    public AddSubscriptionDialog()
    {
        InitializeComponent();
    }

    private void FromClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
        {
            UrlBox.Text = Clipboard.GetText().Trim();
        }
    }

    private void FromFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "配置文件 (*.yaml;*.yml)|*.yaml;*.yml|全部文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            UrlBox.Text = "";
            FilePathBox.Text = dlg.FileName;
            FilePanel.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            }
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var file = FilePanel.Visibility == Visibility.Visible ? FilePathBox.Text : "";
        var url = UrlBox.Text.Trim();

        if (file.Length == 0 && (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out _)))
        {
            MessageBox.Show("请填写订阅链接，或选择本地 YAML 文件", "添加订阅", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ImportButton.IsEnabled = false;
        StatusText.Text = "正在导入…";
        try
        {
            await App.Runtime.AddProfileAsync(NameBox.Text.Trim(), url, file.Length > 0 ? file : null);
            if (IsLoaded)
            {
                DialogResult = true;   // 等待期间窗口可能已被用户关掉，此时不再设置 DialogResult
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            ImportButton.IsEnabled = true;
        }
    }
}
