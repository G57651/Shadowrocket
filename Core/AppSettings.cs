using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shadowrocket.Core;

public class AppSettings
{
    public int MixedPort { get; set; } = 7899;
    public int ControllerPort { get; set; } = 9097;
    public string Mode { get; set; } = "SystemProxy";      // Off / SystemProxy / Tun
    public string RouteMode { get; set; } = "rule";        // rule / global / direct
    public string Theme { get; set; } = "light";           // light / dark
    public bool AutoStart { get; set; }
    public bool CloseToTray { get; set; }
    public string LatencyTestUrl { get; set; } = "https://www.gstatic.com/generate_204";
    public int LatencyTimeoutMs { get; set; } = 5000;
    public string CoreChannel { get; set; } = "stable";    // stable / alpha
    public string Secret { get; set; } = Guid.NewGuid().ToString("N")[..16];
    public string? CorePathOverride { get; set; }
    public bool AllowLan { get; set; }
    public bool Ipv6 { get; set; }
    public bool DnsFakeIp { get; set; } = true;

    /// <summary>
    /// 代理内核："mihomo"（默认，Clash 生态）或 "singbox"（sing-box，Hiddify 同源内核）。
    /// </summary>
    public string CoreKind { get; set; } = "mihomo";

    [JsonIgnore]
    public bool IsSingBoxCore => CoreKind == "singbox";

    /// <summary>首页节点网格的列数（1-6，默认 2）。</summary>
    public int NodeColumns { get; set; } = 2;

    /// <summary>自动测速：按间隔对全部节点跑一轮真实下载测速（默认关闭）。</summary>
    public bool AutoSpeedTest { get; set; }

    /// <summary>自动测速间隔单位："minute"（默认）或 "second"。</summary>
    public string AutoSpeedTestUnit { get; set; } = "minute";

    /// <summary>自动测速间隔（秒，30-86400，默认 1800 = 30 分钟）。</summary>
    public int AutoSpeedTestIntervalSeconds { get; set; } = 1800;

    /// <summary>自动测延迟：按间隔经内核分组延迟接口测全组节点延迟（默认关闭）。</summary>
    public bool AutoLatencyTest { get; set; }

    /// <summary>自动测延迟间隔单位："minute"（默认）或 "second"。</summary>
    public string AutoLatencyTestUnit { get; set; } = "minute";

    /// <summary>自动测延迟间隔（秒，30-86400，默认 300 = 5 分钟）。</summary>
    public int AutoLatencyTestIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// 首页节点排序："default" 默认（已测速优先，未测按延迟）、"name" 按名称、
    /// "latency" 按延迟升序、"speed" 按速度降序。
    /// </summary>
    public string NodeSortMode { get; set; } = "default";

    /// <summary>实时速率刷新间隔（秒，预设 1/5/10/30）：只影响首页/数据页 ↑↓ 数字与图表的刷新节奏，
    /// 内核每秒推送原始速率，界面按此间隔聚合平均后再显示；不触发任何节点测试。</summary>
    public int SpeedIntervalSeconds { get; set; } = 1;

    /// <summary>已做过一次性"一键导入默认打开方式"重置（清 UserChoice，让 Windows 重新弹应用选择框）。</summary>
    public bool SchemeChoiceResetDone { get; set; }

    /// <summary>一键导入接收方记忆：scheme 名 → "self"（导入本软件）或 "pass"（转发给系统真实客户端）。
    /// 空缺 = 每次点击导入链接时弹窗询问。</summary>
    public Dictionary<string, string> ImportHandoff { get; set; } = new();

    /// <summary>
    /// 分流规则："standard" 智能分流（第三方规则集，国内直连+广告拦截，默认）、
    /// "airport" 使用机场订阅自带规则、"minimal" 全部走代理（旧行为）。
    /// </summary>
    public string RuleMode { get; set; } = "standard";

    /// <summary>拉取订阅时使用的 User-Agent。机场常按 UA 下发不同配置（实测同一链接
    /// clash-verge 拿到 44 个节点、Clash/v1.18 只有 21 个），留空则用内核默认 UA。</summary>
    public string SubscriptionUserAgent { get; set; } = "clash-verge/v2.3.1";

    /// <summary>开启系统代理前的原始设置快照（退出/崩溃/切换失败时还原用）。
    /// 三值全为 null = 无快照或已还原。落盘保证异常退出后下次启动仍能还原。</summary>
    public int? PreviousProxyEnable { get; set; }
    public string? PreviousProxyServer { get; set; }
    public string? PreviousProxyOverride { get; set; }

    [JsonIgnore]
    public TunnelMode TunnelMode => Enum.TryParse<TunnelMode>(Mode, out var m) ? m : TunnelMode.Off;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shadowrocket");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>加载期发现的问题（主文件损坏等）。非 null 时调用方必须向用户显示告警，不得静默。</summary>
    [JsonIgnore]
    public string? LoadIssue { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// 全进程单次加载：首次调用完成"读取/损坏恢复/回写"，之后一律返回同一实例。
    /// 修复审计缺陷：启动早期（RegisterUrlSchemes）与 AppRuntime 会各调一次 Load，
    /// 若每次都走磁盘，损坏恢复的结果会在第二次调用时丢失（主文件已被改名 → 静默回退默认值、告警丢失）。
    /// </summary>
    private static AppSettings? _loaded;

    public static AppSettings Load()
    {
        if (_loaded != null)
        {
            return _loaded;
        }

        if (File.Exists(FilePath))
        {
            try
            {
                _loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                          ?? throw new JsonException("反序列化结果为 null");
                return _loaded;
            }
            catch (System.Exception e)
            {
                // 主文件损坏：不静默丢弃。原文件改名存档，尝试从 .bak 恢复，并把问题上报给调用方弹窗提示
                var corrupt = FilePath + ".corrupt-" + System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(FilePath, corrupt); } catch { corrupt = FilePath; }

                var bak = FilePath + ".bak";
                if (File.Exists(bak))
                {
                    try
                    {
                        var recovered = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(bak));
                        if (recovered != null)
                        {
                            recovered.LoadIssue =
                                $"设置文件已损坏（{e.Message}），已从备份 {bak} 恢复。损坏原件保留在：{corrupt}";
                            // 恢复结果立即回写主文件，防止任何后续调用/重启再次落到默认值。
                            // 此时主文件不存在，AtomicFile 走 Move 分支，不会触碰 .bak（绝不用默认值覆盖备份）
                            try { AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(recovered, JsonOpts)); } catch { }
                            _loaded = recovered;
                            return _loaded;
                        }
                    }
                    catch
                    {
                        // 备份也不可用，落到默认值分支
                    }
                }

                var defaults = new AppSettings
                {
                    LoadIssue =
                        $"设置文件已损坏且无可用备份（{e.Message}），已按默认值重建。损坏原件保留在：{corrupt}",
                };
                // 默认值同样回写主文件（Move 分支，不覆盖 .bak 残件），保证单次加载语义与后续 Save 的 Replace 链正常
                try { AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, JsonOpts)); } catch { }
                _loaded = defaults;
                return _loaded;
            }
        }

        // 主文件不存在：防御"主文件被删但 .bak 仍在"（例如上次损坏改名后回写失败）
        var orphanBak = FilePath + ".bak";
        if (File.Exists(orphanBak))
        {
            try
            {
                var recovered = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(orphanBak));
                if (recovered != null)
                {
                    recovered.LoadIssue = $"设置主文件缺失，已从备份 {orphanBak} 恢复。";
                    try { AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(recovered, JsonOpts)); } catch { }
                    _loaded = recovered;
                    return _loaded;
                }
            }
            catch
            {
                // 备份不可用，落到全新默认值
            }
        }

        _loaded = new AppSettings();
        return _loaded;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string CoreExePath => CorePathOverride is { Length: > 0 } p && File.Exists(p)
        ? p
        : Path.Combine(Dir, "core", IsSingBoxCore ? "sing-box.exe" : "mihomo.exe");

    /// <summary>
    /// 内核可执行文件信任校验：只允许本软件 core 目录下的内核，
    /// 或用户显式设置的 CorePathOverride。防止 settings.json 被篡改后启动任意二进制。
    /// </summary>
    public bool IsTrustedCoreExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe))
        {
            return false;
        }
        try
        {
            var full = Path.GetFullPath(exe);
            if (CorePathOverride is { Length: > 0 } o
                && string.Equals(Path.GetFullPath(o), full, StringComparison.OrdinalIgnoreCase))
            {
                return true;   // 用户显式指定的内核路径
            }
            var coreDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Dir, "core")));
            return full.StartsWith(coreDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetDirectoryName(full), coreDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public string MihomoExePath => Path.Combine(Dir, "core", "mihomo.exe");
    public string SingBoxExePath => Path.Combine(Dir, "core", "sing-box.exe");
    public string SingBoxConfigPath => Path.Combine(Dir, "sing-box.json");
    public string SingBoxVersionFilePath => Path.Combine(Dir, "core", "singbox-version.txt");

    public string HomeDir => Dir;                       // mihomo -d 工作目录
    public string ConfigPath => Path.Combine(Dir, "config.yaml");
    public string ProfilesDir => Path.Combine(Dir, "profiles");
    public string ProvidersDir => Path.Combine(Dir, "providers");   // 内核 proxy-providers 落盘目录
    public string VersionFilePath => Path.Combine(Dir, "core", "version.txt");
}

public enum TunnelMode
{
    Off,
    SystemProxy,
    Tun,
}
