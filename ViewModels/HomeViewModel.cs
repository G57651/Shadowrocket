using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shadowrocket.Core;

namespace Shadowrocket.ViewModels;

public class NodeRow : INotifyPropertyChanged
{
    private int _latency = -1;
    private double _speed = -1;   // MB/s：-1 未测过，-2 测速中，0 不通，>0 下载速度

    public string Name { get; init; } = "";
    public string ProviderName { get; init; } = "";
    public string ProviderId { get; init; } = "";
    public string TypeName { get; init; } = "";

    /// <summary>
    /// 所属 VM。右键菜单（ContextMenu）位于独立弹窗树里，用 RelativeSource 找不到 UserControl，
    /// 因此菜单命令必须经由行对象拿到 VM。
    /// </summary>
    public HomeViewModel? Owner { get; init; }
    public bool Udp { get; init; }
    public bool IsCurrent { get; set; }

    public int Latency
    {
        get => _latency;
        set
        {
            _latency = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LatencyText));
            OnPropertyChanged(nameof(LatencyColorText));
            OnPropertyChanged(nameof(MetricText));
            OnPropertyChanged(nameof(MetricColorText));
        }
    }

    /// <summary>真实下载速度（MB/s）。测过之后卡片优先显示速度而不是延迟。</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            _speed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(SpeedColorText));
            OnPropertyChanged(nameof(MetricText));
            OnPropertyChanged(nameof(MetricColorText));
        }
    }

    public string LatencyText => Latency < 0 ? "未测" : $"{Latency}ms";

    public string LatencyColorText => Latency switch
    {
        < 0 => "#8E8E93",
        < 100 => "#34C759",
        < 300 => "#FF9500",
        _ => "#FF3B30",
    };

    public string SpeedText => Speed switch
    {
        < 0 => "…",
        0 => "超时",
        < 1 => $"{Speed * 1024:F0} KB/s",
        _ => $"{Speed:F1} mb/s",
    };

    public string SpeedColorText => Speed switch
    {
        <= 0 => "#FF3B30",
        < 1 => "#FF9500",
        < 5 => "#FF9500",
        _ => "#34C759",
    };

    /// <summary>卡片右上角指标：测过下载速度显示速度，否则显示延迟。</summary>
    public string MetricText => Speed >= 0 ? SpeedText : LatencyText;

    public string MetricColorText => Speed >= 0 ? SpeedColorText : LatencyColorText;

    /// <summary>从节点名推断地区 → 国旗徽标颜色与代码（识别不出为 null，徽标隐藏）。</summary>
    public (string Color, string Code)? Flag => CountryMapper.Match(Name);

    public Brush? FlagBrush => CountryMapper.MatchBrush(Name);

    public string FlagCode => Flag?.Code ?? "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}

/// <summary>首页（M2：全部数据来自内核 API，无任何示例数据；未导入订阅时展示空状态）。</summary>
public class HomeViewModel : INotifyPropertyChanged
{
    private const int SeriesLength = 60;
    private bool _speedTesting;                     // 下载测速进行中（再点一次按钮 = 停止）
    private CancellationTokenSource? _speedCts;
    private readonly AppRuntime _rt;
    private readonly Dispatcher _dispatcher;
    private readonly Queue<double> _upSeries = new();
    private readonly Queue<double> _downSeries = new();
    private long _sessionUp;
    private long _sessionDown;
    private DateTime _lastAutoTest = DateTime.Now;   // 自动测速基准：启动后先等一个完整间隔再测第一轮
    private readonly DispatcherTimer _autoTestTimer;
    private bool _latencyTesting;                    // 自动测延迟进行中（防重叠）
    private DateTime _lastAutoLatency = DateTime.Now;
    private readonly DispatcherTimer _autoLatencyTimer;

    public event PropertyChangedEventHandler? PropertyChanged;

    public HomeViewModel(AppRuntime rt)
    {
        _rt = rt;
        _dispatcher = Application.Current.Dispatcher;
        _rt.TrafficTick += OnTraffic;
        _rt.NodesChanged += () => _dispatcher.BeginInvoke(async () => await RefreshAsync());
        _rt.NodeSortChanged += () => _dispatcher.BeginInvoke(SortNodes);
        _rt.CoreVersionChanged += () => _dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(StatusSubtitleText)));
        _rt.ModeSwitchFailed += msg => _dispatcher.BeginInvoke(() =>
            MessageBox.Show(msg, "切换失败", MessageBoxButton.OK, MessageBoxImage.Warning));
        _rt.CoreKindChanged += () => _dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(KernelName));
            OnPropertyChanged(nameof(EmptyStateText));
            OnPropertyChanged(nameof(StatusSubtitleText));
        });
        _rt.CoreRunningChanged += running => _dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(CoreRunning));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(StatusTitle));
        });
        _rt.Profiles.Changed += () => _dispatcher.BeginInvoke(async () =>
        {
            // 订阅增删改（含配置页删除订阅）会使节点集合失效：正在进行的测速立即停止
            if (_speedTesting)
            {
                _speedCts?.Cancel();
            }
            await RefreshAsync();
        });
        // 自动测速：设置开启后按配置间隔对全部节点跑一轮。DispatcherTimer 保证与手动测速同在 UI 线程
        _autoTestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoTestTimer.Tick += async (_, _) => await AutoTestTickAsync();
        _autoTestTimer.Start();
        // 自动测延迟：内核分组延迟接口一次测全组（不切换出口），轻量
        _autoLatencyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoLatencyTimer.Tick += async (_, _) => await AutoLatencyTickAsync();
        _autoLatencyTimer.Start();
        // 速率检测间隔变更：序列的每点含义变了，清空旧序列并刷新窗口文案
        _rt.SpeedIntervalChanged += () => _dispatcher.BeginInvoke(() =>
        {
            _upSeries.Clear();
            _downSeries.Clear();
            OnPropertyChanged(nameof(ChartWindowText));
            OnPropertyChanged(nameof(UpSeries));
            OnPropertyChanged(nameof(DownSeries));
        });
        _rt.Log += m => { };

        for (var i = 0; i < SeriesLength; i++)
        {
            _upSeries.Enqueue(0);
            _downSeries.Enqueue(0);
        }

        AddSubscriptionCommand = new RelayCommand<object>(_ => MainWindow.RequestAddSubscription());
        SetModeCommand = new RelayCommand<string>(async m =>
        {
            if (!Enum.TryParse<TunnelMode>(m, out var mode))
            {
                return;
            }
            IsSwitchingMode = true;
            OnPropertyChanged(nameof(IsSwitchingMode));
            CommandManager.InvalidateRequerySuggested();
            try
            {
                await _rt.SwitchModeAsync(mode);
            }
            finally
            {
                // BeginInvoke 返回 DispatcherOperation（可等待）：await 确保状态刷新先于命令解锁
                await _dispatcher.BeginInvoke(() =>
                {
                    IsSwitchingMode = false;
                    OnPropertyChanged(nameof(IsSwitchingMode));
                    OnPropertyChanged(nameof(Mode));
                    OnPropertyChanged(nameof(StatusTitle));
                    OnPropertyChanged(nameof(IsRunning));
                    OnPropertyChanged(nameof(StatusSubtitleText));
                    CommandManager.InvalidateRequerySuggested();
                });
            }
        }, _ => !IsSwitchingMode);
        SetRouteCommand = new RelayCommand<string>(async m =>
        {
            if (m == null)
            {
                return;
            }
            await _rt.SetRouteModeAsync(m);
            OnPropertyChanged(nameof(RouteModeText));
        });
        TestConnectivityCommand = new RelayCommand<object>(async _ => await TestConnectivityAsync());
        TestAllCommand = new RelayCommand<object>(async _ => await TestAllSpeedAsync(), _ => Nodes.Count > 0);
        TestNodeCommand = new RelayCommand<NodeRow>(async n => await TestSingleNodeAsync(n));
        SelectNodeCommand = new RelayCommand<NodeRow>(async n => await SelectNodeAsync(n));
        CopyNodeCommand = new RelayCommand<NodeRow>(n => Clipboard.SetText(n?.Name ?? ""));
        RefreshCommand = new RelayCommand<object>(async _ => await RefreshAsync());
    }

    public ObservableCollection<NodeRow> Nodes { get; } = [];

    public ObservableCollection<string> ProviderNames { get; } = ["全部节点"];

    public ICommand AddSubscriptionCommand { get; }
    public ICommand SetModeCommand { get; }
    public ICommand SetRouteCommand { get; }
    public ICommand TestConnectivityCommand { get; }
    public ICommand TestAllCommand { get; }

    /// <summary>首页节点网格列数：用户可在工具条上自选（1-6 栏），持久化保存。</summary>
    public int NodeColumns => Math.Clamp(_rt.Settings.NodeColumns, 1, 6);

    public IReadOnlyList<int> ColumnChoices { get; } = [1, 2, 3, 4, 5, 6];

    /// <summary>工具条下拉框的双向绑定入口：选择即持久化并重排列数。</summary>
    public int NodeColumnsSelected
    {
        get => NodeColumns;
        set
        {
            var cols = Math.Clamp(value, 1, 6);
            if (cols == _rt.Settings.NodeColumns)
            {
                return;
            }
            _rt.Settings.NodeColumns = cols;
            _rt.Settings.Save();
            OnPropertyChanged(nameof(NodeColumns));
        }
    }

    public ICommand TestNodeCommand { get; }
    public ICommand SelectNodeCommand { get; }
    public ICommand CopyNodeCommand { get; }
    public ICommand RefreshCommand { get; }

    public bool IsCoreInstalled => _rt.IsCoreInstalled;
    public bool CoreRunning => _rt.Core.IsRunning;

    public bool IsRunning => _rt.Settings.TunnelMode != TunnelMode.Off;

    /// <summary>切换模式（含首次下载 wintun）期间禁用开关，避免重复点击造成内核连环重启。</summary>
    public bool IsSwitchingMode { get; private set; }

    public TunnelMode Mode => _rt.Settings.TunnelMode;

    public string StatusTitle => Mode switch
    {
        TunnelMode.SystemProxy => "系统代理已开启",
        TunnelMode.Tun => "TUN 模式已开启",
        _ => "代理未开启",
    };

    public string RouteModeText => _rt.Settings.RouteMode switch
    {
        "global" => "全局模式",
        "direct" => "直连模式",
        _ => "规则模式",
    };

    /// <summary>大状态卡副行：真实端口 + 运行中内核版本 + 路由模式。</summary>
    public string StatusSubtitleText
    {
        get
        {
            // sing-box 的 /version 返回 "sing-box 1.14.4"，避免与内核名重复
            var ver = _rt.CoreVersion.Replace("sing-box ", "");
            var verText = ver.Length > 0 ? ver : "未运行";
            return $"混合端口 {_rt.Settings.MixedPort} · 内核 {(_rt.Settings.IsSingBoxCore ? "sing-box" : "mihomo")} {verText} · 模式 {RouteModeText}";
        }
    }

    public string UploadSpeedText => SpeedText(_lastUp);
    public string DownloadSpeedText => SpeedText(_lastDown);

    /// <summary>图表窗口文案：60 个采样点 × 检测间隔（1 秒=最近 60 秒，30 秒=最近 30 分钟）。</summary>
    public string ChartWindowText
    {
        get
        {
            var s = SeriesLength * Math.Max(1, _rt.Settings.SpeedIntervalSeconds);
            return s < 120 ? $"实时速率 · 最近 {s} 秒" : $"实时速率 · 最近 {s / 60} 分钟";
        }
    }

    /// <summary>当前代理内核名（mihomo / sing-box），空状态与副行共用。</summary>
    public string KernelName => _rt.Settings.IsSingBoxCore ? "sing-box" : "mihomo";

    public string EmptyStateText =>
        $"节点信息完全来自你导入的订阅，导入后由 {KernelName} 内核解析，本软件不存储节点。";

    /// <summary>内核未安装时的提示文案（跟随所选内核名）。</summary>
    public string KernelMissingHint => $"官方 {KernelName} 内核尚未下载到本机，请到「设置 → 更新」一键下载。";

    public string SessionUpText => MihomoApi.FormatBytes(_sessionUp);
    public string SessionDownText => MihomoApi.FormatBytes(_sessionDown);

    public string CurrentNodeName { get; private set; } = "";
    public string CurrentNodeLatencyText { get; private set; } = "";
    public string CurrentNodeLatencyColorText { get; private set; } = "#8E8E93";
    public string CurrentNodeProtoText { get; private set; } = "";
    public string CurrentNodeFlagCode { get; private set; } = "";
    public Brush? CurrentNodeFlagBrush { get; private set; }
    public string CurrentNodeBadgeText => IsRunning ? "当前" : "未启用";

    public string SubscriptionName { get; private set; } = "";
    public string SubscriptionUsageText { get; private set; } = "";
    public double SubscriptionPercent { get; private set; }
    public double SubscriptionRemainder { get; private set; } = 1;
    public string SubscriptionExpireText { get; private set; } = "";

    public bool HasSubscription => Nodes.Count > 0;
    public string ConnectivityText { get; private set; } = "未测试";
    public string ConnectivityColorText { get; private set; } = "#8E8E93";
    public bool IsTesting { get; private set; }

    public IReadOnlyList<double> UpSeries => _upSeries.ToList();
    public IReadOnlyList<double> DownSeries => _downSeries.ToList();

    private long _lastUp;
    private long _lastDown;
    private string _selectedProvider = "全部节点";

    public string SelectedProvider
    {
        get => _selectedProvider;
        set { _selectedProvider = value; _ = RefreshAsync(); }
    }

    private DateTime _lastSpeedAt = DateTime.Now;

    private void OnTraffic(long up, long down)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var now = DateTime.Now;
            var dt = (now - _lastSpeedAt).TotalSeconds;
            if (dt < 0.5)
            {
                return;
            }
            _lastUp = up;
            _lastDown = down;
            _lastSpeedAt = now;
            // 聚合节拍是间隔内的平均速率，累计量按间隔秒数还原
            var span = Math.Max(1, _rt.Settings.SpeedIntervalSeconds);
            _sessionUp += up * span;
            _sessionDown += down * span;

            _upSeries.Enqueue(up / 1048576.0);    // MB/s 绝对值，量程由 SpeedChart 动态缩放
            _downSeries.Enqueue(down / 1048576.0);
            while (_upSeries.Count > SeriesLength)
            {
                _upSeries.Dequeue();
            }
            while (_downSeries.Count > SeriesLength)
            {
                _downSeries.Dequeue();
            }

            OnPropertyChanged(nameof(UploadSpeedText));
            OnPropertyChanged(nameof(DownloadSpeedText));
            OnPropertyChanged(nameof(SessionUpText));
            OnPropertyChanged(nameof(SessionDownText));
            OnPropertyChanged(nameof(UpSeries));
            OnPropertyChanged(nameof(DownSeries));
        });
    }

    /// <summary>速度文本自带单位并自适应量级：低于 1 mb/s 用 KB/s，其余用小写 mb/s。</summary>
    private static string SpeedText(long bytesPerSec) => bytesPerSec switch
    {
        <= 0 => "0.0 KB/s",
        < 1024 * 1024 => $"{bytesPerSec / 1024.0:F1} KB/s",
        _ => $"{bytesPerSec / 1024.0 / 1024.0:F1} mb/s",
    };

    /// <summary>测速完成/停止提示 15 秒后自动清除（期间若无新的提示覆盖）。</summary>
    private void ClearHintLater(string content)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(15000);
            await _dispatcher.BeginInvoke(() =>
            {
                if (SpeedTestHint == content)
                {
                    SpeedTestHint = "";
                    OnPropertyChanged(nameof(SpeedTestHint));
                }
            });
        });
    }

    /// <summary>测速结果（MB/s）→ 显示文本：低于 1 mb/s 用 KB/s，其余用小写 mb/s。</summary>
    private static string SpeedTestText(double mbps) => mbps switch
    {
        < 1 => $"{mbps * 1024:F0} KB/s",
        _ => $"{mbps:F1} mb/s",
    };

    /// <summary>刷新首页全部数据。经 async void 调用，异常兜底保住界面并写入日志（内核重启间隙 API 不可达是常态）。</summary>
    public async Task RefreshAsync()
    {
        try
        {
            await RefreshCoreAsync();
        }
        catch (Exception e)
        {
            _rt.WriteLog($"[shadowrocket] 刷新失败：{e.Message}");
        }
    }

    private async Task RefreshCoreAsync()
    {
        OnPropertyChanged(nameof(IsCoreInstalled));
        OnPropertyChanged(nameof(CoreRunning));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(RouteModeText));

        var providers = await _rt.Api.GetProvidersAsync();
        var subs = await _rt.Api.GetSubscriptionInfoAsync();
        var current = await _rt.Api.GetCurrentNodeAsync();

        // 行对象每次重建，已测的下载速度按节点名带过去（内核不持久化速度）
        var prevSpeeds = new Dictionary<string, double>();
        foreach (var n in Nodes)
        {
            if (n.Speed >= 0 || n.Speed == -2) prevSpeeds[n.Name] = n.Speed;
        }

        var rows = providers.Values
            .Where(n => n.Type is not ("Selector" or "URLTest" or "Fallback" or "LoadBalance" or "Direct" or "Reject" or "Compatible" or "Pass"))
            .Select(n => new NodeRow
            {
                Name = n.Name,
                ProviderName = _rt.Profiles.Items.FirstOrDefault(x => x.Id == n.ProviderId)?.Name ?? n.ProviderId,
                ProviderId = n.ProviderId,
                Owner = this,
                TypeName = n.Type,
                Udp = n.Udp,
                Latency = n.Delay,
                Speed = prevSpeeds.TryGetValue(n.Name, out var sp) ? sp : -1,
                IsCurrent = n.Name == current,
            })
            .ToList();

        _dispatcher.Invoke(() =>
        {
            Nodes.Clear();
            foreach (var r in SortRows(SelectedProvider == "全部节点"
                         ? rows
                         : rows.Where(n => n.ProviderName == SelectedProvider).ToList()))
            {
                Nodes.Add(r);
            }
            // 下拉项用订阅显示名，与 NodeRow.ProviderName 对齐（用 provider id 会筛出空列表）
            var wanted = new[] { "全部节点" }
                .Concat(_rt.Profiles.Items.Select(p => p.Name.Length > 0 ? p.Name : p.Id))
                .Distinct()
                .ToList();
            if (!ProviderNames.SequenceEqual(wanted))
            {
                ProviderNames.Clear();
                foreach (var name in wanted)
                {
                    ProviderNames.Add(name);
                }
            }
            if (!wanted.Contains(_selectedProvider))
            {
                _selectedProvider = "全部节点";
                OnPropertyChanged(nameof(SelectedProvider));
            }
            CurrentNodeName = current;
            var cur = rows.FirstOrDefault(x => x.IsCurrent);
            CurrentNodeLatencyText = cur is { Latency: > 0 } ? $"{cur.Latency}ms" : "";
            CurrentNodeLatencyColorText = cur?.LatencyColorText ?? "#8E8E93";
            CurrentNodeProtoText = providers.TryGetValue(current, out var curInfo) ? curInfo.Type : "";
            CurrentNodeFlagCode = CountryMapper.Match(current)?.Code ?? "";
            CurrentNodeFlagBrush = CountryMapper.MatchBrush(current);
            OnPropertyChanged(nameof(CurrentNodeName));
            OnPropertyChanged(nameof(CurrentNodeLatencyText));
            OnPropertyChanged(nameof(CurrentNodeLatencyColorText));
            OnPropertyChanged(nameof(CurrentNodeProtoText));
            OnPropertyChanged(nameof(CurrentNodeFlagCode));
            OnPropertyChanged(nameof(CurrentNodeFlagBrush));
            OnPropertyChanged(nameof(CurrentNodeBadgeText));
            OnPropertyChanged(nameof(HasSubscription));
            OnPropertyChanged(nameof(StatusSubtitleText));

            // 快捷行展示"当前出口所属"的订阅；出口是 AUTO/DIRECT 等组时退回第一个订阅
            var curProviderId = providers.TryGetValue(current, out var ci) ? ci.ProviderId : "";
            var first = _rt.Profiles.Items.FirstOrDefault(p => p.Id == curProviderId) ?? _rt.Profiles.Items.FirstOrDefault();
            if (first != null && subs.TryGetValue(first.Id, out var si) && si.Total > 0)
            {
                SubscriptionName = first.Name;
                var used = si.Upload + si.Download;
                SubscriptionUsageText = $"已用 {MihomoApi.FormatBytes(used)} / {MihomoApi.FormatBytes(si.Total)}";
                SubscriptionPercent = Math.Clamp(used / (double)si.Total, 0, 1);
                SubscriptionRemainder = 1 - SubscriptionPercent;
                SubscriptionExpireText = si.Expire > 0
                    ? $"到期 {DateTimeOffset.FromUnixTimeSeconds(si.Expire).LocalDateTime:yyyy-MM-dd}"
                    : "长期有效";
            }
            else
            {
                SubscriptionName = first?.Name ?? "";
                SubscriptionUsageText = "";
                SubscriptionPercent = 0;
                SubscriptionRemainder = 1;
                SubscriptionExpireText = "";
            }
            OnPropertyChanged(nameof(SubscriptionName));
            OnPropertyChanged(nameof(SubscriptionUsageText));
            OnPropertyChanged(nameof(SubscriptionPercent));
            OnPropertyChanged(nameof(SubscriptionRemainder));
            OnPropertyChanged(nameof(SubscriptionExpireText));
        });
    }

    public async Task TestConnectivityAsync()
    {
        IsTesting = true;
        OnPropertyChanged(nameof(IsTesting));
        ConnectivityText = "测试中…";
        ConnectivityColorText = "#8E8E93";
        OnPropertyChanged(nameof(ConnectivityText));

        var delay = await _rt.Api.TestConnectivityAsync();
        ConnectivityText = delay switch
        {
            < 0 => "超时",
            _ => $"正常 · {delay}ms",
        };
        ConnectivityColorText = delay switch
        {
            < 0 => "#FF3B30",
            < 300 => "#34C759",
            _ => "#FF9500",
        };
        IsTesting = false;
        OnPropertyChanged(nameof(IsTesting));
        OnPropertyChanged(nameof(ConnectivityText));
        OnPropertyChanged(nameof(ConnectivityColorText));
    }

    /// <summary>
    /// 自动测延迟心跳：开关开启、内核在跑、空闲且距上一轮 ≥ 间隔时，
    /// 经内核分组延迟接口一次测全组（不切换出口），结果直接更新节点卡片；
    /// 排序模式为"延迟"时按新结果重排。
    /// </summary>
    private async Task AutoLatencyTickAsync()
    {
        if (!_rt.Settings.AutoLatencyTest || _latencyTesting || _speedTesting || !_rt.Core.IsRunning)
        {
            return;
        }
        var interval = TimeSpan.FromSeconds(Math.Max(30, _rt.Settings.AutoLatencyTestIntervalSeconds));
        if (DateTime.Now - _lastAutoLatency < interval)
        {
            return;
        }
        _latencyTesting = true;
        _lastAutoLatency = DateTime.Now;
        try
        {
            var delays = await _rt.Api.GetGroupDelayAsync("PROXY");
            if (delays.Count > 0)
            {
                foreach (var n in Nodes)
                {
                    if (delays.TryGetValue(n.Name, out var d))
                    {
                        n.Latency = d;
                    }
                }
                if (_rt.Settings.NodeSortMode == "latency")
                {
                    SortNodes();   // 延迟排序模式下，测完立即按新延迟重排
                }
            }
        }
        finally
        {
            _latencyTesting = false;
        }
    }

    /// <summary>按设置的排序模式排序列表：默认（已测速优先，未测按延迟）/名称/延迟/速度。</summary>
    private List<NodeRow> SortRows(List<NodeRow> rows) => _rt.Settings.NodeSortMode switch
    {
        "name" => rows.OrderBy(n => n.Name, StringComparer.CurrentCulture).ToList(),
        "latency" => rows.OrderBy(n => n.Latency < 0 ? int.MaxValue : n.Latency).ThenBy(n => n.Name).ToList(),
        "speed" => rows.OrderByDescending(n => n.Speed).ThenBy(n => n.Name).ToList(),
        _ => rows.OrderBy(n => n.Speed >= 0 ? 0 : (n.Latency < 0 ? int.MaxValue : n.Latency)).ToList(),
    };

    /// <summary>按当前排序模式原位重排 Nodes（Move 逐项就位，避免整列表重建导致闪烁）。</summary>
    private void SortNodes()
    {
        var sorted = SortRows(Nodes.ToList());
        for (var i = 0; i < sorted.Count; i++)
        {
            var j = Nodes.IndexOf(sorted[i]);
            if (j != i)
            {
                Nodes.Move(j, i);
            }
        }
    }

    /// <summary>
    /// 自动测速心跳：开关开启、内核在跑、空闲且距上一轮 ≥ 间隔时，复用"全部测速"跑一轮。
    /// 与手动测速共用 _speedTesting/_speedCts，中途手动点"停止测速"可随时打断。
    /// </summary>
    private async Task AutoTestTickAsync()
    {
        if (!_rt.Settings.AutoSpeedTest || _speedTesting || !_rt.Core.IsRunning)
        {
            return;
        }
        var interval = TimeSpan.FromSeconds(Math.Max(30, _rt.Settings.AutoSpeedTestIntervalSeconds));
        if (DateTime.Now - _lastAutoTest < interval)
        {
            return;
        }
        _lastAutoTest = DateTime.Now;
        await TestAllSpeedAsync();
    }

    /// <summary>全部测速 = 逐节点真实下载测速。再点一次按钮可随时停止，测完自动恢复原出口。</summary>
    public async Task TestAllSpeedAsync()
    {
        if (_speedTesting)
        {
            _speedCts?.Cancel();
            return;
        }
        var targets = Nodes.ToList();
        if (targets.Count == 0)
        {
            return;
        }

        _speedTesting = true;
        _speedCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(TestAllButtonText));
        CommandManager.InvalidateRequerySuggested();

        var original = (await _rt.Api.GetGroupAsync("PROXY"))?.Now ?? "";
        var okCount = 0;
        var failCount = 0;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                if (_speedCts.IsCancellationRequested) break;
                var n = targets[i];
                SpeedTestHint = $"正在测速 {i + 1}/{targets.Count} · {n.Name}";
                OnPropertyChanged(nameof(SpeedTestHint));
                n.Speed = -2;
                try
                {
                    var mbps = await _rt.MeasureNodeDownloadSpeedAsync(n.Name, 3.5, _speedCts.Token);
                    n.Speed = mbps;
                    if (mbps > 0) okCount++; else failCount++;
                }
                catch (OperationCanceledException)
                {
                    n.Speed = 0;
                    failCount++;
                    if (_speedCts.IsCancellationRequested) break;
                }
                catch
                {
                    n.Speed = 0;
                    failCount++;
                }
            }
            SpeedTestHint = _speedCts.IsCancellationRequested
                ? $"测速已停止 · {okCount} 个节点成功"
                : $"测速完成 · {okCount} 个节点有速度，{failCount} 个超时";
            ClearHintLater(SpeedTestHint);
        }
        finally
        {
            // 测速过程不断切换出口，结束后恢复用户原本选中的节点
            if (original.Length > 0)
            {
                try { await _rt.Api.SelectInGroupAsync("PROXY", original); } catch { }
            }
            _speedTesting = false;
            _speedCts.Dispose();
            _speedCts = null;
            if (_rt.Settings.NodeSortMode == "speed")
            {
                SortNodes();   // 速度排序模式下，测完立即按结果重排
            }
            OnPropertyChanged(nameof(SpeedTestHint));
            OnPropertyChanged(nameof(TestAllButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string TestAllButtonText => _speedTesting ? "■ 停止测速" : "⟳ 全部测速";

    /// <summary>测速进度/结果提示（空 = 不显示）。</summary>
    public string SpeedTestHint { get; private set; } = "";

    public async Task SelectNodeAsync(NodeRow? node)
    {
        if (node == null)
        {
            return;
        }
        await _rt.Api.SelectNodeAsync(node.Name);
        await RefreshAsync();
    }

    /// <summary>右键单节点「测速」= 测该节点的真实下载速度（约 3.5 秒）。</summary>
    public async Task TestSingleNodeAsync(NodeRow? node)
    {
        if (node == null || _speedTesting)
        {
            return;
        }
        _speedTesting = true;
        _speedCts = new CancellationTokenSource();   // 与全部测速共用：订阅删除时可一并取消
        SpeedTestHint = $"正在测速 · {node.Name}";
        OnPropertyChanged(nameof(SpeedTestHint));
        try
        {
            var original = (await _rt.Api.GetGroupAsync("PROXY"))?.Now ?? "";
            node.Speed = -2;
            var mbps = await _rt.MeasureNodeDownloadSpeedAsync(node.Name, 3.5, _speedCts.Token);
            node.Speed = mbps;
            SpeedTestHint = mbps > 0 ? $"{node.Name} · {SpeedTestText(mbps)}" : $"{node.Name} · 超时";
            ClearHintLater(SpeedTestHint);
            if (original.Length > 0)
            {
                try { await _rt.Api.SelectInGroupAsync("PROXY", original); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            node.Speed = 0;
            SpeedTestHint = "测速已停止";
            ClearHintLater(SpeedTestHint);
        }
        catch
        {
            node.Speed = 0;
            SpeedTestHint = $"{node.Name} · 超时";
            ClearHintLater(SpeedTestHint);
        }
        finally
        {
            _speedTesting = false;
            _speedCts?.Dispose();
            _speedCts = null;
            if (_rt.Settings.NodeSortMode == "speed")
            {
                SortNodes();   // 速度排序模式下，单节点测完立即按新结果归位
            }
            OnPropertyChanged(nameof(SpeedTestHint));
            OnPropertyChanged(nameof(TestAllButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
