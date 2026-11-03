using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Shadowrocket.Core;
using Shadowrocket.Views;

namespace Shadowrocket.ViewModels;

public class ProfileRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string TrafficText { get; set; } = "";
    public double Percent { get; set; }
    public double Remainder { get; set; } = 1;
    public string ExpireText { get; set; } = "";
    public string UpdatedText { get; set; } = "未更新";
}

public class GroupRow
{
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string Now { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>配置页：订阅 / 规则 / 代理组 / 覆写（M2：订阅与代理组真实数据；规则编辑 M3）。</summary>
public class ConfigViewModel : INotifyPropertyChanged
{
    private readonly AppRuntime _rt;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConfigViewModel(AppRuntime rt)
    {
        _rt = rt;
        AddSubscriptionCommand = new RelayCommand<object>(_ => MainWindow.RequestAddSubscription());
        OpenRulesCommand = new RelayCommand<object>(_ => new RulesDialog(_rt).ShowDialog());
        OpenGroupCommand = new RelayCommand<GroupRow>(g => { if (g != null) { new GroupDetailDialog(_rt, g.Name).ShowDialog(); _ = RefreshAsync(); } });
        OpenProfileCommand = new RelayCommand<ProfileRow>(p => { if (p != null) { new SubscriptionDetailDialog(_rt, p).ShowDialog(); _ = RefreshAsync(); } });
        UpdateProfileCommand = new RelayCommand<ProfileRow>(async p => await UpdateAsync(p));
        DeleteProfileCommand = new RelayCommand<ProfileRow>(p => Delete(p));
        RefreshCommand = new RelayCommand<object>(async _ => await RefreshAsync());
        SetRouteCommand = new RelayCommand<string>(async m =>
        {
            if (m == null)
            {
                return;
            }
            await _rt.SetRouteModeAsync(m);
            OnPropertyChanged(nameof(RouteModeText));
        });
    }

    public ObservableCollection<ProfileRow> Profiles { get; } = [];
    public ObservableCollection<GroupRow> Groups { get; } = [];

    public ICommand AddSubscriptionCommand { get; }
    public ICommand OpenRulesCommand { get; }
    public ICommand OpenGroupCommand { get; }
    public ICommand OpenProfileCommand { get; }
    public ICommand UpdateProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SetRouteCommand { get; }

    public int RuleCount { get; private set; }

    public string RouteModeText => _rt.Settings.RouteMode switch
    {
        "global" => "全局模式",
        "direct" => "直连模式",
        _ => "规则模式",
    };

    public bool HasProfiles => Profiles.Count > 0;

    public async Task RefreshAsync()
    {
        var subs = await _rt.Api.GetSubscriptionInfoAsync();
        var groups = await _rt.Api.GetGroupsAsync();
        var rules = await _rt.Api.GetRuleCountAsync();

        Application.Current.Dispatcher.Invoke(() =>
        {
            Profiles.Clear();
            foreach (var p in _rt.Profiles.Items)
            {
                var row = new ProfileRow
                {
                    Id = p.Id,
                    Name = string.IsNullOrWhiteSpace(p.Name) ? "(未命名)" : p.Name,
                    Source = p.LocalFile.Length > 0 ? "本地文件" : new Uri(p.Url).Host,
                    UpdatedText = p.LastUpdated is { } t ? $"更新于 {t:MM-dd HH:mm}" : "未更新",
                };
                if (subs.TryGetValue(p.Id, out var si) && si.Total > 0)
                {
                    var used = si.Upload + si.Download;
                    row.TrafficText = $"{MihomoApi.FormatBytes(used)} / {MihomoApi.FormatBytes(si.Total)}";
                    row.Percent = Math.Clamp(used / (double)si.Total, 0, 1);
                    row.Remainder = 1 - row.Percent;
                    row.ExpireText = si.Expire > 0
                        ? $"到期 {DateTimeOffset.FromUnixTimeSeconds(si.Expire).LocalDateTime:yyyy-MM-dd}"
                        : "长期有效";
                }
                else
                {
                    row.TrafficText = "未获取到流量信息";
                    row.Percent = 0;
                    row.Remainder = 1;
                }
                Profiles.Add(row);
            }

            Groups.Clear();
            foreach (var g in groups.Where(g => g.Name is "PROXY" or "AUTO"))
            {
                Groups.Add(new GroupRow { Name = g.Name, TypeName = g.Type, Now = g.Now, Count = g.Count });
            }

            RuleCount = rules;
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(RuleCount));
            OnPropertyChanged(nameof(RouteModeText));
        });
    }

    private bool _updatingProfile;

    private async Task UpdateAsync(ProfileRow? p)
    {
        if (p == null || _updatingProfile)
        {
            return;
        }
        _updatingProfile = true;
        try
        {
            var item = _rt.Profiles.Items.First(x => x.Id == p.Id);
            await _rt.UpdateProfileAsync(item);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updatingProfile = false;
        }
    }

    private void Delete(ProfileRow? p)
    {
        if (p == null)
        {
            return;
        }
        var r = MessageBox.Show($"删除订阅「{p.Name}」？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes)
        {
            return;
        }
        _rt.DeleteProfile(p.Id);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
