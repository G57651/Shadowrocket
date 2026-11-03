using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Shadowrocket.Core;
using Shadowrocket.Views;

namespace Shadowrocket;

public partial class App : Application
{
    public static AppRuntime Runtime { get; private set; } = null!;

    public static bool IsDarkTheme { get; private set; }

    private static Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _showWatchCts;
    private static MainWindow? _mainWindow;

    private const string MutexName = "Shadowrocket.Windows.SingleInstance";
    private const string ShowEventName = "Shadowrocket.Windows.ShowEvent";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：两个 GUI 会各自启动一个内核并互相 KillStaleInstance 清理对方，
        // 结果是内核被反复杀掉重启，端口时通时断。
        // 第二实例不弹提示，直接唤起已有实例的主窗口后退出。
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // 第二实例若带着机场的 scheme 订阅链接（clash:// 等），转交给首实例导入
            var handoff = e.Args.Select(ParseSchemeImport).FirstOrDefault(r => r != null);
            if (handoff != null)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Shadowrocket.Core.AppSettings.Dir, "pending-import.txt"),
                        e.Args.First(a => ParseSchemeImport(a) != null));
                }
                catch { }
            }
            NotifyExistingInstance();
            Shutdown();
            return;
        }

        RegisterUrlSchemes();

        // 全局异常兜底：任何未处理异常写 crash.log（AppData\Roaming\Shadowrocket），避免无日志闪退。
        // UI 线程异常尽量标记已处理保住进程；后台/任务异常只记录。
        DispatcherUnhandledException += (_, e) =>
        {
            WriteCrashLog("UI", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("APPDOMAIN", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("TASK", e.Exception);
            e.SetObserved();
        };

        // 之后弹出的所有窗口（导入对话框、组详情等）加载时也自动适配标题栏主题
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((_, _) => ApplyNativeTitleBar()));

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        Runtime = new AppRuntime();
        Runtime.Log += m => System.Diagnostics.Debug.WriteLine(m);

        // 注销/关机时不保证走完 OnExit：先把指向本端口的系统代理摘掉再交给系统，避免遗留断网
        SessionEnding += (_, _) =>
        {
            try
            {
                if (Shadowrocket.Core.SystemProxyService.IsSet(Runtime.Settings.MixedPort))
                {
                    Shadowrocket.Core.SystemProxyService.Disable(Runtime.Settings, Runtime.Settings.MixedPort);
                }
            }
            catch { }
        };

        // 首次开启 TUN：弹窗说明要下载什么，用户确认后才真正下载
        Runtime.ConfirmWintunDownload = async () => await Dispatcher.InvokeAsync(() =>
            MessageBox.Show(
                "首次开启 TUN 模式，需要先下载 wintun 虚拟网卡驱动：\n\n" +
                "· 下载内容：wintun.dll（约 0.5 MB，来自 wintun.net 官方构建）\n" +
                "· 用途：创建 TUN 虚拟网卡，接管系统全局流量\n" +
                "· 存放位置：自动保存到本软件数据目录，无需手动安装\n\n" +
                "是否现在下载？",
                "开启 TUN 模式", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

        // 检测到 sing-box 格式订阅等场景：询问是否切换内核
        Runtime.ConfirmCoreSwitch = async msg => await Dispatcher.InvokeAsync(() =>
            MessageBox.Show(msg, "切换内核", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

        // 内核完整性校验无官方校验和时的安装确认（共识修复第 7 条）
        Runtime.ConfirmKernelInstall = async msg => await Dispatcher.InvokeAsync(() =>
            MessageBox.Show(msg, "内核完整性校验", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);

        ApplyTheme(Runtime.Settings.Theme);

        await Runtime.StartAsync();
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // 主窗口显式创建（StartupUri 在 Shutdown 后仍会创建窗口，
        // 会让第二实例在 Runtime=null 的状态下构造 MainWindow 而崩溃）
        _mainWindow = new MainWindow();
        _mainWindow.Show();
        ApplyNativeTitleBar();

        // 状态文件损坏的可见告警：加载期发现的问题必须让用户知道，不得静默吞掉
        foreach (var issue in new[] { Runtime.Settings.LoadIssue, Runtime.Profiles.LoadIssue })
        {
            if (issue != null)
            {
                MessageBox.Show(_mainWindow, issue, "配置文件异常", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        StartShowWatchLoop();

        // 首实例冷启动时若带 scheme 订阅链接（浏览器导入），启动完成后按用户选择处理
        var startupArg = e.Args.FirstOrDefault(a => ParseSchemeImport(a) != null);
        if (startupArg != null)
        {
            HandleSchemeImport(startupArg);
        }
    }

    /// <summary>本软件作为候选注册的 URL Scheme：网页"一键导入"时 Windows 弹出应用选择框，
    /// 由用户决定交给哪个客户端（勾选"始终"即固定默认，可到设置页重置后重新选）。</summary>
    private static readonly string[] KnownSchemes =
        ["clash://", "clashmeta://", "clash-verge://", "sing-box://", "singbox://", "hiddify://", "shadowrocket://"];

    /// <summary>注册 URL Scheme 到 HKCU（无需管理员；每次启动幂等刷新）。</summary>
    private static void RegisterUrlSchemes()
    {
        try
        {
            foreach (var scheme in KnownSchemes)
            {
                var name = scheme[..^3];   // 去掉 "://"
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{name}");
                key.SetValue("", $"URL:Shadowrocket 订阅导入 ({name})");
                key.SetValue("URL Protocol", "");
                using var cmd = key.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", $"\"{Environment.ProcessPath}\" \"%1\"");
            }
            // 一次性重置默认打开方式：这些协议的 UserChoice 此前固定成了某个客户端，
            // 清掉后 Windows 才会在下次一键导入时弹出选择框，把选择权交还用户
            try
            {
                var s = AppSettings.Load();
                if (!s.SchemeChoiceResetDone)
                {
                    ResetImportSchemeUserChoice();
                    s.SchemeChoiceResetDone = true;
                    s.Save();
                }
            }
            catch
            {
            }
        }
        catch
        {
            // 注册失败不影响主流程（如目录权限），只是机场网页按钮无法唤起
        }
    }

    /// <summary>清除各导入协议的系统默认打开方式（UserChoice）：
    /// 下次网页一键导入时 Windows 重新弹出"选择打开方式"，用户可当场选应用或勾选"始终"。</summary>
    internal static void ResetImportSchemeUserChoice()
    {
        foreach (var scheme in KnownSchemes)
        {
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                    $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme[..^3]}\UserChoice",
                    throwOnMissingSubKey: false);
            }
            catch
            {
                // 删除失败不影响主流程
            }
        }
    }

    /// <summary>
    /// 解析机场网页"导入到 XXX"按钮唤起的 scheme 链接，提取订阅地址与备注名：
    /// clash://install-config?url=X&amp;name=N · clash-verge://install-config?uri=X&amp;name=N ·
    /// sing-box://import-remote-profile?url=X#N · hiddify://import/X
    /// </summary>
    public static (string Url, string? Name)? ParseSchemeImport(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return null;
        }
        var lower = arg.ToLowerInvariant();
        var scheme = KnownSchemes.FirstOrDefault(s => lower.StartsWith(s));
        if (scheme == null)
        {
            return null;
        }
        var rest = arg[scheme.Length..];

        string? url = null;
        string? name = null;

        // 形如 ...?url=X&name=N 或 ...?url=X#N
        var qIdx = rest.IndexOf('?', StringComparison.Ordinal);
        if (qIdx >= 0)
        {
            var qs = rest[(qIdx + 1)..];
            var hash = qs.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0 && name == null)
            {
                name = Uri.UnescapeDataString(qs[(hash + 1)..]).Trim();
                qs = qs[..hash];
            }
            foreach (var kv in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = kv.Split('=', 2);
                if (p.Length != 2)
                {
                    continue;
                }
                if (p[0].Equals("url", StringComparison.OrdinalIgnoreCase) ||
                    p[0].Equals("uri", StringComparison.OrdinalIgnoreCase))   // Clash Verge 用 uri=
                {
                    url = Uri.UnescapeDataString(p[1].Trim());
                }
                else if (p[0].Equals("name", StringComparison.OrdinalIgnoreCase) && name == null)
                {
                    // 机场按钮的标准形态：clash://install-config?url=X&name=N
                    name = Uri.UnescapeDataString(p[1].Trim());
                }
                if (url != null && name != null)
                {
                    break;
                }
            }
        }

        // hiddify://import/<url> 等路径形态
        if (url == null)
        {
            var path = rest;
            foreach (var pre in new[] { "install-config/", "import-remote-profile/", "import/" })
            {
                if (path.StartsWith(pre, StringComparison.OrdinalIgnoreCase))
                {
                    path = path[pre.Length..];
                    break;
                }
            }
            if (path.Length > 0)
            {
                url = Uri.UnescapeDataString(path.Trim());
            }
        }

        if (url == null || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return (url, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }

    /// <summary>
    /// 一键导入链接的总入口：有记忆则直接执行，否则弹窗询问接收方。
    /// "导入到本软件" = 正常导入；"转发给 XXX" = 把链接原样交给系统里该协议的真实客户端。
    /// </summary>
    private static void HandleSchemeImport(string rawArg)
    {
        var parsed = ParseSchemeImport(rawArg);
        if (parsed == null)
        {
            return;
        }
        var schemeFull = KnownSchemes.FirstOrDefault(s => rawArg.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        if (schemeFull == null)
        {
            return;
        }
        var scheme = schemeFull[..^3];

        var choice = Runtime.Settings.ImportHandoff.GetValueOrDefault(scheme, "");
        if (choice is "self" or "pass")
        {
            // 共识修复第 10 条 + 审计修复轮第 9 条：零确认路径彻底消除。
            // "记住"仅是弹窗的预选提示——导入本软件与转发外部客户端都必须用户当面确认一次才执行
            var rememberedExternal = FindExternalHandler(scheme);
            if (choice == "pass" && rememberedExternal == null)
            {
                // 记住的处理器已失效（客户端卸载等）：清掉记忆，回退为每次询问
                Runtime.Settings.ImportHandoff.Remove(scheme);
                Runtime.Settings.Save();
            }
            var hint = choice == "self" || rememberedExternal == null
                ? "此前记住了「导入到本软件」。导入会更改全部代理节点，请再次确认。"
                : $"此前记住了「转发给 {rememberedExternal.Value.Label}」。请再次确认。";
            var rememberedDialog = new Views.SchemeImportDialog(rawArg, rememberedExternal?.Label,
                rememberedHint: hint);
            if (_mainWindow != null)
            {
                rememberedDialog.Owner = _mainWindow;
            }
            rememberedDialog.ShowDialog();
            if (rememberedDialog.DialogResult == true)
            {
                if (rememberedDialog.ChoseExternal && rememberedExternal != null && LaunchExternalHandler(scheme, rawArg))
                {
                    return;
                }
                if (!rememberedDialog.ChoseExternal)
                {
                    ProcessSchemeImport(parsed.Value.Url, parsed.Value.Name);
                }
            }
            return;
        }

        var external = FindExternalHandler(scheme);
        var dialog = new Views.SchemeImportDialog(rawArg, external?.Label);
        if (_mainWindow != null)
        {
            dialog.Owner = _mainWindow;
        }
        dialog.ShowDialog();
        if (dialog.DialogResult != true)
        {
            return;
        }
        if (dialog.RememberChoice)
        {
            Runtime.Settings.ImportHandoff[scheme] = dialog.ChoseExternal ? "pass" : "self";
            Runtime.Settings.Save();
        }
        if (dialog.ChoseExternal && external != null && LaunchExternalHandler(scheme, rawArg))
        {
            return;
        }
        if (dialog.ChoseExternal)
        {
            MessageBox.Show("没有找到可转发的客户端处理器，已改为导入到本软件。", "一键导入",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ProcessSchemeImport(parsed.Value.Url, parsed.Value.Name);
    }

    /// <summary>找某协议的"真实"系统处理器（可能是被本软件用户级注册覆盖前的原客户端）：
    /// 先查 HKLM 全局注册，再查 HKCU，跳过指向本软件的命令。</summary>
    private static (string Label, string Command)? FindExternalHandler(string scheme)
    {
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey($@"Software\Classes\{scheme}\shell\open\command");
                if (key?.GetValue(null) is not string cmd || string.IsNullOrWhiteSpace(cmd))
                {
                    continue;
                }
                if (Environment.ProcessPath != null &&
                    cmd.Contains(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return (HandlerLabel(cmd), cmd);
            }
            catch
            {
            }
        }
        return null;
    }

    /// <summary>从命令模板提取可读名称："D:\Program Files\Clash Verge\clash-verge.exe" → "Clash Verge"。</summary>
    private static string HandlerLabel(string command)
    {
        var exe = command.TrimStart();
        if (exe.StartsWith('"'))
        {
            var end = exe.IndexOf('"', 1);
            exe = end > 0 ? exe[1..end] : exe.Trim('"');
        }
        else
        {
            var sp = exe.IndexOf(' ');
            if (sp > 0)
            {
                exe = exe[..sp];
            }
        }
        var name = Path.GetFileNameWithoutExtension(exe).Replace('-', ' ').Replace('_', ' ');
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name);
    }

    /// <summary>把链接转交给该协议的真实处理器：命令模板里的 %1 换成完整 scheme 链接后启动。</summary>
    private static bool LaunchExternalHandler(string scheme, string rawArg)
    {
        var found = FindExternalHandler(scheme);
        if (found == null)
        {
            return false;
        }
        try
        {
            var cmd = found.Value.Command.TrimStart();
            string exe, args;
            if (cmd.StartsWith('"'))
            {
                var end = cmd.IndexOf('"', 1);
                if (end < 0)
                {
                    return false;
                }
                exe = cmd[1..end];
                args = cmd[(end + 1)..].TrimStart();
            }
            else
            {
                var sp = cmd.IndexOf(' ');
                if (sp < 0)
                {
                    exe = cmd;
                    args = "";
                }
                else
                {
                    exe = cmd[..sp];
                    args = cmd[(sp + 1)..].TrimStart();
                }
            }
            // rawArg 来自网页 scheme 链接（外部输入）：中和引号与控制字符，防止破坏目标程序命令行
            var safe = rawArg.Replace("\"", "%22").Replace("\r", "").Replace("\n", "").Replace("\0", "");
            var quoted = $"\"{safe}\"";
            args = args.Contains("%1", StringComparison.Ordinal)
                ? args.Replace("%1", quoted)
                : args.Length > 0 ? args + " " + quoted : quoted;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,
            });
            _mainWindow?.ShowTrayTip("已转交一键导入", $"链接已交给 {found.Value.Label} 处理");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 scheme 导入的订阅直接导入本软件（后台下载解析，结果用托盘气泡/弹窗反馈）。</summary>
    private static void ProcessSchemeImport(string url, string? name)
    {
        var app = Current;
        var runtime = Runtime;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var item = await runtime.AddProfileAsync(name ?? "", url, null);
                app?.Dispatcher.Invoke(() =>
                {
                    _mainWindow?.ShowTrayTip("订阅导入成功", $"{item.Name} · 已添加到软件");
                    runtime.NotifyNodesChanged();
                });
            }
            catch (Exception ex)
            {
                app?.Dispatcher.Invoke(() =>
                    MessageBox.Show(ex.Message, "订阅导入失败", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        });
    }

    /// <summary>
    /// 后台等待"显示主窗口"信号：第二实例无论从哪里启动，
    /// 都会让这里把已有主窗口拉到前台，自己随即退出。
    /// </summary>
    private void StartShowWatchLoop()
    {
        _showWatchCts = new CancellationTokenSource();
        var handles = new WaitHandle[] { _showEvent!, _showWatchCts.Token.WaitHandle };
        Task.Run(async () =>
        {
            while (!(_showWatchCts?.IsCancellationRequested ?? true))
            {
                var triggered = await Task.Run(() => WaitHandle.WaitAny(handles));
                if (triggered != 0)
                {
                    break;   // 退出信号
                }
                Current?.Dispatcher.Invoke(() =>
                {
                    _mainWindow?.ShowFromTray();
                    ProcessPendingImport();
                });
            }
        });
    }

    /// <summary>第二实例转交的订阅链接（pending-import.txt），由首实例读取并直接导入。</summary>
    private static void ProcessPendingImport()
    {
        try
        {
            var file = System.IO.Path.Combine(Shadowrocket.Core.AppSettings.Dir, "pending-import.txt");
            if (!System.IO.File.Exists(file))
            {
                return;
            }
            var arg = System.IO.File.ReadAllText(file).Trim();
            System.IO.File.Delete(file);
            HandleSchemeImport(arg);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 第二实例：发信号让首实例恢复主窗口；第二实例刚被用户双击、持有前台权限，
    /// 由它直接把首实例的主窗口拉到最前最可靠。首实例尚未就绪时短暂重试。
    /// </summary>
    private static void NotifyExistingInstance()
    {
        Process? first = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
                first = Process.GetProcessesByName("Shadowrocket")
                    .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
                break;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 首实例还在启动中，事件尚未创建
                Thread.Sleep(400);
            }
            catch
            {
                return;
            }
        }
        if (first == null)
        {
            return;
        }
        try
        {
            ShowWindowAsync(first.MainWindowHandle, SW_RESTORE);
            SetForegroundWindow(first.MainWindowHandle);
        }
        catch
        {
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>把未处理异常落盘到 crash.log，便于事后定位闪退原因。</summary>
    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {ex}\r\n\r\n";
            File.AppendAllText(Path.Combine(Shadowrocket.Core.AppSettings.Dir, "crash.log"), line);
        }
        catch
        {
        }
    }

    /// <summary>浅/深主题切换：替换令牌字典（ui-spec §1.2），并同步原生标题栏颜色。</summary>
    public void ApplyTheme(string theme)
    {
        IsDarkTheme = theme == "dark";
        var uri = new Uri($"Themes/Tokens.{(IsDarkTheme ? "Dark" : "Light")}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        var merged = Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("Tokens.") == true);
        if (old != null)
        {
            var index = merged.IndexOf(old);
            merged[index] = dict;
        }
        else
        {
            merged.Insert(0, dict);
        }
        ApplyNativeTitleBar();
    }

    /// <summary>原生标题栏跟随主题：深色 → 近黑标题栏，浅色 → 浅灰，颜色取自当前令牌
    /// （Win10 用系统深色模式属性，Win11 精确上色，见 NativeTitleBar）。</summary>
    private void ApplyNativeTitleBar()
    {
        if (TryGetColor("ColorPageBg", out var bg) && TryGetColor("ColorTextPrimary", out var text))
        {
            foreach (var win in Windows.OfType<Window>())
            {
                NativeTitleBar.Apply(win, IsDarkTheme, bg, bg, text);
            }
        }
    }

    private static bool TryGetColor(string key, out Color color)
    {
        if (Current?.TryFindResource(key) is Color c)
        {
            color = c;
            return true;
        }
        color = default;
        return false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWatchCts?.Cancel();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        _showEvent?.Dispose();
        Runtime?.Dispose();
        base.OnExit(e);
    }
}
