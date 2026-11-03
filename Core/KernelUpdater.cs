using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Shadowrocket.Core;

/// <summary>
/// 内核下载与更新：从 GitHub 拉取官方 mihomo release，替换软件数据目录内的内核并重启。
/// 集成形态 = 内核随软件安装/更新，不依赖机器上其它软件（ui-spec §3.4 更新组）。
/// </summary>
public class KernelUpdater
{
    private readonly AppSettings _settings;

    public KernelUpdater(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>当前已安装内核的版本号，如 "v1.19.33"；未安装返回 ""。</summary>
    public string InstalledVersion
    {
        get
        {
            var exe = _settings.CoreExePath;
            if (!File.Exists(exe) || !_settings.IsTrustedCoreExe(exe))
            {
                return "";
            }
            try
            {
                var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                psi.ArgumentList.Add("-v");
                using var p = Process.Start(psi)!;
                var line = p.StandardOutput.ReadLine() ?? "";
                p.WaitForExit(3000);
                var m = System.Text.RegularExpressions.Regex.Match(line, @"v[\d][\w.\-]*");
                return m.Success ? m.Value : "";
            }
            catch
            {
                return "";
            }
        }
    }

    public record ReleaseInfo(string Tag, string AssetUrl, string AssetName, DateTime PublishedAt, string? ChecksumUrl = null);

    /// <summary>完整性校验无法给出结论时的用户确认入口（由 AppRuntime 接 UI）。未设置视为拒绝。</summary>
    public Func<string, Task<bool>>? ConfirmUnverifiedInstall { get; set; }

    /// <summary>查询 GitHub 上的最新版本；alpha 通道取最新 pre-release。</summary>
    public async Task<ReleaseInfo?> GetLatestAsync()
    {
        using var http = CreateDownloader();
        var url = _settings.CoreChannel == "alpha"
            ? "https://api.github.com/repos/MetaCubeX/mihomo/releases?per_page=10"
            : "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest";

        using var r = await http.GetAsync(url);
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());

        JsonElement release;
        if (_settings.CoreChannel == "alpha")
        {
            release = doc.RootElement.EnumerateArray()
                .FirstOrDefault(x => x.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True);
            if (release.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }
        }
        else
        {
            release = doc.RootElement;
        }

        var tag = release.GetProperty("tag_name").GetString() ?? "";
        string? assetUrl = null, assetName = null;
        var assetList = new List<(string Name, string Url)>();
        foreach (var a in release.GetProperty("assets").EnumerateArray())
        {
            var n = a.GetProperty("name").GetString() ?? "";
            assetList.Add((n, a.GetProperty("browser_download_url").GetString() ?? ""));
            if (assetUrl == null &&
                (n == $"mihomo-windows-amd64-{tag}.zip" ||
                 (n.StartsWith("mihomo-windows-amd64") && n.EndsWith(".zip") && n.Contains(tag) && !n.Contains("-go1"))))
            {
                assetUrl = a.GetProperty("browser_download_url").GetString();
                assetName = n;
            }
        }
        if (assetUrl == null && tag == "Prerelease-Alpha")
        {
            // mihomo 的 Alpha 是滚动发布：tag 恒为 "Prerelease-Alpha"，资产名里只有提交哈希
            // （如 mihomo-windows-amd64-alpha-69c1631.zip），按 tag 匹配永远失败；
            // 这里固定取默认架构的 alpha 构建，并把短哈希作为版本号用于"已是最新"比对
            foreach (var a in release.GetProperty("assets").EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (n.StartsWith("mihomo-windows-amd64-alpha-") && n.EndsWith(".zip"))
                {
                    assetUrl = a.GetProperty("browser_download_url").GetString();
                    assetName = n;
                    break;
                }
            }
            if (assetUrl != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(assetName ?? "", @"alpha-([0-9a-f]+)\.zip$");
                tag = m.Success ? $"alpha-{m.Groups[1].Value}" : assetName;
            }
        }
        if (assetUrl == null)
        {
            return null;
        }
        var published = release.TryGetProperty("published_at", out var pa) && pa.TryGetDateTime(out var dt) ? dt : DateTime.MinValue;
        var checksumUrl = assetName == null ? null : IntegrityVerifier.FindChecksumAssetUrl(assetList, assetName);
        return new ReleaseInfo(tag ?? "", assetUrl, assetName!, published, checksumUrl);
    }

    /// <summary>
    /// 通道切换即卸载：稳定版与 Alpha 共用同一个 exe 路径，新构建写入即完成"替换"，
    /// 这里负责把被换下的那个通道的残留清干净——旧构建的 .bak 回滚副本与旧版本记录。
    /// 同通道内的正常更新不清理（保留 .bak 用于回滚）。清理失败不影响安装本身。
    /// </summary>
    private void UninstallReplacedChannel(string exePath, string versionFilePath, string incomingTag)
    {
        try
        {
            var prevText = File.Exists(versionFilePath) ? File.ReadAllText(versionFilePath).Trim() : "";
            var incomingAlpha = incomingTag.Contains("alpha", StringComparison.OrdinalIgnoreCase);
            var prevAlpha = prevText.Contains("alpha", StringComparison.OrdinalIgnoreCase);
            // 首次写入版本记录（prevText 为空）时，被替换的构建通道未知，同样视为切换
            if (prevText.Length > 0 && incomingAlpha == prevAlpha)
            {
                return;
            }
            var bak = exePath + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            if (File.Exists(versionFilePath))
            {
                File.Delete(versionFilePath);   // 随后由调用方写入新通道的版本记录
            }
        }
        catch
        {
        }
    }

    /// <summary>下载并替换内核；成功后返回新版本号。coreManager 不为空时更新后重启内核。</summary>
    public async Task<string> InstallOrUpdateAsync(ReleaseInfo release, Action<double>? progress, CoreManager? coreManager)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"shadowrocket-core-{release.Tag}.zip");
        using (var http = CreateDownloader())
        {
            using var resp = await http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 21_500_000;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zipPath);
            var buffer = new byte[1024 * 128];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n));
                read += n;
                progress?.Invoke(Math.Clamp(read / (double)total, 0, 1));
            }
        }

        // 固定写 mihomo.exe：本方法也可能在 sing-box 被选中时被调用（设置页"下载 mihomo"按钮），
        // 此时 CoreExePath 指向 sing-box.exe，绝不能覆盖它
        // 完整性校验（共识修复第 7 条）：失败即抛异常拒绝安装，旧内核保留
        await VerifyKernelZipAsync(release, zipPath);

        var exePath = _settings.MihomoExePath;
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

        coreManager?.Stop();

        // 解压 zip 里的 exe（名字可能是 mihomo-windows-amd64.exe）
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".exe"))!;
            var tmp = exePath + ".new";
            using var es = entry.Open();
            using var fs = File.Create(tmp);
            await es.CopyToAsync(fs);

            if (File.Exists(exePath))
            {
                File.Copy(exePath, exePath + ".bak", overwrite: true);
            }
            File.Move(tmp, exePath, overwrite: true);
        }
        File.Delete(zipPath);
        UninstallReplacedChannel(exePath, _settings.VersionFilePath, release.Tag);
        File.WriteAllText(_settings.VersionFilePath, release.Tag);

        coreManager?.Start(elevated: _settings.TunnelMode == TunnelMode.Tun);
        return release.Tag;
    }

    /// <summary>已安装 sing-box 内核的版本号；未安装返回 ""。</summary>
    public string InstalledSingBoxVersion
    {
        get
        {
            var vfile = _settings.SingBoxVersionFilePath;
            return File.Exists(vfile) ? File.ReadAllText(vfile).Trim() : "";
        }
    }

    /// <summary>
    /// 版本文件内容（上次安装时写入的 tag）。mihomo Alpha 是滚动发布、exe 自报版本与 tag 不同，
    /// Alpha 通道的"已是最新"比对要用它，而不是探测 exe 的输出。
    /// </summary>
    public string InstalledMihomoFileVersion
    {
        get
        {
            var vfile = _settings.VersionFilePath;
            return File.Exists(vfile) ? File.ReadAllText(vfile).Trim() : "";
        }
    }

    /// <summary>
    /// 已安装 mihomo 内核的版本号；未安装返回 ""。
    /// 注意不能复用 InstalledVersion（它跟随 CoreExePath 指向当前所选内核），
    /// 设置页需要固定探测 mihomo.exe，才能在 sing-box 选中时展示 mihomo 的安装状态。
    /// </summary>
    public string InstalledMihomoVersion
    {
        get
        {
            var exe = _settings.MihomoExePath;
            if (!File.Exists(exe) || !_settings.IsTrustedCoreExe(exe))
            {
                return "";
            }
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    ArgumentList = { "-v" },
                })!;
                var line = p.StandardOutput.ReadLine() ?? "";
                p.WaitForExit(3000);
                var m = System.Text.RegularExpressions.Regex.Match(line, @"v[\d][\w.\-]*");
                return m.Success ? m.Value : "";
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>
    /// 下载并安装 sing-box 内核（SagerNet 官方 release，Hiddify 的内核同为 sing-box）。
    /// 成功后返回版本号。
    /// </summary>
    /// <summary>查询 sing-box 最新版本（SagerNet 官方 release）。找不到 Windows 资产时返回 null。</summary>
    public async Task<ReleaseInfo?> GetLatestSingBoxAsync()
    {
        using var http = CreateDownloader();
        // sing-box 同样有稳定版（v1.14.4）与 Alpha 预发布（v1.15.0-alpha.x）双通道，
        // Alpha 通道要查 releases 列表取第一个 prerelease，/releases/latest 永远只回稳定版
        var isAlpha = _settings.CoreChannel == "alpha";
        using var r = await http.GetAsync(isAlpha
            ? "https://api.github.com/repos/SagerNet/sing-box/releases?per_page=15"
            : "https://api.github.com/repos/SagerNet/sing-box/releases/latest");
        r.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());

        System.Text.Json.JsonElement release;
        if (isAlpha)
        {
            release = doc.RootElement.EnumerateArray()
                .FirstOrDefault(x => x.TryGetProperty("prerelease", out var pre) && pre.ValueKind == System.Text.Json.JsonValueKind.True);
            if (release.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                return null;
            }
        }
        else
        {
            release = doc.RootElement;
        }

        var tag = release.GetProperty("tag_name").GetString() ?? "";
        var baseName = $"sing-box-{tag.TrimStart('v')}-windows-amd64";
        string? assetUrl = null;
        var assetList = new List<(string Name, string Url)>();
        foreach (var a in release.GetProperty("assets").EnumerateArray())
        {
            var n = a.GetProperty("name").GetString() ?? "";
            assetList.Add((n, a.GetProperty("browser_download_url").GetString() ?? ""));
            if (n == baseName + ".zip")
            {
                assetUrl = a.GetProperty("browser_download_url").GetString();
            }
        }
        if (assetUrl == null)
        {
            return null;
        }
        var assetName = baseName + ".zip";
        var checksumUrl = IntegrityVerifier.FindChecksumAssetUrl(assetList, assetName);
        return new ReleaseInfo(tag, assetUrl, assetName, DateTime.MinValue, checksumUrl);
    }

    /// <summary>下载并安装 sing-box 内核。成功后返回版本号。</summary>
    public async Task<string> InstallSingBoxAsync(ReleaseInfo release, Action<double>? progress, CoreManager? coreManager)
    {
        var tag = release.Tag;
        var zipPath = Path.Combine(Path.GetTempPath(), $"shadowrocket-singbox-{tag}.zip");
        // GitHub 直连经常中断，失败自动重试（最多 3 次）
        using var http = CreateDownloader();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var resp = await http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? 32_000_000;
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(zipPath);
                    var buffer = new byte[1024 * 128];
                    long read = 0;
                    int n;
                    while ((n = await src.ReadAsync(buffer)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, n));
                        read += n;
                        progress?.Invoke(Math.Clamp(read / (double)total, 0, 1));
                    }
                }
                break;
            }
            catch when (attempt < 3)
            {
                progress?.Invoke(0);
                await Task.Delay(2000 * attempt);
            }
        }

        // 完整性校验（共识修复第 7 条）：失败即抛异常拒绝安装，旧内核保留
        await VerifyKernelZipAsync(release, zipPath);

        var exePath = _settings.SingBoxExePath;
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        coreManager?.Stop();

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("sing-box.exe"))!;
            var tmp = exePath + ".new";
            using var es = entry.Open();
            using var fs = File.Create(tmp);
            await es.CopyToAsync(fs);
            File.Move(tmp, exePath, overwrite: true);
        }
        File.Delete(zipPath);
        UninstallReplacedChannel(exePath, _settings.SingBoxVersionFilePath, tag);
        File.WriteAllText(_settings.SingBoxVersionFilePath, tag);

        coreManager?.Start(elevated: _settings.TunnelMode == TunnelMode.Tun);
        return tag;
    }

    /// <summary>
    /// 内核 zip 完整性校验（共识修复第 7 条），失败抛异常拒绝安装（旧内核保留）。策略：
    /// 1) release 官方校验和（上游若开始提供即自动启用）：不匹配直接拒绝；
    /// 2) 官方缺失/解析失败 → 回退内置哈希清单（stable 锁版本）：不匹配拒绝，匹配仍需用户确认；
    /// 3) TOFU：首次确认安装的版本哈希记入信任库，同版本再次安装自动比对（匹配免弹窗、不匹配拒绝）；
    /// 4) 完全无校验来源：alpha/nightly 一律拒绝；stable 需用户显式确认后方可继续（确认后记入信任库）。
    /// </summary>
    private async Task VerifyKernelZipAsync(ReleaseInfo release, string zipPath)
    {
        var actual = IntegrityVerifier.Sha256OfFile(zipPath);

        // 1) release 官方校验和（当前 mihomo/sing-box 均未提供，为上游未来提供做准备）
        if (release.ChecksumUrl != null)
        {
            using var http = CreateDownloader();
            var official = await IntegrityVerifier.FetchOfficialHashAsync(http, release.ChecksumUrl, release.AssetName);
            if (official != null)
            {
                if (!string.Equals(official, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"内核完整性校验失败（release 官方校验和）：期望 {official}，实际 {actual}。已拒绝安装，旧内核保留。");
                }
                return;   // 最强来源校验通过，直接安装
            }
        }

        // 2) 回退内置哈希清单（stable 锁版本优先）
        if (IntegrityVerifier.KnownKernelHashes.TryGetValue(release.Tag, out var known))
        {
            if (!string.Equals(known, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"内核完整性校验失败（内置哈希清单）：期望 {known}，实际 {actual}。已拒绝安装，旧内核保留。");
            }
            var okList = ConfirmUnverifiedInstall != null && await ConfirmUnverifiedInstall(
                $"版本 {release.Tag} 缺少官方校验和，已按内置哈希清单校验并通过。\n是否继续安装？");
            if (!okList)
            {
                throw new InvalidOperationException("用户取消安装（官方校验和缺失，内置哈希校验已通过）。");
            }
            return;
        }

        // 3) TOFU：此前首次信任时记录过该版本哈希 → 自动比对，匹配免弹窗、不匹配拒绝（防首次信任后被调包）
        var trusted = IntegrityVerifier.LoadTrustedHashes();
        if (trusted.TryGetValue(release.Tag, out var remembered))
        {
            if (!string.Equals(remembered, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"内核完整性校验失败（与首次信任记录的哈希不一致）：版本 {release.Tag} 期望 {remembered}，实际 {actual}。\n下载内容可能在首次信任后被篡改，已拒绝安装，旧内核保留。");
            }
            return;
        }

        // 4) 无任何校验来源
        var isAlpha = release.Tag.Contains("alpha", StringComparison.OrdinalIgnoreCase) ||
                      release.Tag.StartsWith("Prerelease", StringComparison.OrdinalIgnoreCase);
        if (isAlpha)
        {
            throw new InvalidOperationException(
                $"Alpha 版本 {release.Tag} 无任何校验来源（官方校验和缺失、不在内置哈希清单、无首次信任记录），按策略拒绝安装。");
        }
        var ok = ConfirmUnverifiedInstall != null && await ConfirmUnverifiedInstall(
            $"警告：版本 {release.Tag} 无任何完整性校验来源（release 无官方校验和，也不在内置哈希清单中）。\n无法排除下载内容被篡改，仍要继续安装吗？\n（确认后本版本哈希将记入信任库，之后安装同一版本将自动校验、不再弹窗。）");
        if (!ok)
        {
            throw new InvalidOperationException("用户取消未校验的安装。");
        }
        // 首次信任：记录该版本哈希，后续同版本安装走 TOFU 自动校验
        IntegrityVerifier.SaveTrustedHash(release.Tag, actual);
    }

    /// <summary>下载器：直连优先，失败自动改走本机混合端口（首次装内核时系统里可能没有可用代理，直连足够）。</summary>
    private static HttpClient CreateDownloader()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            Proxy = null,
            UseProxy = false,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Shadowrocket-Windows/1.0");
        return client;
    }
}
