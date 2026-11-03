using System.IO;
using System.Text.Json.Nodes;

namespace Shadowrocket.Core;

/// <summary>
/// sing-box 配置生成：把订阅（Clash YAML 或 sing-box JSON）转成 sing-box outbounds，
/// 并生成带 clash_api 的完整配置 —— 界面层的 REST/WS 全部走 clash_api，无需改造。
/// 字段映射以 sing-box 1.14 文档为准。
/// </summary>
public static class SingBoxConfigBuilder
{
    public static string Build(AppSettings s, IReadOnlyList<ProfileItem> profiles, bool tunEnabled, Action<string>? log)
    {
        var nodes = new JsonArray();
        var nodeTags = new List<string>();

        foreach (var p in profiles)
        {
            if (!string.IsNullOrEmpty(p.LocalFile) && !File.Exists(p.LocalFile))
            {
                log?.Invoke($"[shadowrocket] 订阅「{p.Name}」的本地文件不存在，已跳过：{p.LocalFile}");
                continue;
            }
            var text = p.LocalFile.Length > 0
                ? File.ReadAllText(p.LocalFile)
                : CacheText(s, p);
            if (string.IsNullOrWhiteSpace(text))
            {
                log?.Invoke($"[shadowrocket] 订阅「{p.Name}」暂无缓存内容，跳过（点「更新」拉取后生效）");
                continue;
            }

            foreach (var node in ParseNodes(text, p, log))
            {
                var tag = node["tag"]!.GetValue<string>();
                if (nodeTags.Contains(tag))
                {
                    continue;   // 多订阅之间重名的节点只保留第一个
                }
                nodes.Add(node);
                nodeTags.Add(tag);
            }
        }

        // TUN 模式需要完整 DNS 劫持链：dns-out 出站 + hijack 规则 + dns 服务器组
        var extraOutbounds = new List<JsonObject>();
        if (s.RuleMode == "airport")
        {
            log?.Invoke("[shadowrocket] sing-box 暂不支持机场自带规则提取，已按智能分流（standard）规则生成");
        }
        // RouteMode / RuleMode 均在 route 生成时接线（此前未接线：任何模式切换都生成同样的配置）
        var route = BuildRoute(s, tunEnabled);
        JsonObject? dns = null;
        if (tunEnabled)
        {
            extraOutbounds.Add(new JsonObject { ["type"] = "dns", ["tag"] = "dns-out" });
            dns = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["tag"] = "proxy-dns", ["address"] = "https://doh.pub/dns-query", ["detour"] = "PROXY" },
                    new JsonObject { ["tag"] = "local-dns", ["address"] = "223.5.5.5", ["detour"] = "DIRECT" },
                },
                ["rules"] = new JsonArray(new JsonObject { ["outbound"] = "any", ["server"] = "local-dns" }),
                ["final"] = "proxy-dns",
            };
        }

        // 策略组与内置出站排在节点前（clash_api 的组顺序即界面显示顺序）。
        // 零节点时 PROXY 只引用恒存在的 DIRECT，避免悬空引用未创建的 AUTO
        var proxyMembers = nodeTags.Count > 0
            ? new List<string> { "AUTO", "DIRECT" }.Concat(nodeTags).ToList()
            : new List<string> { "DIRECT" };
        var outbounds = new JsonArray
        {
            Group("selector", "PROXY", proxyMembers),
        };
        if (nodeTags.Count > 0)
        {
            outbounds.Add(Group("urltest", "AUTO", nodeTags, url: s.LatencyTestUrl));
        }
        outbounds.Add(Outbound("direct", "DIRECT"));
        outbounds.Add(Outbound("block", "BLOCK"));   // 广告拦截规则的目标出站，必须显式声明，否则 route 引用会校验失败
        foreach (var extra in extraOutbounds) outbounds.Add(extra);
        foreach (var n in nodes)
        {
            if (n == null)
            {
                continue;
            }
            outbounds.Add(n.DeepClone());
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true },
            ["inbounds"] = new JsonArray(Inbound("mixed", "mixed-in", s)),
            ["outbounds"] = outbounds,
            ["route"] = route,
            // clash_api：界面层的节点列表/测速/连接/日志全部复用这套 REST+WS
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{s.ControllerPort}",
                    ["secret"] = s.Secret,
                    // 跟随 RouteMode（此前写死 Rule：global/direct 重启后 clash_api 初始仍停留在 Rule）。
                    // 注：运行时经 PATCH /configs 切换 mode 对 sing-box 是否生效未验证，配置生成为主路径
                    ["default_mode"] = s.RouteMode switch { "global" => "Global", "direct" => "Direct", _ => "Rule" },
                },
                ["cache_file"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["store_fakeip"] = false,
                    // 与 mihomo 的 cache.db 分开：两者格式不同且不能同时持有文件锁
                    ["path"] = "singbox-cache.db",
                },
            },
        };
        if (dns != null)
        {
            root["dns"] = dns;
        }
        if (tunEnabled)
        {
            root["inbounds"]!.AsArray().Insert(0, Inbound("tun", "tun-in", s));
        }

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Inbound(string type, string tag, AppSettings s) => type switch
    {
        "tun" => new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = tag,
            ["address"] = new JsonArray("172.19.0.1/30"),
            ["mtu"] = 1500,
            ["auto_route"] = true,
            ["strict_route"] = false,
            ["stack"] = "gvisor",
        },
        _ => new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = tag,
            ["listen"] = s.AllowLan ? "0.0.0.0" : "127.0.0.1",
            ["listen_port"] = s.MixedPort,
        },
    };

    /// <summary>
    /// 生成 route。
    /// RouteMode（rule/global/direct）由 clash_mode 快捷规则对 + default_mode 承接：
    /// 依据 sing-box v1.14.4 源码（route/rule/rule_item_clash_mode.go 用 strings.EqualFold 匹配、
    /// clashmode.Manager.SetMode 大小写归一），模式匹配不区分大小写；default_mode 只决定初始模式，
    /// 若不写 clash_mode 规则，路由不会随模式改变——因此快捷规则对是模式生效的必要条件，
    /// 且运行时经 PATCH /configs 切换 mode 也能即时生效（无需重启）。
    /// RuleMode：standard/airport=智能分流（第三方规则集；airport 自带规则暂不支持提取，
    /// 调用方已在 UI 提示按 standard 降级）；minimal=仅保留私网直连，其余全走代理（与 mihomo 侧对齐）。
    /// </summary>
    private static JsonObject BuildRoute(AppSettings s, bool tunEnabled)
    {
        var rules = new JsonArray();
        if (tunEnabled)
        {
            // TUN 的 DNS 劫持链任何模式下都必须保留，否则 DNS 解析断链
            rules.Add(new JsonObject { ["inbound"] = new JsonArray("tun-in"), ["action"] = "hijack-dns" });
            rules.Add(new JsonObject { ["protocol"] = "dns", ["outbound"] = "dns-out" });
        }

        // clash_mode 快捷规则：global/direct 生效时短路全部流量（置于最前）。
        // rule 模式下两条均不命中，走下方智能分流
        rules.Add(new JsonObject { ["clash_mode"] = "Global", ["outbound"] = "PROXY" });
        rules.Add(new JsonObject { ["clash_mode"] = "Direct", ["outbound"] = "DIRECT" });

        // 私网直连：与 mihomo 侧 standard/minimal 两种模式的行为一致（私网始终直连）
        rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "DIRECT" });

        var ruleSet = new JsonArray();
        if (s.RuleMode != "minimal")
        {
            // 智能分流（sing-box 用官方 srs 规则包，jsDelivr 国内镜像）：
            // 广告拦截 → 国内域名直连 → 国内 IP 直连，其余走 PROXY
            rules.Add(new JsonObject { ["rule_set"] = new JsonArray("ads"), ["outbound"] = "BLOCK" });
            rules.Add(new JsonObject { ["rule_set"] = new JsonArray("geosite-cn"), ["outbound"] = "DIRECT" });
            rules.Add(new JsonObject { ["rule_set"] = new JsonArray("geoip-cn"), ["outbound"] = "DIRECT" });
            ruleSet.Add(new JsonObject
            {
                ["type"] = "remote", ["tag"] = "ads", ["format"] = "binary",
                ["url"] = "https://cdn.jsdelivr.net/gh/MetaCubeX/meta-rules-dat@sing/geo/geosite/category-ads-all.srs",
                ["download_detour"] = "DIRECT",
                ["update_interval"] = "1h",
            });
            ruleSet.Add(new JsonObject
            {
                ["type"] = "remote", ["tag"] = "geosite-cn", ["format"] = "binary",
                // 注意必须是 geosite/cn（v2fly cn 完整类别，11 万条）；geolocation-cn 只是
                // “确认大陆注册域名”小子集（5407 条），jd/taobao/bilibili 都不在里面
                ["url"] = "https://cdn.jsdelivr.net/gh/MetaCubeX/meta-rules-dat@sing/geo/geosite/cn.srs",
                ["download_detour"] = "DIRECT",
                ["update_interval"] = "1h",
            });
            ruleSet.Add(new JsonObject
            {
                ["type"] = "remote", ["tag"] = "geoip-cn", ["format"] = "binary",
                ["url"] = "https://cdn.jsdelivr.net/gh/MetaCubeX/meta-rules-dat@sing/geo/geoip/cn.srs",
                ["download_detour"] = "DIRECT",
                ["update_interval"] = "1h",
            });
        }

        return new JsonObject
        {
            ["rules"] = rules,
            ["rule_set"] = ruleSet,
            ["final"] = "PROXY",
            ["auto_detect_interface"] = true,
        };
    }

    private static JsonObject Group(string type, string tag, List<string> members, string? url = null)
    {
        var g = new JsonObject
        {
            ["type"] = type,
            ["tag"] = tag,
            ["outbounds"] = new JsonArray(members.Select(m => (JsonNode)m).ToArray()),
            ["interrupt_exist_connections"] = false,
        };
        if (url != null)
        {
            g["url"] = url;
            g["interval"] = "5m";
            g["tolerance"] = 50;
        }
        return g;
    }

    private static JsonObject Outbound(string type, string tag)
    {
        var o = new JsonObject { ["type"] = type, ["tag"] = tag };
        if (type == "block")
        {
            o = new JsonObject { ["type"] = "block", ["tag"] = tag };
        }
        return o;
    }

    /// <summary>订阅缓存文本：远程订阅没有缓存时返回 null（由用户点「更新」拉取）。</summary>
    private static string? CacheText(AppSettings s, ProfileItem p)
    {
        var path = Path.Combine(s.ProvidersDir, $"{p.Id}.yaml");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ============ 订阅 → sing-box outbounds ============

    /// <summary>把一份订阅文本解析成 sing-box outbounds。支持 Clash YAML 与 sing-box JSON。</summary>
    public static IEnumerable<JsonObject> ParseNodes(string text, ProfileItem p, Action<string>? log)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') && text.Contains("\"outbounds\""))
        {
            return ParseSingBoxJson(text, log);
        }
        return ParseClashYaml(text, p, log);
    }

    private static IEnumerable<JsonObject> ParseSingBoxJson(string text, Action<string>? log)
    {
        var result = new List<JsonObject>();
        try
        {
            var root = System.Text.Json.JsonDocument.Parse(text).RootElement;
            if (!root.TryGetProperty("outbounds", out var arr))
            {
                yield break;
            }
            var groupTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "selector", "urltest", "direct", "block", "dns", "socks", "http" };
            foreach (var o in arr.EnumerateArray())
            {
                var type = o.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var tag = o.TryGetProperty("tag", out var tg) ? tg.GetString() ?? "" : "";
                if (type.Length == 0 || tag.Length == 0 || groupTypes.Contains(type))
                {
                    continue;
                }
                result.Add(JsonObject.Parse(o.GetRawText())!.AsObject());
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"[shadowrocket] sing-box 格式订阅解析失败：{e.Message}");
        }
        foreach (var r in result) yield return r;
    }

    private static IEnumerable<JsonObject> ParseClashYaml(string text, ProfileItem p, Action<string>? log)
    {
        var result = new List<JsonObject>();
        try
        {
            var doc = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(text);
            if (doc == null || !doc.TryGetValue("proxies", out var rawProxies))
            {
                log?.Invoke($"[shadowrocket] 订阅「{p.Name}」没有 proxies 段，无法在 sing-box 内核下使用");
                yield break;
            }
            if (rawProxies is not IEnumerable<object> list)
            {
                yield break;
            }
            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "ss", "vmess", "vless", "trojan", "hysteria2", "hy2", "tuic" };
            var idx = 0;
            foreach (var raw in list)
            {
                idx++;
                if (raw is not Dictionary<object, object> map)
                {
                    continue;
                }
                var clashType = Str(map, "type") ?? "";
                if (!supported.Contains(clashType))
                {
                    log?.Invoke($"[shadowrocket] 节点 {Str(map, "name")}（{clashType}）暂不支持 sing-box 转换，已跳过");
                    continue;
                }
                if (Str(map, "plugin") is { Length: > 0 } plugin && !plugin.StartsWith("v2ray", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"[shadowrocket] 节点 {Str(map, "name")} 的 plugin={plugin} 暂不支持，已跳过");
                    continue;
                }
                var outbound = ConvertNode(clashType, map);
                if (outbound != null)
                {
                    result.Add(outbound);
                }
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"[shadowrocket] 订阅「{p.Name}」解析失败：{e.Message}");
        }
        foreach (var r in result) yield return r;
    }

    private static JsonObject? ConvertNode(string clashType, Dictionary<object, object> m)
    {
        var name = Str(m, "name") ?? $"node-{m.GetHashCode()}";
        var server = Str(m, "server") ?? "";
        var port = Int(m, "port");
        if (server.Length == 0 || port <= 0)
        {
            return null;
        }
        var type = clashType.ToLowerInvariant() switch
        {
            "ss" => "shadowsocks",
            "hy2" => "hysteria2",
            var t => t,
        };
        var o = new JsonObject { ["type"] = type, ["tag"] = name, ["server"] = server, ["server_port"] = port };

        var transport = BuildTransport(m);
        var tls = BuildTls(m, type, server, transport != null);

        switch (type)
        {
            case "shadowsocks":
                o["method"] = Str(m, "cipher") ?? "";
                o["password"] = Str(m, "password") ?? "";
                break;

            case "vmess":
                o["uuid"] = Str(m, "uuid") ?? "";
                o["security"] = Str(m, "cipher") ?? "auto";
                o["alter_id"] = Int(m, "alterId");
                if (tls != null) o["tls"] = tls;
                if (transport != null) o["transport"] = transport;
                break;

            case "vless":
                o["uuid"] = Str(m, "uuid") ?? "";
                if (Str(m, "flow") is { Length: > 0 } flow) o["flow"] = flow;
                if (tls != null) o["tls"] = tls;
                if (transport != null) o["transport"] = transport;
                break;

            case "trojan":
                o["password"] = Str(m, "password") ?? "";
                if (tls == null)
                {
                    tls = new JsonObject { ["enabled"] = true, ["server_name"] = Str(m, "sni") ?? server };
                }
                o["tls"] = tls;
                if (transport != null) o["transport"] = transport;
                break;

            case "hysteria2":
                o["password"] = Str(m, "password") ?? Str(m, "auth") ?? "";
                if (Up(m, "up") is { } up) o["up_mbps"] = up;
                if (Up(m, "down") is { } down) o["down_mbps"] = down;
                if (Str(m, "obfs") is { Length: > 0 } obfs)
                {
                    o["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = Str(m, "obfs-password") ?? "" };
                }
                if (tls == null)
                {
                    tls = new JsonObject { ["enabled"] = true, ["server_name"] = Str(m, "sni") ?? server };
                }
                o["tls"] = tls;
                break;

            case "tuic":
                o["uuid"] = Str(m, "uuid") ?? "";
                o["password"] = Str(m, "password") ?? "";
                if (Str(m, "congestion-controller") is { Length: > 0 } cc) o["congestion_control"] = cc;
                if (Str(m, "udp-relay-mode") is { Length: > 0 } ur) o["udp_relay_mode"] = ur;
                if (Bool(m, "reduce-rtt")) o["zero_rtt_handshake"] = true;
                if (tls == null)
                {
                    tls = new JsonObject { ["enabled"] = true, ["server_name"] = Str(m, "sni") ?? server };
                }
                o["tls"] = tls;
                break;
        }
        return o;
    }

    private static JsonObject? BuildTls(Dictionary<object, object> m, string type, string server, bool hasTransport)
    {
        var needTls = Bool(m, "tls") || type is "trojan" or "hysteria2" or "tuic" ||
                      Str(m, "servername") is { Length: > 0 } ||
                      m.ContainsKey("reality-opts") || Str(m, "sni") is { Length: > 0 };
        if (!needTls)
        {
            return null;
        }
        var tls = new JsonObject { ["enabled"] = true };
        var sni = Str(m, "servername") ?? Str(m, "sni") ?? server;
        tls["server_name"] = sni;
        if (Bool(m, "skip-cert-verify")) tls["insecure"] = true;
        if (Str(m, "alpn") is { Length: > 0 } alpn)
        {
            tls["alpn"] = new JsonArray(alpn.Split(',').Select(x => (JsonNode)x.Trim()).ToArray());
        }
        if (Str(m, "client-fingerprint") is { Length: > 0 } fp)
        {
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
        }
        if (m.TryGetValue("reality-opts", out var ro) && ro is Dictionary<object, object> reality)
        {
            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = Str(reality, "public-key") ?? "",
                ["short_id"] = Str(reality, "short-id") ?? "",
            };
        }
        return tls;
    }

    private static JsonObject? BuildTransport(Dictionary<object, object> m)
    {
        var network = Str(m, "network");
        if (network is not ("ws" or "grpc" or "h2")) return null;
        if (network == "ws" && m.TryGetValue("ws-opts", out var w) && w is Dictionary<object, object> ws)
        {
            var t = new JsonObject { ["type"] = "ws", ["path"] = Str(ws, "path") ?? "/" };
            if (ws.TryGetValue("headers", out var h) && h is Dictionary<object, object> headers)
            {
                var hj = new JsonObject();
                foreach (var kv in headers) hj[kv.Key.ToString() ?? ""] = Str2(kv.Value);
                t["headers"] = hj;
            }
            if (Int(ws, "max-early-data") > 0)
            {
                t["max_early_data"] = Int(ws, "max-early-data");
                t["early_data_header_name"] = Str(ws, "early-data-header-name") ?? "Sec-WebSocket-Protocol";
            }
            return t;
        }
        if (network == "ws")
        {
            return new JsonObject { ["type"] = "ws", ["path"] = "/" };
        }
        if (network == "grpc" && m.TryGetValue("grpc-opts", out var g) && g is Dictionary<object, object> grpc)
        {
            return new JsonObject { ["type"] = "grpc", ["service_name"] = Str(grpc, "grpc-service-name") ?? "" };
        }
        if (network == "h2" && m.TryGetValue("h2-opts", out var h2) && h2 is Dictionary<object, object> hh)
        {
            var t = new JsonObject { ["type"] = "http" };
            if (Str(hh, "host") is { Length: > 0 } host)
            {
                t["host"] = new JsonArray(host.Split(',').Select(x => (JsonNode)x.Trim()).ToArray());
            }
            t["path"] = Str(hh, "path") ?? "/";
            return t;
        }
        return null;
    }

    // ---- Clash YAML 弱类型取值工具 ----

    private static string? Str(Dictionary<object, object> m, string key) =>
        m.TryGetValue(key, out var v) ? v switch
        {
            string s => s,
            int i => i.ToString(),
            long l => l.ToString(),
            bool b => b.ToString().ToLowerInvariant(),
            double d => d.ToString("0"),
            _ => v.ToString(),
        } : null;

    private static string? Str2(object v) => v switch
    {
        string s => s,
        _ => v.ToString(),
    };

    private static int Int(Dictionary<object, object> m, string key) =>
        m.TryGetValue(key, out var v) && int.TryParse(Str2(v), out var i) ? i : 0;

    private static bool Bool(Dictionary<object, object> m, string key) =>
        m.TryGetValue(key, out var v) && bool.TryParse(Str2(v), out var b) && b;

    /// <summary>clash 的 up/down 可能是 "30 Mbps" 或数字，统一取数值部分。</summary>
    private static int? Up(Dictionary<object, object> m, string key)
    {
        if (!m.TryGetValue(key, out var v)) return null;
        var digits = new string(Str2(v)?.TakeWhile(char.IsDigit).ToArray() ?? []);
        return int.TryParse(digits, out var i) && i > 0 ? i : null;
    }
}
