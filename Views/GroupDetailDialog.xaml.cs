using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shadowrocket.Core;

namespace Shadowrocket.Views;

public partial class GroupDetailDialog : Window
{
    private readonly AppRuntime _rt;
    private readonly string _group;
    private List<GroupMemberRow> _rows = [];

    public GroupDetailDialog(AppRuntime rt, string group)
    {
        InitializeComponent();
        _rt = rt;
        _group = group;
        Owner = Application.Current.MainWindow;
        GroupName.Text = group;
        TestHint.Text = "双击一行即可切换";
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var g = await _rt.Api.GetGroupAsync(_group);
        if (g == null)
        {
            TestHint.Text = "内核未运行或该组不存在";
            return;
        }
        var (now, members) = g.Value;
        var groups = await _rt.Api.GetGroupsAsync();
        GroupType.Text = groups.FirstOrDefault(x => x.Name == _group)?.Type ?? "";
        GroupNow.Text = $"当前：{now}";

        // 保留已有延迟数据，避免刷新后回到"未测"
        var previous = _rows.ToDictionary(r => r.Name, r => r.Delay);
        var providers = await _rt.Api.GetProvidersAsync();
        _rows = members.Select(m => new GroupMemberRow
        {
            Name = m,
            ProviderId = providers.TryGetValue(m, out var info) ? info.ProviderId : "",
            IsCurrent = m == now,
            Delay = previous.TryGetValue(m, out var d) ? d : -1,
        }).ToList();
        Members.ItemsSource = _rows;
        var idx = _rows.FindIndex(r => r.IsCurrent);
        if (idx >= 0)
        {
            Members.SelectedIndex = idx;
        }
    }

    private async void TestAll_Click(object sender, RoutedEventArgs e)
    {
        TestAllButton.IsEnabled = false;
        TestHint.Text = "正在测速…";
        try
        {
            var delays = await _rt.Api.GetGroupDelayAsync(_group);
            foreach (var r in _rows)
            {
                if (!delays.TryGetValue(r.Name, out var d))
                {
                    // 组接口只返回测通的节点，其余逐个确认
                    d = await _rt.Api.TestDelayAsync(r.Name, r.ProviderId);
                }
                r.Delay = d;
            }
            TestHint.Text = $"完成：{delays.Count}/{_rows.Count} 个节点有响应";
        }
        finally
        {
            TestAllButton.IsEnabled = true;
        }
    }

    private async void TestOne_Click(object sender, RoutedEventArgs e)
    {
        if (Members.SelectedItem is not GroupMemberRow row)
        {
            MessageBox.Show("请先选中一个节点。", "测速", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        row.Delay = -1;
        row.Delay = await _rt.Api.TestDelayAsync(row.Name, row.ProviderId);
    }

    private async void Select_Click(object sender, RoutedEventArgs e) => await SelectCoreAsync();

    private async Task SelectCoreAsync()
    {
        if (Members.SelectedItem is not GroupMemberRow row || row.Name.Length == 0)
        {
            MessageBox.Show("请先选中一个节点。", "切换", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await _rt.Api.SelectInGroupAsync(_group, row.Name);
        await LoadAsync();
    }

    private async void Members_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await SelectCoreAsync();
}

/// <summary>策略组成员行：名称 + 归属 provider + 延迟。</summary>
public class GroupMemberRow : INotifyPropertyChanged
{
    private int _delay = -1;

    public string Name { get; init; } = "";
    public string ProviderId { get; init; } = "";
    public bool IsCurrent { get; init; }

    public int Delay
    {
        get => _delay;
        set
        {
            _delay = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DelayText));
            OnPropertyChanged(nameof(DelayColorText));
        }
    }

    public string DelayText => Delay < 0 ? "未测" : $"{Delay}ms";

    public string DelayColorText => Delay switch
    {
        < 0 => "#8E8E93",
        < 100 => "#34C759",
        < 300 => "#FF9500",
        _ => "#FF3B30",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
