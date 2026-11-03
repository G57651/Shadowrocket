using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace Shadowrocket.Core;

/// <summary>
/// TUN 依赖的 wintun.dll：首次开启 TUN 时自动从 wintun.net 官方构建下载，
/// 解压 amd64 版本放到内核目录（Clash Verge 同款做法）。
/// </summary>
public static class WintunService
{
    private const string Url = "https://www.wintun.net/builds/wintun-0.14.1.zip";

    public static bool IsInstalled(AppSettings s) => File.Exists(Path.Combine(AppSettings.Dir, "core", "wintun.dll"));

    /// <summary>确保 wintun.dll 就位；下载失败抛异常，由调用方提示。</summary>
    public static async Task EnsureAsync(AppSettings s, Action<string>? log)
    {
        var target = Path.Combine(AppSettings.Dir, "core", "wintun.dll");
        if (File.Exists(target))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var zipPath = Path.Combine(Path.GetTempPath(), "wintun-0.14.1.zip");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Shadowrocket-Windows/1.0");
            log?.Invoke("[shadowrocket] 正在下载 wintun 虚拟网卡驱动（TUN 模式依赖）…");
            using var resp = await http.GetAsync(Url);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zipPath);
            await resp.Content.CopyToAsync(fs);
        }

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.GetEntry("wintun/bin/amd64/wintun.dll") ?? throw new InvalidOperationException("wintun 压缩包结构异常");
            // 完整性校验（共识修复第 7 条）：wintun.dll 会被提权内核加载，落盘前必须比对硬编码官方哈希
            var tmp = target + ".new";
            using (var es = entry.Open())
            using (var fs = File.Create(tmp))
            {
                await es.CopyToAsync(fs);
            }
            var actual = IntegrityVerifier.Sha256OfFile(tmp);
            if (!string.Equals(actual, IntegrityVerifier.WintunDllSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidOperationException(
                    $"wintun.dll 完整性校验失败：期望 {IntegrityVerifier.WintunDllSha256}，实际 {actual}。已拒绝安装。");
            }
            File.Move(tmp, target, overwrite: true);
        }
        File.Delete(zipPath);
        log?.Invoke("[shadowrocket] wintun.dll 安装完成");
    }
}
