using System.IO;
using System.Text.Json;

namespace Shadowrocket.Core;

/// <summary>
/// 流量历史：按天持久化（yyyy-MM-dd → 上/下行字节），
/// 数据页"今日/本周/本月"来自这里。刷新频率 30 秒落盘一次，退出时强制落盘。
/// </summary>
public class TrafficHistoryStore
{
    private readonly string _filePath;
    private Dictionary<string, long[]> _days = new();   // date → [up, down]
    private long _pendingUp;
    private long _pendingDown;
    private DateTime _lastFlush = DateTime.Now;

    public TrafficHistoryStore(AppSettings settings)
    {
        _filePath = Path.Combine(AppSettings.Dir, "traffic-history.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, long[]>>(File.ReadAllText(_filePath));
                if (data != null)
                {
                    _days = data;
                }
            }
        }
        catch
        {
            _days = new Dictionary<string, long[]>();
        }
    }

    public void Add(long up, long down)
    {
        _pendingUp += up;
        _pendingDown += down;
        if (DateTime.Now - _lastFlush > TimeSpan.FromSeconds(30))
        {
            Flush();
        }
    }

    public void Flush()
    {
        if (_pendingUp == 0 && _pendingDown == 0 && _days.Count > 0)
        {
            _lastFlush = DateTime.Now;
            return;
        }
        var key = DateTime.Today.ToString("yyyy-MM-dd");
        if (!_days.TryGetValue(key, out var bucket))
        {
            bucket = new long[2];
            _days[key] = bucket;
        }
        bucket[0] += _pendingUp;
        bucket[1] += _pendingDown;
        _pendingUp = 0;
        _pendingDown = 0;
        _lastFlush = DateTime.Now;
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_days));
        }
        catch
        {
        }
    }

    public (long Up, long Down) Today()
    {
        Flush();
        return Get(DateTime.Today);
    }

    public (long Up, long Down) Week()
    {
        Flush();
        long up = 0, down = 0;
        for (var i = 0; i < 7; i++)
        {
            var (u, d) = Get(DateTime.Today.AddDays(-i));
            up += u;
            down += d;
        }
        return (up, down);
    }

    public (long Up, long Down) Month()
    {
        Flush();
        long up = 0, down = 0;
        var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        for (var d = first; d <= DateTime.Today; d = d.AddDays(1))
        {
            var (u, dn) = Get(d);
            up += u;
            down += dn;
        }
        return (up, down);
    }

    private (long, long) Get(DateTime date)
    {
        if (_days.TryGetValue(date.ToString("yyyy-MM-dd"), out var b))
        {
            return (b[0], b[1]);
        }
        return (0, 0);
    }
}
