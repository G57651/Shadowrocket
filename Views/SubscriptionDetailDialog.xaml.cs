using System.Windows;
using Shadowrocket.Core;
using Shadowrocket.ViewModels;

namespace Shadowrocket.Views;

public partial class SubscriptionDetailDialog : Window
{
    private readonly AppRuntime _rt;
    private readonly ProfileRow _row;

    public SubscriptionDetailDialog(AppRuntime rt, ProfileRow row)
    {
        InitializeComponent();
        _rt = rt;
        _row = row;
        Owner = Application.Current.MainWindow;
        NameBox.Text = row.Name;
        var item = rt.Profiles.Items.FirstOrDefault(x => x.Id == row.Id);
        var isRemote = item is { LocalFile.Length: 0 };
        // 远程订阅的链接允许编辑：机场重置订阅链接后，用户可以直接粘贴新链接自救
        UrlBox.IsReadOnly = !isRemote;
        UrlBox.Text = item?.LocalFile is { Length: > 0 } f
            ? $"本地文件（内核读取副本）：{f}\n原始文件：{(item!.SourceFile.Length > 0 ? item.SourceFile : "—")}"
            : item?.Url ?? "";
        if (item != null)
        {
            // 自动更新设置回显
            SetAutoUpdateMode(item.AutoUpdateMode);
            IntervalBox.Text = item.AutoUpdateIntervalMinutes.ToString();
            TimeBox.Text = item.AutoUpdateTime;
            CronBox.Text = item.AutoUpdateCron;
        }
        _ = RefreshInfoAsync();
    }

    private string _autoUpdateMode = "off";

    /// <summary>切换自动更新模式（✓ 标记选中项，按模式显示对应输入）。</summary>
    private void SetAutoUpdateMode(string mode)
    {
        _autoUpdateMode = mode;
        ModeOffButton.Content = mode == "off" ? "✓ 关闭" : "关闭";
        ModeIntervalButton.Content = mode == "interval" ? "✓ 按间隔" : "按间隔";
        ModeDailyButton.Content = mode == "daily" ? "✓ 每天定时" : "每天定时";
        ModeCronButton.Content = mode == "cron" ? "✓ Cron" : "Cron";
        IntervalPanel.Visibility = mode == "interval" ? Visibility.Visible : Visibility.Collapsed;
        DailyPanel.Visibility = mode == "daily" ? Visibility.Visible : Visibility.Collapsed;
        CronPanel.Visibility = mode == "cron" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        SetAutoUpdateMode(((System.Windows.Controls.Button)sender).Tag as string ?? "off");
    }

    /// <summary>从内核重新拉取该订阅的流量、到期与节点数并回显。</summary>
    private async Task RefreshInfoAsync()
    {
        var subs = await _rt.Api.GetSubscriptionInfoAsync();
        var providers = await _rt.Api.GetProvidersAsync();
        var nodeCount = providers.Values.Count(n => n.ProviderId == _row.Id);
        var item = _rt.Profiles.Items.FirstOrDefault(x => x.Id == _row.Id);

        if (subs.TryGetValue(_row.Id, out var si) && si.Total > 0)
        {
            var used = si.Upload + si.Download;
            TrafficText.Text = $"已用 {MihomoApi.FormatBytes(used)} / {MihomoApi.FormatBytes(si.Total)}";
            ExpireText.Text = si.Expire > 0
                ? $"到期 {DateTimeOffset.FromUnixTimeSeconds(si.Expire).LocalDateTime:yyyy-MM-dd}"
                : "长期有效";
        }
        else
        {
            TrafficText.Text = nodeCount > 0
                ? $"机场未返回流量信息 · 当前 {nodeCount} 个节点"
                : "机场未返回流量信息";
            ExpireText.Text = "";
        }
        UpdatedText.Text = $"{nodeCount} 个节点 · " + (item?.LastUpdated is { } t ? $"更新于 {t:MM-dd HH:mm}" : "未更新");
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var item = _rt.Profiles.Items.FirstOrDefault(x => x.Id == _row.Id);
        var text = item?.LocalFile is { Length: > 0 } ? item.SourceFile : item?.Url ?? "";
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && !b.IsEnabled)
        {
            return;   // 防双击并发：上一轮更新未结束时忽略再次点击
        }
        if (sender is System.Windows.Controls.Button btn)
        {
            btn.IsEnabled = false;
        }
        try
        {
            var item = _rt.Profiles.Items.First(x => x.Id == _row.Id);
            await _rt.UpdateProfileAsync(item);
            await RefreshInfoAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (sender is System.Windows.Controls.Button btn2)
            {
                btn2.IsEnabled = true;
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var item = _rt.Profiles.Items.FirstOrDefault(x => x.Id == _row.Id);
        if (item == null)
        {
            DialogResult = true;
            return;
        }
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }
        item.Name = name;

        // 远程订阅：链接一并保存（机场重置链接后可在此粘贴新链接）
        if (item.LocalFile.Length == 0)
        {
            var url = UrlBox.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
                (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show("订阅链接必须是 http:// 或 https:// 开头的完整地址。", "订阅链接",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            item.Url = url;
        }

        // 自动更新设置：按模式校验后写入
        item.AutoUpdateMode = _autoUpdateMode;
        if (_autoUpdateMode == "interval")
        {
            if (!int.TryParse(IntervalBox.Text.Trim(), out var minutes) || minutes is < 5 or > 10080)
            {
                MessageBox.Show("自动更新间隔需在 5-10080 分钟之间。", "自动更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            item.AutoUpdateIntervalMinutes = minutes;
        }
        else if (_autoUpdateMode == "daily")
        {
            if (!DateTime.TryParseExact(TimeBox.Text.Trim(), "HH\\:mm", null,
                    System.Globalization.DateTimeStyles.None, out var t))
            {
                MessageBox.Show("更新时间格式应为 HH:mm，例如 08:30。", "自动更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            item.AutoUpdateTime = t.ToString("HH\\:mm");
        }
        else if (_autoUpdateMode == "cron")
        {
            var expression = CronBox.Text.Trim();
            if (!Cronos.CronExpression.TryParse(expression, out _))
            {
                MessageBox.Show("Cron 表达式无效。支持 5 段式：分 时 日 月 周，例如 30 8 * * *。", "自动更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            item.AutoUpdateCron = expression;
        }
        // 从"关闭"切到自动模式：从现在起算第一个周期，避免一保存就立刻拉取
        if (_autoUpdateMode != "off" && item.LastAutoUpdate == null)
        {
            item.LastAutoUpdate = DateTime.Now;
        }

        _rt.Profiles.Persist();
        _rt.RegenerateConfig();
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show($"删除订阅「{_row.Name}」？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        _rt.DeleteProfile(_row.Id);
        DialogResult = true;
    }
}
