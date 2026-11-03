using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Shadowrocket.Core;

/// <summary>
/// 内核/驱动下载完整性校验（共识修复第 7 条）。策略分层：
/// 1. wintun.dll：硬编码官方 SHA-256（版本固定文件，最强校验）；
/// 2. 内核：优先解析 release 内官方校验和资产（为上游未来提供做准备）；
///    缺失或解析失败时回退内置哈希清单（stable 锁版本），并要求用户确认；
/// 3. alpha/nightly 通道：一律走"内置清单 + 确认"；完全无校验来源时拒绝安装。
/// 注：内置哈希为一次性从官方 release 地址（TLS）拉取后录入，属引导信任锚，
/// 不能防御 release 页面被整体接管的情形。
/// </summary>
public static class IntegrityVerifier
{
    /// <summary>wintun-0.14.1.zip 内 wintun/bin/amd64/wintun.dll 的官方 SHA-256（wintun.net 官方构建）。</summary>
    public const string WintunDllSha256 = "e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce";

    /// <summary>
    /// 内置内核哈希清单：tag → sha256(zip)。stable 锁版本优先。
    /// 来源与复核记录（可复现）：
    /// · v1.19.33：GitHub 官方 release 直下 + 镜像通道（gh-proxy.com）双路下载，两次 SHA-256 一致；
    /// · v1.14.4：GitHub 官方 release 直下 + 镜像通道（gh-proxy.com）双路下载，两次 SHA-256 一致。
    /// 属 TLS 引导信任锚：未能与上游独立发布的哈希清单交叉比对（两家 release 均无校验和资产，已实测确认）。
    /// </summary>
    public static readonly Dictionary<string, string> KnownKernelHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        // mihomo-windows-amd64-v1.19.33.zip（64 位十六进制）
        ["v1.19.33"] = "6ab2180875f9f6fff8b19386d760daec257f66b45f72b3afc650634272fa837a",
        // sing-box-1.14.4-windows-amd64.zip
        ["v1.14.4"] = "99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df",
    };

    /// <summary>
    /// TOFU（首次信任后记住）哈希库：用户对"无校验来源"的版本显式确认安装后，
    /// 记录该 tag 的 zip 哈希；同版本再次安装时自动比对，不再弹窗。
    /// </summary>
    public static string TrustedHashesPath => Path.Combine(AppSettings.Dir, "verified-hashes.json");

    public static Dictionary<string, string> LoadTrustedHashes()
    {
        try
        {
            if (File.Exists(TrustedHashesPath))
            {
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                           File.ReadAllText(TrustedHashesPath)) ?? new Dictionary<string, string>();
            }
        }
        catch
        {
            // 信任库损坏按空处理：退化为重新首次信任，不阻断更新
        }
        return new Dictionary<string, string>();
    }

    public static void SaveTrustedHash(string tag, string sha256)
    {
        try
        {
            var trusted = LoadTrustedHashes();
            trusted[tag] = sha256.ToLowerInvariant();
            Directory.CreateDirectory(AppSettings.Dir);
            AtomicFile.WriteAllText(TrustedHashesPath,
                System.Text.Json.JsonSerializer.Serialize(trusted, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 记录失败不影响本次安装（下次安装会再次走首次信任确认）
        }
    }

    public static string Sha256OfFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>在 release 资产列表里寻找与内核资产匹配的官方校验和资产；没有返回 null。</summary>
    public static string? FindChecksumAssetUrl(IEnumerable<(string Name, string Url)> assets, string assetName)
    {
        var lowerAsset = assetName.ToLowerInvariant();
        foreach (var (name, url) in assets)
        {
            var lower = name.ToLowerInvariant();
            if ((lower.StartsWith(lowerAsset, StringComparison.Ordinal) &&
                 (lower.EndsWith(".sha256") || lower.EndsWith(".sha256sum") || lower.EndsWith(".dgst"))) ||
                lower == "sha256sum" || lower == "sha256sums")
            {
                return url;
            }
        }
        return null;
    }

    /// <summary>下载并解析官方校验和文件，返回指定资产的期望 sha256；不可解析返回 null。</summary>
    public static async Task<string?> FetchOfficialHashAsync(HttpClient http, string checksumUrl, string assetName)
    {
        try
        {
            var text = await http.GetStringAsync(checksumUrl);
            return ParseHash(text, assetName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析常见校验和格式：`HASH *file` / `HASH file` / `file: HASH` / 单一 64 位十六进制。</summary>
    public static string? ParseHash(string text, string assetName)
    {
        var hex = new Regex("[0-9a-fA-F]{64}");
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.IndexOf(assetName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var m = hex.Match(line);
                if (m.Success)
                {
                    return m.Value.ToLowerInvariant();
                }
            }
        }
        // 无文件名匹配：整份文件恰有一个哈希时按单文件校验和接受
        var all = hex.Matches(text);
        return all.Count == 1 ? all[0].Value.ToLowerInvariant() : null;
    }
}
