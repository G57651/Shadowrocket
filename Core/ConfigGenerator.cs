using System.IO;
using System.Text;

namespace Shadowrocket.Core;

/// <summary>
/// 生成 mihomo config.yaml：订阅以 proxy-providers 交给内核解析与下载，
/// GUI 不解析节点格式（无论机场是 Clash YAML 还是 base64 分享链接，内核都认）。
/// </summary>
public static class ConfigGenerator
{
    public static void Write(AppSettings s, IReadOnlyList<ProfileItem> profiles, bool tunEnabled, Action<string>? log = null)
    {
        Directory.CreateDirectory(AppSettings.Dir);
        Directory.CreateDirectory(s.ProvidersDir);
        Directory.CreateDirectory(Path.Combine(AppSettings.Dir, "rules"));   // mihomo rule-providers 落盘目录

        if (s.IsSingBoxCore)
        {
            WriteSingBox(s, profiles, tunEnabled, log);
            return;
        }
        WriteMihomo(s, profiles, tunEnabled, log);
    }

    private static void WriteSingBox(AppSettings s, IReadOnlyList<ProfileItem> profiles, bool tunEnabled, Action<string>? log)
    {
        var json = SingBoxConfigBuilder.Build(s, profiles, tunEnabled, log);
        File.WriteAllText(s.SingBoxConfigPath, json);
    }

    private static void WriteMihomo(AppSettings s, IReadOnlyList<ProfileItem> profiles, bool tunEnabled, Action<string>? log)
    {

        // 本地订阅的文件被用户删掉时跳过它：宁可少一个订阅，也不能让整份 config 失效
        var usable = new List<ProfileItem>();
        foreach (var p in profiles)
        {
            if (!string.IsNullOrEmpty(p.LocalFile) && !File.Exists(p.LocalFile))
            {
                log?.Invoke($"[shadowrocket] 订阅「{p.Name}」的本地文件不存在，已跳过：{p.LocalFile}");
                continue;
            }
            // mihomo 内核解析不了 sing-box 格式的订阅，跳过并提示
            var cacheText = p.LocalFile.Length > 0
                ? SafeRead(p.LocalFile)
                : SafeRead(Path.Combine(s.ProvidersDir, $"{p.Id}.yaml"));
            if (cacheText != null && IsSingBoxFormat(cacheText))
            {
                log?.Invoke($"[shadowrocket] 订阅「{p.Name}」是 sing-box 格式，请在设置里切换到 sing-box 内核使用");
                continue;
            }
            usable.Add(p);
        }

        var sb = new StringBuilder();
        sb.AppendLine("# 由 Shadowrocket 生成，请勿手工修改");
        sb.AppendLine($"mixed-port: {s.MixedPort}");
        sb.AppendLine($"allow-lan: {s.AllowLan.ToString().ToLowerInvariant()}");
        sb.AppendLine($"ipv6: {s.Ipv6.ToString().ToLowerInvariant()}");
        sb.AppendLine($"mode: {s.RouteMode}");
        sb.AppendLine("log-level: info");
        sb.AppendLine($"external-controller: 127.0.0.1:{s.ControllerPort}");
        sb.AppendLine($"secret: {YamlStr(s.Secret)}");
        sb.AppendLine("profile:");
        sb.AppendLine("  store-selected: true");
        // 延迟统计取"整条链路"耗时，避免测速结果明显偏低
        sb.AppendLine("unified-delay: true");

        // 机场规则常含 GEOIP，内核需要 geo 数据；默认源是 GitHub，这里换国内可达镜像
        if (s.RuleMode == "airport")
        {
            sb.AppendLine("geox-url:");
            sb.AppendLine($"  geoip: {YamlStr("https://testingcf.jsdelivr.net/gh/MetaCubeX/meta-rules-dat@release/geoip.metadb")}");
            sb.AppendLine($"  geosite: {YamlStr("https://testingcf.jsdelivr.net/gh/MetaCubeX/meta-rules-dat@release/geosite.dat")}");
            sb.AppendLine($"  mmdb: {YamlStr("https://testingcf.jsdelivr.net/gh/MetaCubeX/meta-rules-dat@release/country.mmdb")}");
        }

        var needDns = tunEnabled || s.DnsFakeIp;
        if (needDns)
        {
            sb.AppendLine("dns:");
            sb.AppendLine("  enable: true");
            sb.AppendLine(s.DnsFakeIp || tunEnabled
                ? "  enhanced-mode: fake-ip"
                : "  enhanced-mode: redir-host");
            sb.AppendLine("  fake-ip-range: 198.18.0.1/16");
            sb.AppendLine("  fake-ip-filter:");
            sb.AppendLine("    - \"*.msftconnecttest.com\"");
            sb.AppendLine("    - \"*.msftncsi.com\"");
            sb.AppendLine("  nameserver:");
            sb.AppendLine("    - 223.5.5.5");
            sb.AppendLine("    - 119.29.29.29");
        }

        if (tunEnabled)
        {
            sb.AppendLine("tun:");
            sb.AppendLine("  enable: true");
            sb.AppendLine("  stack: gvisor");
            sb.AppendLine("  auto-route: true");
            sb.AppendLine("  auto-detect-interface: true");
            sb.AppendLine("  dns-hijack:");
            sb.AppendLine("    - any:53");
        }

        if (usable.Count > 0)
        {
            sb.AppendLine("proxy-providers:");
            foreach (var p in usable)
            {
                sb.AppendLine($"  {p.Id}:");
                if (string.IsNullOrEmpty(p.LocalFile))
                {
                    sb.AppendLine("    type: http");
                    sb.AppendLine($"    url: {YamlStr(p.Url)}");
                    sb.AppendLine("    interval: 86400");
                    // 机场按 UA 下发不同节点列表；带上与 Clash Verge 一致的 UA 才能拿到完整节点
                    if (!string.IsNullOrWhiteSpace(s.SubscriptionUserAgent))
                    {
                        sb.AppendLine("    header:");
                        sb.AppendLine($"      User-Agent: [{YamlStr(s.SubscriptionUserAgent)}]");
                    }
                }
                else
                {
                    // 文件已在 AddProfileAsync 复制到 ProvidersDir，path 必须与实际位置一致。
                    // 注意不能再写第二个 path 键：mihomo 严格 YAML 解析会报
                    // "mapping key \"path\" already defined"，导致整个配置加载失败、内核起不来。
                    sb.AppendLine("    type: file");
                }
                sb.AppendLine($"    path: ./providers/{p.Id}.yaml");
                sb.AppendLine("    health-check:");
                sb.AppendLine("      enable: true");
                // lazy：启动时不逐个打节点，交给用户点"全部测速"，避免导入即压满线路
                sb.AppendLine("      lazy: true");
                sb.AppendLine($"      url: {YamlStr(s.LatencyTestUrl)}");
                sb.AppendLine("      interval: 600");
            }
        }

        sb.AppendLine("proxy-groups:");
        sb.AppendLine("  - name: PROXY");
        sb.AppendLine("    type: select");
        if (usable.Count > 0)
        {
            sb.AppendLine("    proxies:");
            sb.AppendLine("      - AUTO");
            sb.AppendLine("      - DIRECT");
            sb.AppendLine("    use:");
            foreach (var p in usable)
            {
                sb.AppendLine($"      - {p.Id}");
            }
            sb.AppendLine("  - name: AUTO");
            sb.AppendLine("    type: url-test");
            sb.AppendLine($"    url: {YamlStr(s.LatencyTestUrl)}");
            sb.AppendLine("    interval: 600");
            sb.AppendLine("    lazy: true");
            sb.AppendLine("    use:");
            foreach (var p in usable)
            {
                sb.AppendLine($"      - {p.Id}");
            }
        }
        else
        {
            sb.AppendLine("    proxies:");
            sb.AppendLine("      - DIRECT");
        }

        AppendRuleSection(sb, s, usable, log);

        File.WriteAllText(s.ConfigPath, sb.ToString());
    }

    /// <summary>
    /// 分流规则三模式：standard = 第三方规则集（jsDelivr 国内镜像，国内直连+广告拦截）；
    /// airport = 提取订阅自带的 rules 并映射策略组；minimal = 全部走代理。
    /// </summary>
    private static void AppendRuleSection(StringBuilder sb, AppSettings s, List<ProfileItem> usable, Action<string>? log)
    {
        var mode = s.RuleMode switch
        {
            "airport" => "airport",
            "minimal" => "minimal",
            _ => "standard",
        };

        if (mode == "airport")
        {
            var airportRules = ExtractAirportRules(s, usable, log);
            if (airportRules.Count > 0)
            {
                log?.Invoke($"[shadowrocket] 使用机场自带规则：{airportRules.Count} 条（策略已映射到本软件策略组）");
                sb.AppendLine("rules:");
                foreach (var r in airportRules)
                {
                    sb.AppendLine($"  - {YamlStr(r)}");
                }
                return;
            }
            log?.Invoke("[shadowrocket] 订阅里没有可用的 rules 段，回退到智能分流");
        }

        if (mode == "airport" || mode == "standard")
        {
            // 智能分流：Loyalsoldier 规则集（经 jsDelivr 国内镜像，内核按 interval 自动更新）
            sb.AppendLine("rule-providers:");
            void Provider(string tag, string behavior, string file)
            {
                sb.AppendLine($"  {tag}:");
                sb.AppendLine("    type: http");
                sb.AppendLine("    behavior: " + behavior);
                sb.AppendLine($"    url: {YamlStr($"https://testingcf.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/{file}")}");
                sb.AppendLine($"    path: ./rules/{file}");
                sb.AppendLine("    interval: 86400");
            }
            Provider("reject", "domain", "reject.txt");
            Provider("private", "domain", "private.txt");
            Provider("direct", "domain", "direct.txt");
            Provider("cncidr", "ipcidr", "cncidr.txt");

            sb.AppendLine("rules:");
            sb.AppendLine("  - RULE-SET,reject,REJECT");
            sb.AppendLine("  - DOMAIN-SUFFIX,local,DIRECT");
            sb.AppendLine("  - RULE-SET,private,DIRECT");
            sb.AppendLine("  - RULE-SET,direct,DIRECT");
            sb.AppendLine("  - IP-CIDR,127.0.0.0/8,DIRECT,no-resolve");
            sb.AppendLine("  - IP-CIDR,10.0.0.0/8,DIRECT,no-resolve");
            sb.AppendLine("  - IP-CIDR,172.16.0.0/12,DIRECT,no-resolve");
            sb.AppendLine("  - IP-CIDR,192.168.0.0/16,DIRECT,no-resolve");
            sb.AppendLine("  - IP-CIDR,169.254.0.0/16,DIRECT,no-resolve");
            sb.AppendLine("  - RULE-SET,cncidr,DIRECT");
            sb.AppendLine("  - MATCH,PROXY");
            return;
        }

        // minimal：全部走代理（旧行为）
        sb.AppendLine("rules:");
        sb.AppendLine("  - DOMAIN-SUFFIX,local,DIRECT");
        sb.AppendLine("  - IP-CIDR,127.0.0.0/8,DIRECT,no-resolve");
        sb.AppendLine("  - IP-CIDR,10.0.0.0/8,DIRECT,no-resolve");
        sb.AppendLine("  - IP-CIDR,172.16.0.0/12,DIRECT,no-resolve");
        sb.AppendLine("  - IP-CIDR,192.168.0.0/16,DIRECT,no-resolve");
        sb.AppendLine("  - IP-CIDR,169.254.0.0/16,DIRECT,no-resolve");
        sb.AppendLine("  - MATCH,PROXY");
    }

    /// <summary>
    /// 从订阅缓存里提取 rules 段，并把策略目标映射到本软件的策略组：
    /// 直连类 → DIRECT，广告/拒绝类 → REJECT，其余（节点组/自动选择等）→ PROXY。
    /// RULE-SET 引用机场自己的规则包，本软件没有对应文件，跳过。
    /// </summary>
    private static List<string> ExtractAirportRules(AppSettings s, List<ProfileItem> usable, Action<string>? log)
    {
        var rules = new List<string>();
        foreach (var p in usable)
        {
            var text = p.LocalFile.Length > 0 ? SafeRead(p.LocalFile) : SafeRead(Path.Combine(s.ProvidersDir, $"{p.Id}.yaml"));
            if (text == null)
            {
                continue;
            }
            try
            {
                var doc = new YamlDotNet.Serialization.DeserializerBuilder().Build()
                    .Deserialize<Dictionary<object, object>>(text);
                if (doc?.TryGetValue("rules", out var raw) != true || raw is not IEnumerable<object> list)
                {
                    continue;
                }
                foreach (var item in list)
                {
                    var line = item?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }
                    var parts = line.Split(',');
                    var type = parts[0].Trim().ToUpperInvariant();
                    if (type == "MATCH" || type == "RULE-SET")
                    {
                        continue;   // MATCH 由本软件收尾；RULE-SET 缺少对应规则包
                    }
                    var target = parts[^1].Trim();
                    var mapped = target.ToLowerInvariant() switch
                    {
                        var t when t.Contains("direct") || t.Contains("直连") => "DIRECT",
                        var t when t.Contains("reject") || t.Contains("广告") || t.Contains("拒") => "REJECT",
                        _ => "PROXY",
                    };
                    var rule = parts.Length >= 3
                        ? $"{type},{string.Join(',', parts[1..^1]).Trim()},{mapped}"
                        : $"{type},{mapped}";
                    if (!rules.Contains(rule))
                    {
                        rules.Add(rule);
                    }
                }
                if (rules.Count > 0)
                {
                    return rules;
                }
            }
            catch (Exception e)
            {
                log?.Invoke($"[shadowrocket] 订阅「{p.Name}」的规则段解析失败：{e.Message}");
            }
        }
        return rules;
    }

    /// <summary>YAML 双引号标量：Windows 路径的反斜杠与内嵌引号都必须转义。</summary>
    private static string YamlStr(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? SafeRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>判断文本是否为 sing-box 格式配置（JSON 且含 outbounds 数组）。</summary>
    public static bool IsSingBoxFormat(string text)
    {
        var t = text.TrimStart();
        if (!t.StartsWith('{')) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("outbounds", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }
}
