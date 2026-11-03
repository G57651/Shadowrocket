using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Shadowrocket.Core;

/// <summary>模型：内核 API 返回的节点 / 策略组 / 订阅流量信息。</summary>
public class ProxyNodeInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Udp { get; set; }
    public int Delay { get; set; } = -1;   // -1 = 未测速
    public string ProviderId { get; set; } = "";
}

public class GroupInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Now { get; set; } = "";
    public int Count { get; set; }
}

public class SubscriptionInfo
{
    public long Upload { get; set; }
    public long Download { get; set; }
    public long Total { get; set; }
    public long Expire { get; set; }
}

public class ConnectionItem
{
    public string Id { get; set; } = "";
    public string Host { get; set; } = "";
    public string Process { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Chains { get; set; } = "";
    public string Upload { get; set; } = "";
    public string Download { get; set; } = "";
    public string Duration { get; set; } = "";
}

/// <summary>mihomo external-controller 的 REST + WebSocket 客户端。</summary>
public class MihomoApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private static readonly JsonSerializerOptions JsonOpt = new() { PropertyNameCaseInsensitive = true };

    public MihomoApi(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient(new HttpClientHandler { Proxy = null, UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{settings.ControllerPort}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", settings.Secret);
    }

    private Uri Ws(string path) => new($"ws://127.0.0.1:{_settings.ControllerPort}{path}?token={_settings.Secret}");

    // ---------- REST ----------

    public async Task<bool> IsAliveAsync()
    {
        try
        {
            using var r = await _http.GetAsync("/version");
            return r.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GetVersionAsync()
    {
        var doc = await GetJson("/version");
        return doc.RootElement.GetProperty("version").GetString() ?? "";
    }

    public async Task SetModeAsync(string mode)
    {
        var content = new StringContent($"{{\"mode\":\"{mode}\"}}", Encoding.UTF8, "application/json");
        await _http.PatchAsync("/configs", content);
    }

    public async Task<Dictionary<string, ProxyNodeInfo>> GetProvidersAsync()
    {
        if (_settings.IsSingBoxCore)
        {
            return await GetProxiesSingBoxAsync();
        }
        var result = new Dictionary<string, ProxyNodeInfo>();
        try
        {
            var doc = await GetJson("/providers/proxies");
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("providers", out var providers))
            {
                return result;
            }
            foreach (var p in providers.EnumerateObject())
            {
                if (!p.Value.TryGetProperty("proxies", out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var n in arr.EnumerateArray())
                {
                    var info = new ProxyNodeInfo
                    {
                        Name = n.GetProperty("name").GetString() ?? "",
                        Type = n.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                        Udp = n.TryGetProperty("udp", out var u) && u.ValueKind == JsonValueKind.True,
                        ProviderId = p.Name,
                        Delay = GetLastDelay(n),
                    };
                    if (info.Name.Length > 0 && !result.ContainsKey(info.Name))
                    {
                        result[info.Name] = info;
                    }
                }
            }
        }
        catch
        {
            // 内核未就绪时返回空
        }
        return result;
    }

    /// <summary>
    /// sing-box 没有 provider 概念：clash_api 的 /proxies 列出全部出站，
    /// 过滤掉策略组/内置出站后剩下的就是节点。
    /// </summary>
    private async Task<Dictionary<string, ProxyNodeInfo>> GetProxiesSingBoxAsync()
    {
        var result = new Dictionary<string, ProxyNodeInfo>();
        try
        {
            var doc = await GetJson("/proxies");
            if (!doc.RootElement.TryGetProperty("proxies", out var all))
            {
                return result;
            }
            var special = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE", "dns-out", "BLOCK" };
            foreach (var p in all.EnumerateObject())
            {
                var name = p.Name;
                var type = p.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (special.Contains(name) || special.Contains(type) ||
                    type is "Selector" or "URLTest" or "Fallback" or "LoadBalance")
                {
                    continue;
                }
                var info = new ProxyNodeInfo
                {
                    Name = name,
                    Type = type,
                    Udp = p.Value.TryGetProperty("udp", out var u) && u.ValueKind == JsonValueKind.True,
                    ProviderId = "singbox",
                    Delay = GetLastDelay(p.Value),
                };
                if (info.Name.Length > 0 && !result.ContainsKey(info.Name))
                {
                    result[info.Name] = info;
                }
            }
        }
        catch
        {
        }
        return result;
    }

    public async Task<Dictionary<string, SubscriptionInfo>> GetSubscriptionInfoAsync()
    {
        var result = new Dictionary<string, SubscriptionInfo>();
        try
        {
            var doc = await GetJson("/providers/proxies");
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("providers", out var providers))
            {
                return result;
            }
            foreach (var p in providers.EnumerateObject())
            {
                if (!p.Value.TryGetProperty("subscriptionInfo", out var si) || si.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                result[p.Name] = new SubscriptionInfo
                {
                    Upload = si.TryGetProperty("Upload", out var u) ? u.GetInt64() : 0,
                    Download = si.TryGetProperty("Download", out var d) ? d.GetInt64() : 0,
                    Total = si.TryGetProperty("Total", out var t) ? t.GetInt64() : 0,
                    Expire = si.TryGetProperty("Expire", out var e) ? e.GetInt64() : 0,
                };
            }
        }
        catch
        {
        }
        return result;
    }

    private static int GetLastDelay(JsonElement node)
    {
        if (node.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Array && h.GetArrayLength() > 0)
        {
            var last = h[h.GetArrayLength() - 1];
            if (last.TryGetProperty("delay", out var d) && d.TryGetInt32(out var v) && v > 0)
            {
                // 超时的节点内核会写入约等于 timeout 的延迟值，不能当成可用延迟显示
                return v >= 4999 ? -1 : v;
            }
        }
        return -1;
    }

    public async Task<List<GroupInfo>> GetGroupsAsync()
    {
        var list = new List<GroupInfo>();
        try
        {
            var doc = await GetJson("/proxies");
            if (!doc.RootElement.TryGetProperty("proxies", out var all))
            {
                return list;
            }
            foreach (var g in all.EnumerateObject())
            {
                var type = g.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (type is not ("Selector" or "URLTest" or "Fallback" or "LoadBalance"))
                {
                    continue;
                }
                list.Add(new GroupInfo
                {
                    Name = g.Name,
                    Type = type,
                    Now = g.Value.TryGetProperty("now", out var n) ? n.GetString() ?? "" : "",
                    Count = g.Value.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0,
                });
            }
        }
        catch
        {
        }
        return list;
    }

    public async Task<string> GetCurrentNodeAsync()
    {
        try
        {
            var doc = await GetJson("/proxies/PROXY");
            var now = doc.RootElement.TryGetProperty("now", out var n) ? n.GetString() ?? "" : "";
            if (now is "AUTO" or "DIRECT" or "PROXY")
            {
                // 显示自动组的真实出口，而不是组名
                var inner = await GetJson($"/proxies/{Uri.EscapeDataString(now)}");
                if (inner.RootElement.TryGetProperty("now", out var n2))
                {
                    var real = n2.GetString() ?? "";
                    if (real.Length > 0)
                    {
                        return real;
                    }
                }
            }
            return now;
        }
        catch
        {
            return "";
        }
    }

    public async Task SelectNodeAsync(string node)
    {
        var content = new StringContent(JsonSerializer.Serialize(new { name = node }), Encoding.UTF8, "application/json");
        await _http.PutAsync("/proxies/PROXY", content);
    }

    /// <summary>
    /// 单节点延迟测试；失败或超时返回 -1。
    /// mihomo：订阅节点不会出现在 /proxies 全局表，必须走 provider 的 healthcheck；
    /// sing-box：clash_api 没有 provider 端点，统一走 /proxies/{name}/delay。
    /// </summary>
    public Task<int> TestDelayAsync(string node, string providerId = "") =>
        providerId.Length > 0 && !_settings.IsSingBoxCore
            ? TestProviderNodeDelayAsync(providerId, node)
            : TestProxyDelayAsync(node);

    private async Task<int> TestProxyDelayAsync(string node)
    {
        try
        {
            var url = Uri.EscapeDataString(_settings.LatencyTestUrl);
            using var r = await _http.GetAsync($"/proxies/{Uri.EscapeDataString(node)}/delay?timeout={_settings.LatencyTimeoutMs}&url={url}");
            return await ReadDelayAsync(r);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>GET /providers/proxies/{provider}/{node}/healthcheck：订阅节点的测速端点。</summary>
    private async Task<int> TestProviderNodeDelayAsync(string providerId, string node)
    {
        try
        {
            var url = Uri.EscapeDataString(_settings.LatencyTestUrl);
            var path = $"/providers/proxies/{Uri.EscapeDataString(providerId)}/{Uri.EscapeDataString(node)}/healthcheck?timeout={_settings.LatencyTimeoutMs}&url={url}";
            using var r = await _http.GetAsync(path);
            return await ReadDelayAsync(r);
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<int> ReadDelayAsync(HttpResponseMessage r)
    {
        if (!r.IsSuccessStatusCode)
        {
            return -1;
        }
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("delay", out var d) || !d.TryGetInt32(out var v))
        {
            return -1;
        }
        return v > 0 && v < 4999 ? v : -1;
    }

    /// <summary>
    /// GET /group/{name}/delay：一次请求测完整组，返回 节点名 → 延迟。
    /// 内核自己控制并发并把结果写进节点 history，比逐节点请求更准也更快；
    /// 测不通的节点不会出现在返回结果里。
    /// </summary>
    public async Task<Dictionary<string, int>> GetGroupDelayAsync(string group)
    {
        var result = new Dictionary<string, int>();
        try
        {
            var url = Uri.EscapeDataString(_settings.LatencyTestUrl);
            using var r = await _http.GetAsync($"/group/{Uri.EscapeDataString(group)}/delay?timeout={_settings.LatencyTimeoutMs}&url={url}");
            if (!r.IsSuccessStatusCode)
            {
                return result;
            }
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.TryGetInt32(out var v) && v > 0)
                    {
                        result[p.Name] = v;
                    }
                }
            }
        }
        catch
        {
            // 内核未就绪/组不存在：返回空表，调用方逐节点回退
        }
        return result;
    }

    public async Task<int> TestConnectivityAsync()
    {
        return await TestDelayAsync("PROXY");
    }

    public async Task UpdateProviderAsync(string id)
    {
        await _http.PutAsync($"/providers/proxies/{Uri.EscapeDataString(id)}", null);
    }

    public record RuleItem(string Type, string Payload, string Proxy);

    /// <summary>GET /rules：全部规则（规则查看器与规则测试用）。</summary>
    public async Task<List<RuleItem>> GetRulesAsync()
    {
        var list = new List<RuleItem>();
        try
        {
            var doc = await GetJson("/rules");
            if (doc.RootElement.TryGetProperty("rules", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in arr.EnumerateArray())
                {
                    list.Add(new RuleItem(
                        r.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                        r.TryGetProperty("payload", out var p) ? p.GetString() ?? "" : "",
                        r.TryGetProperty("proxy", out var px) ? px.GetString() ?? "" : ""));
                }
            }
        }
        catch
        {
        }
        return list;
    }

    /// <summary>GET /proxies/{group}：组成员与当前选中。</summary>
    public async Task<(string Now, List<string> Members)?> GetGroupAsync(string group)
    {
        try
        {
            var doc = await GetJson($"/proxies/{Uri.EscapeDataString(group)}");
            var root = doc.RootElement;
            var now = root.TryGetProperty("now", out var n) ? n.GetString() ?? "" : "";
            var members = new List<string>();
            if (root.TryGetProperty("all", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    members.Add(m.GetString() ?? "");
                }
            }
            return (now, members);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PUT /proxies/{group}：在指定策略组里切换节点。</summary>
    public async Task SelectInGroupAsync(string group, string node)
    {
        var content = new StringContent(JsonSerializer.Serialize(new { name = node }), Encoding.UTF8, "application/json");
        await _http.PutAsync($"/proxies/{Uri.EscapeDataString(group)}", content);
    }

    public async Task<int> GetRuleCountAsync()
    {
        try
        {
            var doc = await GetJson("/rules");
            return doc.RootElement.TryGetProperty("rules", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0;
        }
        catch
        {
            return 0;
        }
    }

    public async Task CloseConnectionAsync(string id)
    {
        await _http.DeleteAsync($"/connections/{Uri.EscapeDataString(id)}");
    }

    public async Task CloseAllConnectionsAsync()
    {
        await _http.DeleteAsync("/connections");
    }

    private async Task<JsonDocument> GetJson(string path)
    {
        var text = await _http.GetStringAsync(path);
        return JsonDocument.Parse(text);
    }

    /// <summary>原样读取内核某个 GET 端点的 JSON（诊断与自检用）。</summary>
    public async Task<string> GetJsonRawAsync(string path) => await _http.GetStringAsync(path);

    // ---------- WebSocket ----------

    /// <summary>订阅 /traffic：每秒回调上下行字节数。</summary>
    public Task SubscribeTrafficAsync(Action<long, long> onTick, CancellationToken ct) =>
        RunWsAsync("/traffic", bytes =>
        {
            var doc = JsonDocument.Parse(bytes);
            onTick(
                doc.RootElement.TryGetProperty("up", out var u) ? u.GetInt64() : 0,
                doc.RootElement.TryGetProperty("down", out var d) ? d.GetInt64() : 0);
        }, ct);

    /// <summary>订阅 /logs：每条回调 (level, payload)。</summary>
    public Task SubscribeLogsAsync(Action<string, string> onLog, CancellationToken ct) =>
        RunWsAsync("/logs?level=info", bytes =>
        {
            var doc = JsonDocument.Parse(bytes);
            onLog(
                doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                doc.RootElement.TryGetProperty("payload", out var p) ? p.GetString() ?? "" : "");
        }, ct);

    /// <summary>订阅 /connections：每次快照回调（约每秒）。</summary>
    public Task SubscribeConnectionsAsync(Action<List<ConnectionItem>, long, long> onSnapshot, CancellationToken ct) =>
        RunWsAsync("/connections", bytes =>
        {
            var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            var list = new List<ConnectionItem>();
            if (root.TryGetProperty("connections", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    var meta = c.TryGetProperty("metadata", out var m) ? m : default;
                    list.Add(new ConnectionItem
                    {
                        Id = c.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                        Host = HostOf(meta),
                        Process = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("process", out var pr) ? pr.GetString() ?? "" : "",
                        Rule = c.TryGetProperty("rule", out var rl) ? rl.GetString() ?? "" : "",
                        Chains = c.TryGetProperty("chains", out var ch) && ch.ValueKind == JsonValueKind.Array
                            ? string.Join(" → ", ch.EnumerateArray().Select(x => x.GetString()))
                            : "",
                        Upload = FormatBytes(c.TryGetProperty("upload", out var up) ? up.GetInt64() : 0),
                        Download = FormatBytes(c.TryGetProperty("download", out var dl) ? dl.GetInt64() : 0),
                        Duration = DurationOf(c),
                    });
                }
            }
            onSnapshot(
                list.OrderByDescending(x => x.Duration).ToList(),
                root.TryGetProperty("uploadTotal", out var ut) ? ut.GetInt64() : 0,
                root.TryGetProperty("downloadTotal", out var dt) ? dt.GetInt64() : 0);
        }, ct);

    private static string HostOf(JsonElement meta)
    {
        if (meta.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        if (meta.TryGetProperty("host", out var h) && h.GetString() is { Length: > 0 } s)
        {
            return s;
        }
        return meta.TryGetProperty("destinationIP", out var ip) ? ip.GetString() ?? "" : "";
    }

    private static string DurationOf(JsonElement c)
    {
        if (!c.TryGetProperty("start", out var s))
        {
            return "";
        }
        if (!DateTime.TryParse(s.GetString(), out var start))
        {
            return "";
        }
        var d = DateTime.Now - start.ToLocalTime();
        return d < TimeSpan.Zero ? "00:00" : $"{(int)d.TotalHours:d2}:{d.Minutes:d2}:{d.Seconds:d2}";
    }

    public static string FormatBytes(long b)
    {
        return b switch
        {
            >= 1L << 30 => $"{b / (double)(1L << 30):F2} GB",
            >= 1L << 20 => $"{b / (double)(1L << 20):F2} MB",
            >= 1L << 10 => $"{b / (double)(1L << 10):F1} KB",
            _ => $"{b} B",
        };
    }

    private async Task RunWsAsync(string path, Action<byte[]> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(Ws(path), ct);
                var buffer = new byte[256 * 1024];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            throw new IOException("closed");
                        }
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);
                    onMessage(ms.ToArray());
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 内核未启动/重启中：等待后重连
            }
            try
            {
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
