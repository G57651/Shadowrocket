using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Shadowrocket.Core;

namespace Shadowrocket.ViewModels;

public class LogRow
{
    public string Time { get; init; } = "";
    public string Level { get; init; } = "";
    public string Payload { get; init; } = "";
}

/// <summary>数据页：实时连接（/connections WebSocket）、流量曲线、内核日志（/logs WebSocket）。</summary>
public class DataViewModel : INotifyPropertyChanged
{
    private const int MaxLogs = 500;
    private const int SeriesLength = 60;

    private readonly AppRuntime _rt;
    private readonly Dispatcher _dispatcher;
    private readonly Queue<double> _upSeries = new();
    private readonly Queue<double> _downSeries = new();
    private readonly List<LogRow> _allLogs = [];
    private readonly Dictionary<string, ConnRowVm> _connRows = new(StringComparer.Ordinal);
    private List<ConnectionItem> _lastSnapshot = [];
    private bool _paused;
    private string _levelFilter = "全部";
    private string _searchText = "";
    private string _connSearch = "";
    private long _totalUp;
    private long _totalDown;
    private long _lastUp;
    private long _lastDown;
    private long _sessionUp;
    private long _sessionDown;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DataViewModel(AppRuntime rt)
    {
        _rt = rt;
        _dispatcher = Application.Current.Dispatcher;
        // 速率检测间隔变更：序列每点含义变了，清空旧序列
        _rt.SpeedIntervalChanged += () => _dispatcher.BeginInvoke(() =>
        {
            _upSeries.Clear();
            _downSeries.Clear();
            OnPropertyChanged(nameof(UpSeries));
            OnPropertyChanged(nameof(DownSeries));
        });
        _rt.ConnectionsSnapshot += (list, ut, dt) => _dispatcher.BeginInvoke(() =>
        {
            if (!_paused)
            {
                // 暂停 = 冻结当前画面，但列表对象留着，搜索框仍可过滤
                _lastSnapshot = list;
                _totalUp = ut;
                _totalDown = dt;
                OnPropertyChanged(nameof(TotalUpText));
                OnPropertyChanged(nameof(TotalDownText));
                ApplyConnections();
            }
        });
        _rt.TrafficTick += (up, down) => _dispatcher.BeginInvoke(() =>
        {
            _lastUp = up;
            _lastDown = down;
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
            OnPropertyChanged(nameof(UpSeries));
            OnPropertyChanged(nameof(DownSeries));
            OnPropertyChanged(nameof(UploadSpeedText));
            OnPropertyChanged(nameof(DownloadSpeedText));
            OnPropertyChanged(nameof(SessionUpText));
            OnPropertyChanged(nameof(SessionDownText));
        });
        _rt.Log += (m) => _dispatcher.BeginInvoke(() =>
        {
            var level = "info";
            var payload = m;
            if (m.StartsWith('['))
            {
                var end = m.IndexOf(']');
                if (end > 0)
                {
                    level = m[1..end];
                    payload = m[(end + 2)..];
                }
            }
            _allLogs.Add(new LogRow { Time = DateTime.Now.ToString("HH:mm:ss"), Level = level, Payload = payload });
            if (_allLogs.Count > MaxLogs)
            {
                _allLogs.RemoveRange(0, _allLogs.Count - MaxLogs);
            }
            ApplyLogFilter();
        });
        for (var i = 0; i < SeriesLength; i++)
        {
            _upSeries.Enqueue(0);
            _downSeries.Enqueue(0);
        }
        CloseAllCommand = new RelayCommand<object>(async _ =>
        {
            await _rt.Api.CloseAllConnectionsAsync();
            _rt.WriteLog("[shadowrocket] 已请求关闭全部连接");
        });
        CloseOneCommand = new RelayCommand<string>(async id =>
        {
            if (id == null)
            {
                return;
            }
            await _rt.Api.CloseConnectionAsync(id);
        });
        PauseCommand = new RelayCommand<object>(_ =>
        {
            _paused = !_paused;
            OnPropertyChanged(nameof(PauseText));
            OnPropertyChanged(nameof(IsPaused));
        });
        ClearLogsCommand = new RelayCommand<object>(_ =>
        {
            _allLogs.Clear();
            ApplyLogFilter();
        });
        SetLevelCommand = new RelayCommand<string>(l =>
        {
            _levelFilter = l ?? "全部";
            OnPropertyChanged(nameof(LevelFilter));
            OnPropertyChanged(nameof(LevelWarningLabel));
            OnPropertyChanged(nameof(LevelErrorLabel));
            OnPropertyChanged(nameof(LevelAllLabel));
            ApplyLogFilter();
        });
        RefreshCommand = new RelayCommand<object>(async _ => await RefreshAsync());
        var historyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        historyTimer.Tick += (_, _) => RefreshHistory();
        historyTimer.Start();
    }

    private void RefreshHistory()
    {
        OnPropertyChanged(nameof(TodayUpText));
        OnPropertyChanged(nameof(TodayDownText));
        OnPropertyChanged(nameof(WeekUpText));
        OnPropertyChanged(nameof(WeekDownText));
        OnPropertyChanged(nameof(MonthUpText));
        OnPropertyChanged(nameof(MonthDownText));
    }

    public ObservableCollection<ConnRowVm> Connections { get; } = [];
    public ObservableCollection<LogRow> Logs { get; } = [];

    public ICommand CloseAllCommand { get; }
    public ICommand CloseOneCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand SetLevelCommand { get; }
    public ICommand RefreshCommand { get; }

    public string TotalUpText => MihomoApi.FormatBytes(_totalUp);
    public string TotalDownText => MihomoApi.FormatBytes(_totalDown);
    public int ConnectionCount => Connections.Count;

    public string UploadSpeedText => SpeedText(_lastUp);
    public string DownloadSpeedText => SpeedText(_lastDown);
    public string SessionUpText => MihomoApi.FormatBytes(_sessionUp);
    public string SessionDownText => MihomoApi.FormatBytes(_sessionDown);

    /// <summary>速度文本自带单位并自适应量级：低于 1 mb/s 用 KB/s，其余用小写 mb/s。</summary>
    private static string SpeedText(long bytesPerSec) => bytesPerSec switch
    {
        <= 0 => "0.0 KB/s",
        < 1024 * 1024 => $"{bytesPerSec / 1024.0:F1} KB/s",
        _ => $"{bytesPerSec / 1024.0 / 1024.0:F1} mb/s",
    };

    public string PauseText => _paused ? "▶ 继续" : "⏸ 暂停";
    public bool IsPaused => _paused;
    public string LevelFilter => _levelFilter;
    public string LevelWarningLabel => _levelFilter == "warning" ? "✓ warning" : "warning";
    public string LevelErrorLabel => _levelFilter == "error" ? "✓ error" : "error";
    public string LevelAllLabel => _levelFilter == "全部" ? "✓ 全部" : "全部";
    public IReadOnlyList<double> UpSeries => _upSeries.ToList();
    public IReadOnlyList<double> DownSeries => _downSeries.ToList();

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; _dispatcher.BeginInvoke(ApplyLogFilter); }
    }

    public string ConnectionSearch
    {
        get => _connSearch;
        set { _connSearch = value; _dispatcher.BeginInvoke(ApplyConnections); }
    }

    public string TodayUpText => MihomoApi.FormatBytes(_rt.History.Today().Up);
    public string TodayDownText => MihomoApi.FormatBytes(_rt.History.Today().Down);
    public string WeekUpText => MihomoApi.FormatBytes(_rt.History.Week().Up);
    public string WeekDownText => MihomoApi.FormatBytes(_rt.History.Week().Down);
    public string MonthUpText => MihomoApi.FormatBytes(_rt.History.Month().Up);
    public string MonthDownText => MihomoApi.FormatBytes(_rt.History.Month().Down);

    public async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(ConnectionCount));
        OnPropertyChanged(nameof(TotalUpText));
        OnPropertyChanged(nameof(TotalDownText));
        RefreshHistory();
        await Task.CompletedTask;
    }

    /// <summary>
    /// 增量更新连接表：同 Id 的行原地改字段，新增的连接插到最前，消失的连接移除。
    /// 不能整表 Clear+重建，否则每秒都会打断用户的点击和滚动。
    /// </summary>
    private void ApplyConnections()
    {
        IEnumerable<ConnectionItem> q = _lastSnapshot;
        if (_connSearch.Length > 0)
        {
            q = q.Where(c => c.Host.Contains(_connSearch, StringComparison.OrdinalIgnoreCase) ||
                             c.Process.Contains(_connSearch, StringComparison.OrdinalIgnoreCase));
        }
        var visible = q.ToList();

        for (var i = Connections.Count - 1; i >= 0; i--)
        {
            var id = Connections[i].Id;
            if (!visible.Any(c => c.Id == id))
            {
                Connections.RemoveAt(i);
                _connRows.Remove(id);
            }
        }

        foreach (var c in visible)
        {
            if (_connRows.TryGetValue(c.Id, out var row))
            {
                row.Update(c);
            }
            else
            {
                row = new ConnRowVm(c);
                _connRows[c.Id] = row;
                Connections.Insert(0, row);
            }
        }
        OnPropertyChanged(nameof(ConnectionCount));
    }

    private void ApplyLogFilter()
    {
        _dispatcher.Invoke(() =>
        {
            Logs.Clear();
            IEnumerable<LogRow> q = _allLogs;
            if (_levelFilter != "全部")
            {
                q = q.Where(l => l.Level.Equals(_levelFilter, StringComparison.OrdinalIgnoreCase));
            }
            if (_searchText.Length > 0)
            {
                q = q.Where(l => l.Payload.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
            }
            foreach (var l in q.TakeLast(300))
            {
                Logs.Add(l);
            }
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}

/// <summary>活动连接行：字段可变，供增量刷新原地更新。</summary>
public class ConnRowVm : INotifyPropertyChanged
{
    public ConnRowVm(ConnectionItem c)
    {
        Id = c.Id;
        _host = c.Host;
        _process = c.Process;
        _rule = c.Rule;
        _chains = c.Chains;
        _upload = c.Upload;
        _download = c.Download;
        _duration = c.Duration;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    private string _host;
    private string _process;
    private string _rule;
    private string _chains;
    private string _upload;
    private string _download;
    private string _duration;

    public string Host { get => _host; private set { _host = value; OnPropertyChanged(); } }
    public string Process { get => _process; private set { _process = value; OnPropertyChanged(); } }
    public string Rule { get => _rule; private set { _rule = value; OnPropertyChanged(); } }
    public string Chains { get => _chains; private set { _chains = value; OnPropertyChanged(); } }
    public string Upload { get => _upload; private set { _upload = value; OnPropertyChanged(); } }
    public string Download { get => _download; private set { _download = value; OnPropertyChanged(); } }
    public string Duration { get => _duration; private set { _duration = value; OnPropertyChanged(); } }

    public void Update(ConnectionItem c)
    {
        Host = c.Host;
        Process = c.Process;
        Rule = c.Rule;
        Chains = c.Chains;
        Upload = c.Upload;
        Download = c.Download;
        Duration = c.Duration;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
