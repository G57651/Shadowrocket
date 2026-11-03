using System.IO;
using System.Net.Http;

namespace Shadowrocket.Core;

/// <summary>
/// 应用运行时：唯一的内核/订阅/代理协调入口。
/// 生命周期：加载设置 → 生成配置 → 启动内核 → 打开实时流；模式切换、订阅增删、端口修改都从这里走。
/// </summary>
public class AppRuntime : IDisposable
{
    public AppSettings Settings { get; }
    public ProfileStore Profiles { get; }
    public CoreManager Core { get; }
    public MihomoApi Api { get; private set; }
    public KernelUpdater Kernel { get; }
    public TrafficHistoryStore History { get; }

    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource _streams = new();

    public event Action<string>? Log;

    /// <summary>向日志流写一条 GUI 侧消息（事件只能在声明类内 Invoke，故提供方法入口）。</summary>
    public void WriteLog(string message) => Log?.Invoke(message);

    /// <summary>请求各界面重新拉取节点（外部改动后同步用）。</summary>
    public void NotifyNodesChanged() => NodesChanged?.Invoke();

    /// <summary>代理内核切换后通知界面刷新内核相关的展示文案。</summary>
    public event Action? CoreKindChanged;
    public void NotifyCoreKindChanged() => CoreKindChanged?.Invoke();

    /// <summary>节点排序模式切换后通知首页对现有列表原位重排（无需重新拉 API）。</summary>
    public event Action? NodeSortChanged;
    public void NotifyNodeSortChanged() => NodeSortChanged?.Invoke();

    /// <summary>实时速率刷新间隔变更后通知界面清空速率序列并刷新窗口文案。</summary>
    public event Action? SpeedIntervalChanged;
    public void NotifySpeedIntervalChanged() => SpeedIntervalChanged?.Invoke();

    /// <summary>速率聚合缓冲：内核每秒推一次原始速率，攒够设置间隔再取平均通知界面。</summary>
    private long _trafficAccumUp, _trafficAccumDown;
    private int _trafficAccumTicks;

    /// <summary>测速拉流地址（按顺序尝试）。OVH 已实测国内直连与代理均可达；
    /// Cloudflare speed 端点会对部分 IP 返回 403，故只作备选。</summary>
    public static readonly string[] SpeedTestUrls =
    [
        "https://proof.ovh.net/files/100Mb.dat",
        "http://speedtest.tele2.net/10MB.zip",
        "https://speed.cloudflare.com/__down?bytes=104857600",
    ];

    private const string SpeedTestUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36";

    /// <summary>
    /// 测量指定节点的真实下载速度：把该节点设为 PROXY 出口，
    /// 经本机混合端口拉流 seconds 秒计字节数，返回 MB/s（0 = 完全不通）。
    /// </summary>
    public async Task<double> MeasureNodeDownloadSpeedAsync(string node, double seconds, CancellationToken ct)
    {
        await Api.SelectNodeAsync(node);
        await Task.Delay(250, ct);   // 等内核切换出口生效

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        long total = 0;
        Exception? lastError = null;
        foreach (var url in SpeedTestUrls)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy($"127.0.0.1:{Settings.MixedPort}"),
                    UseProxy = true,
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(seconds + 6) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(SpeedTestUserAgent);
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                resp.EnsureSuccessStatusCode();
                await using var stream = resp.Content.ReadAsStream(timeout.Token);
                var buffer = new byte[65536];
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!timeout.IsCancellationRequested)
                {
                    var n = await stream.ReadAsync(buffer, timeout.Token);
                    if (n == 0) break;
                    total += n;
                    if (sw.Elapsed.TotalSeconds >= seconds) break;
                }
                if (total > 0)
                {
                    return total / 1024.0 / 1024.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.3);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 测速时间窗结束：用已收到的字节计算速度
                return total / 1024.0 / 1024.0 / seconds;
            }
            catch (Exception e)
            {
                lastError = e;
            }
        }
        if (lastError != null && ct.IsCancellationRequested)
        {
            throw lastError;
        }
        return 0;   // 所有测速源都拉不到流量：节点不通
    }
    public event Action<bool>? CoreRunningChanged;
    public event Action<long, long>? TrafficTick;              // (up, down) bytes/s
    public event Action<List<ConnectionItem>, long, long>? ConnectionsSnapshot;
    public event Action? NodesChanged;
    public event Action? CoreVersionChanged;

    /// <summary>模式切换失败（例如 UAC 被取消）时通知界面给出提示。</summary>
    public event Action<string>? ModeSwitchFailed;

    /// <summary>
    /// 首次开启 TUN 前由界面层弹出确认框（说明将下载 wintun 虚拟网卡驱动）；
    /// 返回 false 时取消本次切换。未接线（如自动化测试）时视为同意，保持原下载行为。
    /// </summary>
    public Func<Task<bool>>? ConfirmWintunDownload { get; set; }

    /// <summary>需要切换内核时（如检测到 sing-box 格式订阅）由界面层弹框确认；参数为提示文本。</summary>
    public Func<string, Task<bool>>? ConfirmCoreSwitch { get; set; }

    /// <summary>
    /// 内核下载完整性校验无法给出结论时（官方校验和缺失、回退内置清单或完全无来源）
    /// 由界面层弹框确认是否继续安装；参数为提示文本。返回 false 即拒绝安装。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmKernelInstall { get; set; }

    /// <summary>切换代理内核（mihomo / sing-box）并重启。sing-box 内核未安装时抛出。</summary>
    public void SetCoreKind(string kind)
    {
        if (kind != "mihomo" && kind != "singbox")
        {
            throw new ArgumentException("未知内核", kind);
        }
        if (Settings.CoreKind == kind)
        {
            return;
        }
        Settings.CoreKind = kind;
        Settings.Save();
        NotifyCoreKindChanged();
        if (!Core.IsCoreInstalled)
        {
            RegenerateConfig();   // 生成对应内核的配置文件（即使内核还没下载）
            throw new InvalidOperationException(
                kind == "singbox" ? "sing-box 内核尚未下载，请在设置里点击下载。" : "mihomo 内核尚未下载，请在设置里点击下载。");
        }
        RegenerateConfig();
        SwapApiAndRestartCore();
    }

    /// <summary>运行中内核上报的版本号，如 "v1.19.33"；内核未运行时为空。</summary>
    public string CoreVersion { get; private set; } = "";

    private async Task RefreshCoreVersionAsync()
    {
        string v;
        try
        {
            v = await Api.GetVersionAsync();
        }
        catch
        {
            v = "";   // 内核没在跑：清空，副行会显示"未运行"
        }
        if (v != CoreVersion)
        {
            CoreVersion = v;
            CoreVersionChanged?.Invoke();
        }
    }

    public AppRuntime()
    {
        Settings = AppSettings.Load();
        Profiles = new ProfileStore(Settings);
        Core = new CoreManager(Settings);
        Kernel = new KernelUpdater(Settings);
        // 完整性校验的用户确认转给界面层（未接线时视为拒绝，安全侧倾）
        Kernel.ConfirmUnverifiedInstall = async msg => ConfirmKernelInstall != null && await ConfirmKernelInstall(msg);
        Api = new MihomoApi(Settings);
        History = new TrafficHistoryStore(Settings);
        Core.Log += m =>
        {
            // 内核 stdout 行形如 time="..." level=info msg="..."，解析后与 /logs 流统一
            var match = System.Text.RegularExpressions.Regex.Match(m, "level=(\\w+) msg=\"(.*)\"\\s*$");
            Log?.Invoke(match.Success ? $"[{match.Groups[1].Value}] {match.Groups[2].Value}" : m);
        };
    }

    public bool IsCoreInstalled => Core.IsCoreInstalled;

    public Task StartAsync()
    {
        Settings.Save();   // 固定 secret，避免每次启动轮换
        // 启动兜底：上次异常退出（崩溃/强杀/断电）可能遗留指向本端口的系统代理，
        // 先清掉恢复直连，避免用户"打开软件前就断网"；内核就绪后 ApplySystemProxyState 会按当前模式重设
        try
        {
            if (SystemProxyService.IsSet(Settings.MixedPort))
            {
                SystemProxyService.Disable(Settings, Settings.MixedPort);
                Log?.Invoke("[shadowrocket] 已清理上次遗留的系统代理设置");
            }
        }
        catch { }
        if (!IsCoreInstalled)
        {
            Log?.Invoke("[shadowrocket] 内核未安装，请在「设置 → 更新」下载内核");
            return Task.CompletedTask;
        }
        RegenerateConfig();
        RestartCoreInternal(elevated: Settings.TunnelMode == TunnelMode.Tun);
        StartStreams();
        StartAutoUpdateLoop();   // 订阅自动更新调度

        // 同步系统代理状态（上次退出时的模式）
        ApplySystemProxyState();
        return Task.CompletedTask;
    }

    private void RestartCoreInternal(bool elevated)
    {
        Core.Start(elevated);
        _ = Task.Run(async () =>
        {
            var ok = await Core.WaitReadyAsync(TimeSpan.FromSeconds(15));
            Log?.Invoke(ok ? "[shadowrocket] 内核已就绪" : "[shadowrocket] 内核启动超时，请查看日志");
            CoreRunningChanged?.Invoke(ok);
            _ = RefreshCoreVersionAsync();
            if (ok)
            {
                NodesChanged?.Invoke();
            }
        });
    }

    private void StartStreams()
    {
        // 每次换 Api 实例都重建一组流：旧流必须先 Cancel 再 Dispose 才会真正退出
        // （Dispose 本身不置取消标志，旧循环会抱着已释放的 HttpClient 每 2 秒重试，
        // 最终以未观察的 ObjectDisposedException 收场，期间还会重复订阅）
        var oldStreams = _streams;
        _streams = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try { oldStreams.Cancel(); } catch (ObjectDisposedException) { }
        oldStreams.Dispose();
        var ct = _streams.Token;
        var api = Api;
        _ = api.SubscribeTrafficAsync((up, down) =>
        {
            History.Add(up, down);
            // 按设置的检测间隔聚合：N 个每秒样本取平均后作为一个界面节拍（间隔期间传输量由界面 × 间隔秒数还原）
            _trafficAccumUp += up;
            _trafficAccumDown += down;
            _trafficAccumTicks++;
            if (_trafficAccumTicks >= Math.Max(1, Settings.SpeedIntervalSeconds))
            {
                TrafficTick?.Invoke(_trafficAccumUp / _trafficAccumTicks, _trafficAccumDown / _trafficAccumTicks);
                _trafficAccumUp = _trafficAccumDown = 0;
                _trafficAccumTicks = 0;
            }
        }, ct);
        _ = api.SubscribeConnectionsAsync((list, ut, dt) => ConnectionsSnapshot?.Invoke(list, ut, dt), ct);
        _ = api.SubscribeLogsAsync((level, payload) => Log?.Invoke($"[{level}] {payload}"), ct);
    }

    /// <summary>端口/网络选项变更后：重建 API 客户端并重启内核与实时流。</summary>
    private void SwapApiAndRestartCore()
    {
        Api.Dispose();
        Api = new MihomoApi(Settings);
        StartStreams();
        RestartCoreInternal(elevated: Settings.TunnelMode == TunnelMode.Tun);
    }

    /// <summary>内核被外部（KernelUpdater）重启后同步界面状态：运行标志、版本号、节点列表。</summary>
    public void NotifyCoreRestarted()
    {
        _ = Task.Run(async () =>
        {
            var ok = await Core.WaitReadyAsync(TimeSpan.FromSeconds(15));
            CoreRunningChanged?.Invoke(ok);
            await RefreshCoreVersionAsync();
            if (ok)
            {
                NodesChanged?.Invoke();
            }
        });
    }

    private void ApplySystemProxyState()
    {
        try
        {
            if (Settings.TunnelMode == TunnelMode.SystemProxy)
            {
                SystemProxyService.Enable(Settings.MixedPort, Settings);
            }
            else
            {
                SystemProxyService.Disable(Settings, Settings.MixedPort);
            }
        }
        catch (Exception e)
        {
            Log?.Invoke($"[shadowrocket] 系统代理设置失败：{e.Message}");
        }
    }

    private readonly SemaphoreSlim _modeSwitch = new(1, 1);

    /// <summary>切换 TUN / 系统代理 / 关闭。会重写配置并重启内核。</summary>
    public async Task SwitchModeAsync(TunnelMode mode)
    {
        // 同一时刻只允许一次模式切换：连续点击会让上一个还没起稳的内核被下一次的 Stop 杀掉
        if (!await _modeSwitch.WaitAsync(TimeSpan.FromMilliseconds(200)))
        {
            Log?.Invoke("[shadowrocket] 上一次模式切换还在进行中，已忽略本次请求");
            return;
        }
        try
        {
            await SwitchModeCoreAsync(mode);
        }
        finally
        {
            _modeSwitch.Release();
        }
    }

    private async Task SwitchModeCoreAsync(TunnelMode mode)
    {
        if (mode == TunnelMode.Tun && !WintunService.IsInstalled(Settings))
        {
            // 首次开启 TUN：先征得用户同意，再下载 wintun 虚拟网卡驱动
            var confirmed = ConfirmWintunDownload == null || await ConfirmWintunDownload();
            if (!confirmed)
            {
                Log?.Invoke("[shadowrocket] 已取消开启 TUN（未下载 wintun 驱动）");
                return;
            }
            try
            {
                // 下载驱动必须异步等待，否则界面会假死到下载结束
                await WintunService.EnsureAsync(Settings, Log);
            }
            catch (Exception e)
            {
                Log?.Invoke($"[shadowrocket] wintun 下载失败，无法开启 TUN：{e.Message}");
                ModeSwitchFailed?.Invoke($"wintun 驱动下载失败，已取消开启 TUN：{e.Message}");
                return;
            }
        }
        var previousMode = Settings.Mode;
        Settings.Mode = mode.ToString();
        Settings.Save();
        RegenerateConfig();

        if (!IsCoreInstalled)
        {
            Log?.Invoke("[shadowrocket] 内核未安装，无法开启代理");
            return;
        }
        try
        {
            RestartCoreInternal(elevated: mode == TunnelMode.Tun);
        }
        catch (Exception e)
        {
            // 典型场景：TUN 需要的 UAC 授权被取消。必须退回切换前的模式并落盘，
            // 否则界面显示"TUN 已开启"而内核其实没跑，且异常会掀掉整个应用
            Log?.Invoke($"[shadowrocket] 切换到 {mode} 失败，已退回 {previousMode}：{e.Message}");
            Settings.Mode = previousMode;
            Settings.Save();
            RegenerateConfig();
            try
            {
                RestartCoreInternal(elevated: previousMode == nameof(TunnelMode.Tun));
            }
            catch (Exception e2)
            {
                Log?.Invoke($"[shadowrocket] 回退重启内核也失败：{e2.Message}");
            }
            ApplySystemProxyState();
            ModeSwitchFailed?.Invoke(e.Message);
            return;
        }

        // TUN 走虚拟网卡，不需要系统代理；关闭/系统代理按需设置
        try
        {
            if (mode == TunnelMode.SystemProxy)
            {
                SystemProxyService.Enable(Settings.MixedPort, Settings);
            }
            else
            {
                SystemProxyService.Disable(Settings, Settings.MixedPort);
            }
        }
        catch (Exception e)
        {
            Log?.Invoke($"[shadowrocket] 系统代理设置失败：{e.Message}");
        }
        Log?.Invoke($"[shadowrocket] 已切换到 {mode}");
    }

    /// <summary>切换 全局路由：rule / global / direct（运行时 PATCH，无需重启内核）。</summary>
    public async Task SetRouteModeAsync(string routeMode)
    {
        Settings.RouteMode = routeMode;
        Settings.Save();
        RegenerateConfig();
        if (await Api.IsAliveAsync())
        {
            await Api.SetModeAsync(routeMode);
        }
    }

    /// <summary>切换分流规则模式并重启内核（规则在配置生成时确定，需重启生效）。</summary>
    public void SetRuleMode(string mode)
    {
        if (mode is not ("standard" or "airport" or "minimal"))
        {
            throw new ArgumentException("未知分流模式", mode);
        }
        if (Settings.RuleMode == mode)
        {
            return;
        }
        Settings.RuleMode = mode;
        Settings.Save();
        RegenerateConfig();
        SwapApiAndRestartCore();
    }

    /// <summary>网络选项变更（allow-lan / ipv6 / dns）：重写配置并重启内核。</summary>
    public void ApplyNetworkOptions()
    {
        Settings.Save();
        RegenerateConfig();
        SwapApiAndRestartCore();
    }

    /// <summary>检测端口能否被内核绑定（Windows 的保留端口段会静默让内核放弃监听）。</summary>
    public static bool IsPortBindable(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>修改混合端口；端口被占用/被系统保留时抛出，且不改动当前设置。</summary>
    public void SetPort(int mixedPort)
    {
        if (mixedPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("端口必须在 1-65535 之间");
        }
        if (mixedPort == Settings.MixedPort)
        {
            return;
        }
        // 先占位检测：内核绑不上端口时会静默把 mixed-port 归零，表现为"代理没开但界面说开了"
        if (!IsPortBindable(mixedPort))
        {
            throw new InvalidOperationException(
                $"端口 {mixedPort} 在本机不可用（可能被其它程序占用，或被 Windows 保留）。请换一个端口，例如 7899 或 7890。");
        }

        Settings.MixedPort = mixedPort;
        Settings.Save();
        RegenerateConfig();
        SwapApiAndRestartCore();
        ApplySystemProxyState();
    }

    public void RegenerateConfig()
    {
        ConfigGenerator.Write(Settings, Profiles.Items, tunEnabled: Settings.TunnelMode == TunnelMode.Tun, Log);
    }

    /// <summary>内核（重新）下载订阅需要时间，分多次通知界面刷新节点。</summary>
    private void NotifyNodesChangedWithRetry()
    {
        foreach (var delay in new[] { 0, 2000, 5000, 9000 })
        {
            _ = Task.Run(async () =>
            {
                if (delay > 0)
                {
                    await Task.Delay(delay);
                }
                NodesChanged?.Invoke();
            });
        }
    }

    private readonly HashSet<string> _autoUpdating = new();   // 正在自动更新的订阅 Id（单飞）

    /// <summary>启动订阅自动更新调度循环：每 30 秒检查一次所有订阅的到期情况。</summary>
    private void StartAutoUpdateLoop()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await CheckAutoUpdatesAsync();
                }
                catch
                {
                    // 单轮失败不影响后续调度
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    /// <summary>按各订阅的自动更新模式判断是否到期并执行：固定间隔 / 每天定时 / Cron 表达式。</summary>
    private async Task CheckAutoUpdatesAsync()
    {
        var now = DateTime.Now;
        foreach (var p in Profiles.Snapshot())
        {
            if (_autoUpdating.Contains(p.Id) || p.LocalFile.Length > 0)
            {
                continue;   // 本地文件订阅没有远端可拉
            }
            var due = p.AutoUpdateMode switch
            {
                "interval" => (now - (p.LastAutoUpdate ?? p.AddedAt)).TotalMinutes >= Math.Max(5, p.AutoUpdateIntervalMinutes),
                "daily" => DateTime.TryParseExact(p.AutoUpdateTime, "HH\\:mm", null, System.Globalization.DateTimeStyles.None, out var t)
                           && now.TimeOfDay >= t.TimeOfDay
                           && (p.LastAutoUpdate?.Date ?? DateTime.MinValue.Date) < now.Date,
                "cron" => IsCronDue(p.AutoUpdateCron, p.LastAutoUpdate),
                _ => false,
            };
            if (!due)
            {
                continue;
            }
            _autoUpdating.Add(p.Id);
            _ = Task.Run(async () =>
            {
                try
                {
                    WriteLog($"[shadowrocket] 开始自动更新订阅「{p.Name}」…");
                    await UpdateProfileAsync(p);
                    p.LastAutoUpdate = DateTime.Now;
                    Profiles.Persist();
                    WriteLog($"[shadowrocket] 订阅「{p.Name}」已自动更新");
                }
                catch (Exception e)
                {
                    // 失败也推进时间戳：下个完整周期后再试，避免机场不可达时 30 秒一轮的重试风暴
                    p.LastAutoUpdate = DateTime.Now;
                    Profiles.Persist();
                    WriteLog($"[shadowrocket] 订阅「{p.Name}」自动更新失败：{e.Message}");
                }
                finally
                {
                    _autoUpdating.Remove(p.Id);
                }
            });
        }
    }

    /// <summary>Cron 到期判断：上次运行（或当前时刻）之后的下一次触发点已过即为到期。</summary>
    private static bool IsCronDue(string expression, DateTime? lastRun)
    {
        try
        {
            var expr = Cronos.CronExpression.Parse(expression.Trim());
            var from = (lastRun ?? DateTime.Now).ToUniversalTime();
            var next = expr.GetNextOccurrence(from, TimeZoneInfo.Local);
            return next.HasValue && next.Value <= DateTime.UtcNow;
        }
        catch
        {
            return false;   // 表达式无效或格式不支持：静默跳过
        }
    }

    // ---------- 订阅 ----------

    /// <summary>导入远程订阅：下载失败不落库；本地文件复制进 providers 目录（与 config 里声明的 path 一致）。</summary>
    public async Task<ProfileItem> AddProfileAsync(string name, string url, string? localFilePath)
    {
        Directory.CreateDirectory(Settings.ProfilesDir);
        Directory.CreateDirectory(Settings.ProvidersDir);
        var item = new ProfileItem { Name = name, Url = url };

        if (localFilePath is { Length: > 0 })
        {
            var text = SafeReadText(localFilePath);
            if (!LooksLikeSubscription(text))
            {
                throw new InvalidOperationException(
                    "这个文件看起来不是订阅：既没有 proxies: 段，也没有 ss:// / vmess:// 之类的分享链接。\n" +
                    "如果它是完整配置文件，请把其中的 proxies 段单独存成 YAML，或直接使用机场的订阅链接。");
            }

            var dest = Path.Combine(Settings.ProvidersDir, $"{item.Id}.yaml");
            File.WriteAllText(dest, text);
            item.LocalFile = dest;
            item.SourceFile = localFilePath;
            if (name.Length == 0)
            {
                item.Name = Path.GetFileNameWithoutExtension(localFilePath);
            }
        }
        else
        {
            var (text, status) = await DownloadSubscriptionAsync(url);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("订阅下载失败，请检查网络或链接");
            }
            text = NormalizeSubscription(text);
            if (!LooksLikeSubscription(text))
            {
                throw new InvalidOperationException(StatusHint(status, text));
            }
            // 自动匹配：sing-box 格式的订阅需要 sing-box 内核（Hiddify 同源），征得同意后切换
            if (ConfigGenerator.IsSingBoxFormat(text) && !Settings.IsSingBoxCore)
            {
                var confirmed = ConfirmCoreSwitch == null || await ConfirmCoreSwitch(
                    "检测到 sing-box 格式的订阅，需要把代理内核切换为 sing-box（当前为 mihomo）才能使用。\n\n" +
                    (Core.IsCoreInstalled ? "是否切换内核？" : "是否切换内核？切换后将自动下载 sing-box 内核。"));
                if (!confirmed)
                {
                    throw new InvalidOperationException("该订阅为 sing-box 格式，请在设置里把代理内核切换为 sing-box 后重新添加。");
                }
                Settings.CoreKind = "singbox";
                Settings.Save();
                if (!Core.IsCoreInstalled)
                {
                    try
                    {
                        Log?.Invoke("[shadowrocket] 正在下载 sing-box 内核…");
                        var rel = await Kernel.GetLatestSingBoxAsync();
                        if (rel is null)
                        {
                            throw new InvalidOperationException("没有找到 sing-box 的 Windows 版本");
                        }
                        await Kernel.InstallSingBoxAsync(rel, null, Core);
                    }
                    catch (Exception e)
                    {
                        Settings.CoreKind = "mihomo";
                        Settings.Save();
                        throw new InvalidOperationException($"sing-box 内核下载失败，已保持 mihomo 内核：{e.Message}");
                    }
                }
            }
            if (name.Length == 0)
            {
                item.Name = TryHostName(url);
            }
        }

        Profiles.Add(item);
        RegenerateConfig();
        RestartCoreInternal(elevated: Settings.TunnelMode == TunnelMode.Tun);
        NotifyNodesChangedWithRetry();

        // 本地文件是即时解析的，节点数能直接反映格式是否被内核接受
        if (item.LocalFile.Length > 0)
        {
            var count = await WaitForProviderNodesAsync(item.Id);
            if (count == 0)
            {
                Profiles.Remove(item.Id);
                RegenerateConfig();
                RestartCoreInternal(elevated: Settings.TunnelMode == TunnelMode.Tun);
                throw new InvalidOperationException("内核未能从该文件解析出任何节点，订阅已回滚");
            }
        }
        return item;
    }

    /// <summary>轮询等待内核解析出该 provider 的节点；返回节点数（超时返回最后一次结果）。</summary>
    private async Task<int> WaitForProviderNodesAsync(string providerId)
    {
        var last = 0;
        for (var i = 0; i < 10; i++)
        {
            if (await Core.WaitReadyAsync(TimeSpan.FromSeconds(3)))
            {
                var providers = await Api.GetProvidersAsync();
                last = providers.Values.Count(n => n.ProviderId == providerId);
                if (last > 0)
                {
                    return last;
                }
            }
            await Task.Delay(500);
        }
        return last;
    }

    private static string SafeReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"读取文件失败：{e.Message}");
        }
    }

    private static readonly string[] LinkSchemes = ["ss://", "ssr://", "vmess://", "vless://", "trojan://", "hysteria://", "hy2://", "tuic://", "socks://", "wireguard://"];

    /// <summary>粗略判断文本是否为订阅数据：Clash provider YAML、分享链接列表、base64 订阅或 sing-box JSON。</summary>
    private static bool LooksLikeSubscription(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        // sing-box 订阅是 JSON（outbounds 数组）。必须在这里识别，否则会在下面的格式校验
        // 被当乱码拒掉，永远走不到后续的 sing-box 内核检测（表现为机场返回 HTTP 200 却导入失败）
        if (ConfigGenerator.IsSingBoxFormat(text))
        {
            return true;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("proxies:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("proxy-providers:", StringComparison.OrdinalIgnoreCase) ||
                LinkSchemes.Any(s => line.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        // base64 订阅：首行是一长串无空白的 base64 字符
        var first = text.Split('\n').FirstOrDefault(x => x.Trim().Length > 0)?.Trim() ?? "";
        return first.Length >= 60 && !first.Contains(' ') && !first.Contains(':') &&
               first.All(c => char.IsLetterOrDigit(c) || c is '+' or '/' or '=' or '_');
    }

    public async Task UpdateProfileAsync(ProfileItem p)
    {
        if (p.LocalFile.Length == 0)
        {
            var (text, status) = await DownloadSubscriptionAsync(p.Url);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("订阅更新失败，请检查网络");
            }
            text = NormalizeSubscription(text);
            if (!LooksLikeSubscription(text))
            {
                throw new InvalidOperationException(StatusHint(status, text));
            }
            // 拉到了新内容，写入缓存供内核使用
            Directory.CreateDirectory(Settings.ProvidersDir);
            File.WriteAllText(Path.Combine(Settings.ProvidersDir, $"{p.Id}.yaml"), text);
        }
        else
        {
            // 本地订阅：用户可能改过原文件，更新 = 重新读取一份到 providers 目录
            if (p.SourceFile.Length > 0 && File.Exists(p.SourceFile))
            {
                var text = SafeReadText(p.SourceFile);
                if (!LooksLikeSubscription(text))
                {
                    throw new InvalidOperationException("原文件已不再是订阅格式");
                }
                Directory.CreateDirectory(Settings.ProvidersDir);
                File.WriteAllText(p.LocalFile, text);
            }
            else if (!File.Exists(p.LocalFile))
            {
                throw new InvalidOperationException($"本地订阅文件已丢失：{(p.SourceFile.Length > 0 ? p.SourceFile : p.LocalFile)}");
            }
        }
        await Api.UpdateProviderAsync(p.Id);
        Profiles.MarkUpdated(p.Id);
        NotifyNodesChangedWithRetry();
    }

    public void DeleteProfile(string id)
    {
        Profiles.Remove(id);
        RegenerateConfig();
        RestartCoreInternal(elevated: Settings.TunnelMode == TunnelMode.Tun);
        NotifyNodesChangedWithRetry();
    }

    /// <summary>
    /// 订阅下载，按可用网络路径逐级尝试（每段 12 秒超时）：
    /// 系统代理（与浏览器同路径，机场走 Cloudflare 时常用）→ 直连 → 本机混合端口。
    /// 返回 (内容, HTTP状态码)。
    /// </summary>
    private async Task<(string? Text, int? Status)> DownloadSubscriptionAsync(string url)
    {
        (string?, int?) TryFetch(bool useSystemProxy, System.Net.IWebProxy? proxy)
        {
            try
            {
                using var handler = new HttpClientHandler();
                if (proxy != null)
                {
                    handler.Proxy = proxy;
                    handler.UseProxy = true;
                }
                else
                {
                    handler.UseProxy = useSystemProxy;   // false = 强制直连，true = 跟随系统代理
                }
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
                // 与内核拉取时用同一个 UA：机场可能按 UA 下发不同节点列表
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    string.IsNullOrWhiteSpace(Settings.SubscriptionUserAgent)
                        ? "clash-verge/1.0"
                        : Settings.SubscriptionUserAgent);
                using var resp = client.GetAsync(url).GetAwaiter().GetResult();
                var status = (int)resp.StatusCode;
                var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return (text.Length > 0 ? text : null, status);
            }
            catch
            {
                return (null, null);
            }
        }

        // 1) 系统代理  2) 直连  3) 自己的混合端口
        // TryFetch 内部是同步 I/O，必须丢进线程池执行，否则会阻塞调用方（UI 线程）直到超时
        var (text, status) = await Task.Run(() => TryFetch(useSystemProxy: true, proxy: null));
        if (text != null)
        {
            return (text, status);
        }
        (text, status) = await Task.Run(() => TryFetch(useSystemProxy: false, proxy: null));
        if (text != null)
        {
            return (text, status);
        }
        var (viaSelf, status3) = await Task.Run(() => TryFetch(useSystemProxy: false, proxy: new System.Net.WebProxy($"127.0.0.1:{Settings.MixedPort}")));
        return (viaSelf ?? "", status3 ?? status);
    }

    /// <summary>把 HTTP 状态码/网页正文翻译成用户能行动的提示，未识别的正文给出预览便于自查。</summary>
    private static string StatusHint(int? status, string body)
    {
        var code = status ?? 0;
        if (code is 401 or 403)
        {
            return $"机场返回 {code}：订阅链接可能已被重置或访问受限。" +
                   "请到机场官网重新复制订阅链接，然后在「配置 → 订阅详情」里粘贴保存。";
        }
        if (code >= 500)
        {
            return $"机场返回 {code}：机场服务暂时不可用，请稍后重试。";
        }
        if (IsHtmlBody(body))
        {
            var challenge = body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                            body.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
                            body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
                            body.Contains("请开启JavaScript", StringComparison.OrdinalIgnoreCase) ||
                            body.Contains("访问验证", StringComparison.OrdinalIgnoreCase);
            var why = challenge
                ? "这是 Cloudflare 人机验证页：浏览器能打开但程序拿不到数据，请稍后重试或换一个网络环境。"
                : "请确认链接是否正确，或用浏览器打开该链接看看机场是否返回了公告/登录页。";
            return $"机场返回了网页而不是订阅数据（HTTP {code}）。{why}\n内容预览：{Preview(body)}";
        }
        return $"订阅内容无法识别（HTTP {code}）：链接返回的不是订阅数据。\n内容预览：{Preview(body)}";
    }

    /// <summary>识别网页正文（机场公告页、Cloudflare 验证页、登录页等）。</summary>
    private static bool IsHtmlBody(string body) =>
        body.TrimStart().StartsWith('<') ||
        body.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("<!doctype", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 订阅内容规范化：机场有时下发 base64 包裹的 YAML/链接/JSON 订阅，
    /// 能解出已知订阅格式就返回解码结果，否则原文返回（校验会给出带预览的错误提示）。
    /// </summary>
    private static string NormalizeSubscription(string text)
    {
        var trimmed = text.Trim();
        if (LooksLikeSubscription(trimmed) || ConfigGenerator.IsSingBoxFormat(trimmed))
        {
            return text;
        }
        // base64（含 URL 安全变体 -_，去空白，补 = 填充）
        var compact = trimmed.Replace("\r", "").Replace("\n", "").Replace(" ", "")
                             .Replace('-', '+').Replace('_', '/');
        var padding = compact.Length % 4 == 0 ? "" : new string('=', 4 - compact.Length % 4);
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(compact + padding));
            if (LooksLikeSubscription(decoded) || ConfigGenerator.IsSingBoxFormat(decoded))
            {
                return decoded;
            }
        }
        catch
        {
        }
        return text;
    }

    /// <summary>压成单行并截断，用于错误提示里的正文预览。</summary>
    private static string Preview(string body)
    {
        var oneLine = System.Text.RegularExpressions.Regex.Replace(body.Trim(), @"\s+", " ");
        return oneLine.Length > 90 ? oneLine[..90] + "…" : oneLine;
    }

    private static string TryHostName(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return "订阅";
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _streams.Dispose();
        try { History.Flush(); } catch { }
        // 退出前恢复系统代理：只清掉指向本应用端口的代理（防退出后断网），
        // 不动用户自己在 Windows 里设置的其他代理。任何模式都检查，TUN 下遗留的代理同样会被清掉
        try
        {
            if (SystemProxyService.IsSet(Settings.MixedPort))
            {
                SystemProxyService.Disable(Settings, Settings.MixedPort);
            }
        }
        catch { }
        Core.Stop();
        Api.Dispose();
    }
}
