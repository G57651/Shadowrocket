using System.Diagnostics;
using System.IO;

namespace Shadowrocket.Core;

/// <summary>
/// 内核进程生命周期管理：启动、停止、崩溃自动重启（普通模式）、健康探测。
/// 用 PID 文件识别"本软件启动的"内核实例，启动前先清理上次遗留，
/// TUN 模式需要 UAC 提权，此时进程归系统所有，仅通过 controller 探测。
/// </summary>
public class CoreManager
{
    private readonly AppSettings _settings;
    private Process? _process;
    private int _restartCount;
    private int _generation;
    private DateTime _lastStartAt;

    private string PidFilePath => Path.Combine(AppSettings.Dir, "core", "mihomo.pid");

    public CoreManager(AppSettings settings)
    {
        _settings = settings;
    }

    public bool IsRunning
    {
        get
        {
            if (_process is { HasExited: false })
            {
                return true;
            }
            return ProbeController().GetAwaiter().GetResult();
        }
    }

    public event Action<string>? Log;

    public bool IsCoreInstalled => File.Exists(_settings.CoreExePath);

    public string CoreVersionText
    {
        get
        {
            try
            {
                if (!_settings.IsTrustedCoreExe(_settings.CoreExePath))
                {
                    return "未安装";
                }
                var psi = new ProcessStartInfo
                {
                    FileName = _settings.CoreExePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(_settings.IsSingBoxCore ? "version" : "-v");
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                if (_settings.IsSingBoxCore)
                {
                    // sing-box 输出 "sing-box version 1.14.4"，提取并统一 v 前缀
                    var mv = System.Text.RegularExpressions.Regex.Match(output, "version\\s+(v?[\\d][\\w.\\-]*)");
                    return mv.Success
                        ? (mv.Groups[1].Value.StartsWith("v") ? mv.Groups[1].Value : "v" + mv.Groups[1].Value)
                        : output.Split('\n').FirstOrDefault() ?? "";
                }
                var mVer = System.Text.RegularExpressions.Regex.Match(output, "v[\\d][\\w.\\-]*");
                return mVer.Success ? mVer.Value : output.Split('\n').FirstOrDefault() ?? "";
            }
            catch
            {
                return "未安装";
            }
        }
    }

    /// <summary>
    /// 清理本软件遗留的内核实例。三级属主校验（共识修复第 6 条），只杀自己启动的实例：
    /// 1) 镜像路径：进程可执行文件全路径 == 本软件内核路径（core 目录下的 mihomo.exe / sing-box.exe）；
    /// 2) PID：与启动时写入的 pid 文件一致（覆盖因权限读不到镜像路径的情形）；
    /// 3) controller secret：前两级都拿不到结论、同名候选恰有一个，且控制器以本软件 secret 应答 → 推定为本软件实例。
    /// 任何一级都不命中的一律跳过并记日志，绝不误杀。
    /// </summary>
    private void KillStaleInstance()
    {
        var ownPaths = new[] { _settings.MihomoExePath, _settings.SingBoxExePath };
        var (pidFromFile, pidTicks) = ReadPidFile();

        foreach (var name in new[] { "mihomo", "sing-box" })
        {
            var candidates = new List<Process>();
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!p.HasExited)
                    {
                        candidates.Add(p);
                    }
                    else
                    {
                        p.Dispose();
                    }
                }
                catch
                {
                    p.Dispose();
                }
            }

            foreach (var stale in candidates)
            {
                var ours = false;
                var why = "";

                // 第 1 级：镜像路径。能读到路径且不等于本软件内核的，直接跳过（明确不是本软件的）
                try
                {
                    var image = stale.MainModule?.FileName;
                    if (image != null)
                    {
                        if (ownPaths.Any(p => string.Equals(p, image, StringComparison.OrdinalIgnoreCase)))
                        {
                            ours = true;
                            why = "镜像路径为本软件内核";
                        }
                        else
                        {
                            Log?.Invoke($"[shadowrocket] 跳过同名进程 pid={stale.Id} path={image}（非本软件内核）");
                            stale.Dispose();
                            continue;
                        }
                    }
                }
                catch
                {
                    // 读不到镜像路径（如跨完整性级别），继续走 PID / secret 判定
                }

                // 第 2 级：PID 文件 + 启动时间（±3s，防 PID 复用）
                if (!ours && pidFromFile == stale.Id)
                {
                    if (pidTicks > 0)
                    {
                        try
                        {
                            var recorded = new DateTime(pidTicks, DateTimeKind.Utc);
                            var delta = Math.Abs((stale.StartTime.ToUniversalTime() - recorded).TotalSeconds);
                            if (delta <= 3)
                            {
                                ours = true;
                                why = $"与 pid 文件一致且启动时间吻合（Δ{delta:0.#}s）";
                            }
                            else
                            {
                                Log?.Invoke($"[shadowrocket] pid {stale.Id} 与 pid 文件一致但启动时间不符（Δ{delta:0.#}s），判定 PID 被复用，跳过");
                            }
                        }
                        catch
                        {
                            // 提权进程读不到 StartTime：时间校验不可行，退回仅 PID 判据（记录降级）
                            ours = true;
                            why = "与 pid 文件一致（启动时间不可读，未做复用校验）";
                        }
                    }
                    else
                    {
                        // 旧格式 pid 文件（纯数字，无启动时间）：只能按 PID 认定，记录判据降级
                        ours = true;
                        why = "与 pid 文件一致（旧格式无启动时间，未做复用校验）";
                    }
                }

                // 第 3 级：controller secret（仅当同名候选唯一时推定，避免误伤）
                if (!ours && candidates.Count == 1 && ProbeControllerSync())
                {
                    ours = true;
                    why = "控制器以本软件 secret 应答且同名候选唯一";
                }

                if (ours)
                {
                    var killed = false;
                    try
                    {
                        stale.Kill(entireProcessTree: true);
                        stale.WaitForExit(3000);
                        killed = stale.HasExited;
                    }
                    catch
                    {
                        // 提权进程可能因权限不足杀不掉；该场景由提权停止逻辑负责，这里不重复处理
                    }
                    // 日志如实反映结果，禁止"先宣告清理、后静默失败"的误导
                    Log?.Invoke(killed
                        ? $"[shadowrocket] 已清理遗留内核进程 (pid {stale.Id}，{why})"
                        : $"[shadowrocket] 警告：遗留内核进程 (pid {stale.Id}，{why}) 未能结束（可能为提权实例，权限不足）");
                }
                stale.Dispose();
            }
        }
        try { File.Delete(PidFilePath); } catch { }
    }

    /// <summary>读取 pid 文件。格式 "pid|启动时间(UTC ticks)"；兼容旧版纯数字（ticks 返回 0）。</summary>
    private (int Pid, long Ticks) ReadPidFile()
    {
        try
        {
            if (File.Exists(PidFilePath))
            {
                var parts = File.ReadAllText(PidFilePath).Trim().Split('|');
                if (parts.Length >= 1 && int.TryParse(parts[0], out var pid) && pid > 0)
                {
                    long ticks = 0;
                    if (parts.Length >= 2)
                    {
                        long.TryParse(parts[1], out ticks);
                    }
                    return (pid, ticks);
                }
            }
        }
        catch { }
        return (-1, 0);
    }

    /// <summary>
    /// 控制器探测的同步包装（启动清理路径使用，带 2s 超时）。
    /// 探测本体经 Task.Run 跑在线程池（无 WPF 同步上下文，不会死锁）；
    /// 调用方线程（可能是 UI 线程）最多被阻塞 2 秒，且仅在第 1/2 级判据都未命中时触发。
    /// </summary>
    private bool ProbeControllerSync()
    {
        try
        {
            var t = Task.Run(() => ProbeController());
            return t.Wait(TimeSpan.FromSeconds(2)) && t.Result;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启动内核（普通模式）。elevated=true 时走 UAC（TUN 需要）。</summary>
    public void Start(bool elevated)
    {
        if (!IsCoreInstalled)
        {
            throw new FileNotFoundException("内核未安装，请在设置中下载内核", _settings.CoreExePath);
        }
        Stop();
        KillStaleInstance();
        if (!_settings.IsTrustedCoreExe(_settings.CoreExePath))
        {
            throw new InvalidOperationException("内核路径不受信任（超出本软件数据目录），已拒绝启动");
        }
        var generation = _generation;

        // sing-box：启动前先做配置校验，把错误提前暴露给界面而不是让内核静默失败
        if (_settings.IsSingBoxCore)
        {
            var check = RunCheck();
            if (check.Length > 0)
            {
                throw new InvalidOperationException($"sing-box 配置校验失败：{check}");
            }
        }

        // 参数统一经 QuoteWin 转义（路径来自用户目录，防注入且兼容含空格路径）；提权场景 Verb=runas 必须用命令行字符串
        var psi = new ProcessStartInfo
        {
            FileName = _settings.CoreExePath,
            Arguments = _settings.IsSingBoxCore
                ? $"run -D {QuoteWin(_settings.HomeDir)} -c {QuoteWin(_settings.SingBoxConfigPath)} --disable-color"
                : $"-d {QuoteWin(_settings.HomeDir)} -f {QuoteWin(_settings.ConfigPath)}",
            UseShellExecute = elevated,
            Verb = elevated ? "runas" : "",
            CreateNoWindow = !elevated,
            RedirectStandardOutput = !elevated,
            RedirectStandardError = !elevated,
            WorkingDirectory = _settings.HomeDir,
        };
        try
        {
            _process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("TUN 模式需要管理员权限，已取消启动");
        }

        _lastStartAt = DateTime.Now;
        if (_process != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PidFilePath)!);
            // pid|启动时间(UTC ticks)：供 KillStaleInstance 做 PID 复用校验（审计修复轮第 8 条）
            File.WriteAllText(PidFilePath, $"{_process.Id}|{_lastStartAt.ToUniversalTime().Ticks}");
        }

        if (!elevated && _process != null)
        {
            var proc = _process;
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => OnExited(generation);
        }
    }

    /// <summary>Windows 命令行参数转义：包裹引号并正确处理内嵌引号与反斜杠（提权启动 UseShellExecute=true 时无法使用 ArgumentList）。</summary>
    private static string QuoteWin(string arg)
    {
        var sb = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                sb.Append('\\');
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes + 1).Append('"');
                backslashes = 0;
                continue;
            }
            backslashes = 0;
            sb.Append(c);
        }
        if (backslashes > 0)
        {
            sb.Append('\\', backslashes);
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>sing-box 配置校验（check 命令）；通过返回空串，失败返回错误摘要。</summary>
    private string RunCheck()
    {
        try
        {
            if (!_settings.IsTrustedCoreExe(_settings.CoreExePath))
            {
                throw new InvalidOperationException("内核路径不受信任");
            }
            var psi = new ProcessStartInfo
            {
                FileName = _settings.CoreExePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("check");
            psi.ArgumentList.Add("-D");
            psi.ArgumentList.Add(_settings.HomeDir);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(_settings.SingBoxConfigPath);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0 ? "" : (err.Length > 0 ? err : "未知错误").Trim().Split('\n').LastOrDefault() ?? "";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    private async void OnExited(int generation)
    {
        // 手动停止/切换模式会杀掉旧进程；只有未被新启动取代的意外退出才自动重启，
        // 否则新旧内核会抢同一端口，形成无限重启。
        if (generation != _generation)
        {
            return;
        }
        // 崩溃自动重启：60 秒内的连续崩溃超过 3 次则放弃，避免死循环
        Log?.Invoke("[shadowrocket] 内核进程退出，正在自动重启…");
        if (DateTime.Now - _lastStartAt < TimeSpan.FromSeconds(60))
        {
            _restartCount++;
        }
        else
        {
            _restartCount = 0;
        }
        if (_restartCount > 3)
        {
            Log?.Invoke("[shadowrocket] 内核连续崩溃超过 3 次，已停止自动重启，请检查订阅配置");
            return;
        }
        await Task.Delay(1500);
        if (generation != _generation)
        {
            return;   // 等待期间已被手动重启
        }
        try
        {
            Start(elevated: false);
        }
        catch (Exception e)
        {
            Log?.Invoke($"[shadowrocket] 重启失败：{e.Message}");
        }
    }

    public void Stop()
    {
        _generation++;   // 让挂起的自动重启失效
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch
        {
        }
        _process = null;
    }

    public async Task<bool> WaitReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            if (await ProbeController())
            {
                return true;
            }
            await Task.Delay(300);
        }
        return false;
    }

    private async Task<bool> ProbeController()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", _settings.Secret);
            using var r = await client.GetAsync($"http://127.0.0.1:{_settings.ControllerPort}/version");
            return r.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
