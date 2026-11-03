using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Shadowrocket.Core;

namespace Shadowrocket.ViewModels;

/// <summary>设置页：所有开关即时生效并持久化；内核更新走 GitHub release。</summary>
public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly AppRuntime _rt;
    private readonly KernelUpdater _kernel;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsViewModel(AppRuntime rt)
    {
        _rt = rt;
        _kernel = new KernelUpdater(rt.Settings);
        // 完整性校验的用户确认复用 AppRuntime 已接线的界面委托（共识修复第 7 条）
        _kernel.ConfirmUnverifiedInstall = rt.Kernel.ConfirmUnverifiedInstall;
        ApplyPortCommand = new RelayCommand<object>(_ => ApplyPort());
        ApplyLatencyCommand = new RelayCommand<object>(_ => ApplyLatency());
        NetworkOptionChanged = new RelayCommand<object>(_ =>
        {
            _rt.ApplyNetworkOptions();
            MessageBox.Show("网络选项已应用，内核已重启。", "设置", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        CheckUpdateCommand = new RelayCommand<object>(async _ => await CheckUpdateAsync());
        UpdateKernelCommand = new RelayCommand<object>(async _ => await UpdateKernelAsync());
        DownloadKernelCommand = new RelayCommand<object>(async _ => await UpdateKernelAsync());
        OpenFolderCommand = new RelayCommand<object>(_ => System.Diagnostics.Process.Start("explorer.exe", AppSettings.Dir));
        ResetImportChoiceCommand = new RelayCommand<object>(_ =>
        {
            _rt.Settings.ImportHandoff.Clear();
            _rt.Settings.Save();
            MessageBox.Show(
                "已清除记住的一键导入接收方。\n\n下次在网页点击「一键导入 / 一键订阅」时会重新弹窗询问：\n" +
                "· 导入到本软件，或转发给系统里的其他客户端（如 Clash Verge）\n" +
                "· 勾选\"记住我的选择\"后，下次弹窗会预选该接收方，但导入前仍需确认一次（安全要求），随时回到这里清除",
                "一键导入", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        ResetCommand = new RelayCommand<object>(_ => Reset());
        AutoStartChanged = new RelayCommand<object>(_ =>
        {
            var s = _rt.Settings;
            if (s.AutoStart)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)!;
                key.SetValue("Shadowrocket", $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue("Shadowrocket", throwOnMissingValue: false);
            }
            s.Save();   // 开关本身也要落盘，否则下次启动回到旧值
        });
        ThemeChanged = new RelayCommand<string>(t =>
        {
            if (t == null)
            {
                return;
            }
            _rt.Settings.Theme = t;
            _rt.Settings.Save();
            OnPropertyChanged(nameof(IsDark));
            OnPropertyChanged(nameof(IsLight));
            OnPropertyChanged(nameof(ThemeLightLabel));
            OnPropertyChanged(nameof(ThemeDarkLabel));
            (Application.Current as App)?.ApplyTheme(t);
        });
        CloseToTrayChanged = new RelayCommand<object>(_ => _rt.Settings.Save());
        ApplyUserAgentCommand = new RelayCommand<object>(_ => ApplyUserAgent());
        AutoSpeedTestChanged = new RelayCommand<object>(_ => _rt.Settings.Save());
        ApplyAutoSpeedTestCommand = new RelayCommand<object>(_ => ApplyAutoSpeedTestInterval());
        AutoSpeedTestUnitChanged = new RelayCommand<string>(k =>
        {
            if (k is not ("minute" or "second") || _rt.Settings.AutoSpeedTestUnit == k)
            {
                return;
            }
            // 切换单位时换算草稿值保持语义连续（30 分钟 ↔ 1800 秒），点"应用"后才落盘数值
            _rt.Settings.AutoSpeedTestUnit = k;
            _autoSpeedTestIntervalDraft = k == "second"
                ? Math.Max(30, _autoSpeedTestIntervalDraft * 60)
                : Math.Max(1, (int)Math.Round(_autoSpeedTestIntervalDraft / 60.0));
            _rt.Settings.Save();
            OnPropertyChanged(nameof(AutoSpeedTestIntervalText));
            OnPropertyChanged(nameof(AutoSpeedTestMinuteLabel));
            OnPropertyChanged(nameof(AutoSpeedTestSecondLabel));
        });
        AutoLatencyTestChanged = new RelayCommand<object>(_ => _rt.Settings.Save());
        ApplyAutoLatencyTestCommand = new RelayCommand<object>(_ => ApplyAutoLatencyTestInterval());
        AutoLatencyTestUnitChanged = new RelayCommand<string>(k =>
        {
            if (k is not ("minute" or "second") || _rt.Settings.AutoLatencyTestUnit == k)
            {
                return;
            }
            _rt.Settings.AutoLatencyTestUnit = k;
            _autoLatencyTestIntervalDraft = k == "second"
                ? Math.Max(30, _autoLatencyTestIntervalDraft * 60)
                : Math.Max(1, (int)Math.Round(_autoLatencyTestIntervalDraft / 60.0));
            _rt.Settings.Save();
            OnPropertyChanged(nameof(AutoLatencyTestIntervalText));
            OnPropertyChanged(nameof(AutoLatencyTestMinuteLabel));
            OnPropertyChanged(nameof(AutoLatencyTestSecondLabel));
        });
        NodeSortChanged = new RelayCommand<string>(k =>
        {
            if (k == null)
            {
                return;
            }
            _rt.Settings.NodeSortMode = k;
            _rt.Settings.Save();
            _rt.NotifyNodeSortChanged();
            OnPropertyChanged(nameof(NodeSortDefaultLabel));
            OnPropertyChanged(nameof(NodeSortNameLabel));
            OnPropertyChanged(nameof(NodeSortLatencyLabel));
            OnPropertyChanged(nameof(NodeSortSpeedLabel));
        });
        SpeedIntervalChanged = new RelayCommand<string>(k =>
        {
            if (!int.TryParse(k, out var s))
            {
                return;
            }
            _rt.Settings.SpeedIntervalSeconds = s;
            _rt.Settings.Save();
            _rt.NotifySpeedIntervalChanged();
            OnPropertyChanged(nameof(SpeedInterval1Label));
            OnPropertyChanged(nameof(SpeedInterval5Label));
            OnPropertyChanged(nameof(SpeedInterval10Label));
            OnPropertyChanged(nameof(SpeedInterval30Label));
        });
        RuleModeChanged = new RelayCommand<string>(k =>
        {
            if (k == null)
            {
                return;
            }
            try
            {
                _rt.SetRuleMode(k);
                var msg = $"分流规则已切换为「{RuleModeName(k)}」，内核已重启。";
                if (k == "airport" && _rt.Settings.IsSingBoxCore)
                {
                    // sing-box 暂不支持机场规则提取：必须让用户知道分流已按智能分流（standard）降级，不得只写日志
                    msg += "\n\n注意：sing-box 内核暂不支持机场自带规则提取，当前已按「智能分流」规则运行（机场规则未生效）。";
                }
                MessageBox.Show(msg, "分流规则",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "分流规则", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            OnPropertyChanged(nameof(RuleModeStandardLabel));
            OnPropertyChanged(nameof(RuleModeAirportLabel));
            OnPropertyChanged(nameof(RuleModeMinimalLabel));
        });
        KernelKindChanged = new RelayCommand<string>(k =>
        {
            if (k == null)
            {
                return;
            }
            try
            {
                _rt.SetCoreKind(k);
                var msg = $"已切换到 {KernelName(k)} 内核并重启。";
                if (k == "singbox" && _rt.Settings.RuleMode == "airport")
                {
                    // 机场规则模式切到 sing-box：分流会降级为智能分流，必须让用户知道
                    msg += "\n\n注意：sing-box 内核暂不支持机场自带规则提取，当前已按「智能分流」规则运行（机场规则未生效）。";
                }
                MessageBox.Show(msg, "代理内核", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "切换内核", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            OnPropertyChanged(nameof(IsMihomoCore));
            OnPropertyChanged(nameof(KernelMihomoLabel));
            OnPropertyChanged(nameof(KernelSingBoxLabel));
            OnPropertyChanged(nameof(KernelDisplayName));
            OnPropertyChanged(nameof(KernelStatusText));
            OnPropertyChanged(nameof(OtherKernelNameText));
            OnPropertyChanged(nameof(OtherKernelStatusText));
            OnPropertyChanged(nameof(OtherKernelButtonText));
            OnPropertyChanged(nameof(HasStableChannel));
            OnPropertyChanged(nameof(HasAlphaChannel));
            RefreshCoreInfo();
        });
        DownloadSingBoxCommand = new RelayCommand<object>(async _ => await DownloadSingBoxAsync());
        DownloadOtherKernelCommand = new RelayCommand<object>(async _ =>
        {
            if (IsMihomoCore)
            {
                await DownloadSingBoxAsync();
            }
            else
            {
                await DownloadMihomoAsync();
            }
        });
        CoreChannelChanged = new RelayCommand<string>(c =>
        {
            _rt.Settings.CoreChannel = c ?? "stable";
            _rt.Settings.Save();
            OnPropertyChanged(nameof(IsStableChannel));
            OnPropertyChanged(nameof(IsAlphaChannel));
            OnPropertyChanged(nameof(ChannelStableLabel));
            OnPropertyChanged(nameof(ChannelAlphaLabel));
        });
        RefreshCoreInfo();
    }

    private static string KernelName(string kind) => kind == "singbox" ? "sing-box" : "mihomo";

    private async Task DownloadSingBoxAsync()
    {
        if (IsDownloadingCore)
        {
            return;
        }
        try
        {
            IsDownloadingCore = true;
            OnPropertyChanged(nameof(IsDownloadingCore));
            UpdateStatusText = "正在下载 sing-box 内核…";
            OnPropertyChanged(nameof(UpdateStatusText));
            var rel = await _kernel.GetLatestSingBoxAsync();
            if (rel is null)
            {
                UpdateStatusText = "没有找到 sing-box 的 Windows 版本";
                OnPropertyChanged(nameof(UpdateStatusText));
                return;
            }
            var tag = await _kernel.InstallSingBoxAsync(rel, p =>
            {
                UpdateProgress = p;
                UpdateRemainder = 1 - p;
                OnPropertyChanged(nameof(UpdateProgress));
                OnPropertyChanged(nameof(UpdateRemainder));
            }, _rt.Core);
            UpdateStatusText = $"sing-box 已更新到 {tag}";
            _rt.NotifyCoreRestarted();
        }
        catch (Exception e)
        {
            UpdateStatusText = $"sing-box 下载失败：{e.Message}";
        }
        finally
        {
            IsDownloadingCore = false;
            OnPropertyChanged(nameof(IsDownloadingCore));
            OnPropertyChanged(nameof(UpdateStatusText));
            OnPropertyChanged(nameof(KernelStatusText));
        }
    }

    private async Task DownloadMihomoAsync()
    {
        if (IsDownloadingCore)
        {
            return;
        }
        try
        {
            IsDownloadingCore = true;
            OnPropertyChanged(nameof(IsDownloadingCore));
            UpdateStatusText = "正在下载 mihomo 内核…";
            OnPropertyChanged(nameof(UpdateStatusText));
            var rel = await _kernel.GetLatestAsync();
            if (rel is null)
            {
                UpdateStatusText = "没有找到 mihomo 的 Windows 版本";
                OnPropertyChanged(nameof(UpdateStatusText));
                return;
            }
            var tag = await _kernel.InstallOrUpdateAsync(rel, p =>
            {
                UpdateProgress = p;
                UpdateRemainder = 1 - p;
                OnPropertyChanged(nameof(UpdateProgress));
                OnPropertyChanged(nameof(UpdateRemainder));
            }, _rt.Settings.IsSingBoxCore ? null : _rt.Core);
            UpdateStatusText = $"mihomo 已更新到 {tag}";
            _rt.NotifyCoreRestarted();
        }
        catch (Exception e)
        {
            UpdateStatusText = $"mihomo 下载失败：{e.Message}";
        }
        finally
        {
            IsDownloadingCore = false;
            OnPropertyChanged(nameof(IsDownloadingCore));
            OnPropertyChanged(nameof(UpdateStatusText));
            RefreshCoreInfo();
        }
    }

    public ICommand ApplyPortCommand { get; }
    public ICommand ApplyLatencyCommand { get; }
    public ICommand NetworkOptionChanged { get; }
    public ICommand CloseToTrayChanged { get; }
    public ICommand ApplyUserAgentCommand { get; }
    public ICommand AutoSpeedTestChanged { get; }
    public ICommand ApplyAutoSpeedTestCommand { get; }
    public ICommand AutoSpeedTestUnitChanged { get; }
    public ICommand AutoLatencyTestChanged { get; }
    public ICommand ApplyAutoLatencyTestCommand { get; }
    public ICommand AutoLatencyTestUnitChanged { get; }
    public ICommand NodeSortChanged { get; }
    public ICommand SpeedIntervalChanged { get; }
    public ICommand KernelKindChanged { get; }
    public ICommand DownloadSingBoxCommand { get; }
    public ICommand DownloadOtherKernelCommand { get; }
    public ICommand RuleModeChanged { get; }

    private static string RuleModeName(string mode) => mode switch
    {
        "airport" => "机场规则",
        "minimal" => "全部代理",
        _ => "智能分流",
    };

    /// <summary>分流规则三选一（✓ 标记当前项）。</summary>
    public string RuleModeStandardLabel => _rt.Settings.RuleMode == "standard" ? "✓ 智能分流" : "智能分流";
    public string RuleModeAirportLabel => _rt.Settings.RuleMode == "airport" ? "✓ 机场规则" : "机场规则";
    public string RuleModeMinimalLabel => _rt.Settings.RuleMode == "minimal" ? "✓ 全部代理" : "全部代理";

    /// <summary>节点排序四选一（✓ 标记当前项）。</summary>
    public string NodeSortDefaultLabel => _rt.Settings.NodeSortMode == "default" ? "✓ 默认" : "默认";
    public string NodeSortNameLabel => _rt.Settings.NodeSortMode == "name" ? "✓ 名称" : "名称";
    public string NodeSortLatencyLabel => _rt.Settings.NodeSortMode == "latency" ? "✓ 延迟" : "延迟";
    public string NodeSortSpeedLabel => _rt.Settings.NodeSortMode == "speed" ? "✓ 速度" : "速度";

    /// <summary>实时速率刷新间隔四选一（✓ 标记当前项）。</summary>
    public string SpeedInterval1Label => _rt.Settings.SpeedIntervalSeconds == 1 ? "✓ 1 秒" : "1 秒";
    public string SpeedInterval5Label => _rt.Settings.SpeedIntervalSeconds == 5 ? "✓ 5 秒" : "5 秒";
    public string SpeedInterval10Label => _rt.Settings.SpeedIntervalSeconds == 10 ? "✓ 10 秒" : "10 秒";
    public string SpeedInterval30Label => _rt.Settings.SpeedIntervalSeconds == 30 ? "✓ 30 秒" : "30 秒";

    /// <summary>sing-box 内核下载进行中。</summary>
    public bool IsDownloadingCore { get; private set; }

    public bool IsMihomoCore => !_rt.Settings.IsSingBoxCore;
    public string KernelMihomoLabel => IsMihomoCore ? "✓ mihomo" : "mihomo";
    public string KernelSingBoxLabel => IsMihomoCore ? "sing-box" : "✓ sing-box";
    public string SingBoxStatusText => _kernel.InstalledSingBoxVersion.Length > 0
        ? $"已安装 · {_kernel.InstalledSingBoxVersion}"
        : "未安装，点击下载";

    /// <summary>未选中内核的下载入口（sing-box 选中时提供 mihomo 下载，反之亦然）。</summary>
    public string MihomoStatusText => _kernel.InstalledMihomoVersion.Length > 0
        ? $"已安装 · {_kernel.InstalledMihomoVersion}"
        : "未安装，点击下载";

    /// <summary>「更新」卡第二行：始终展示另一个内核的安装状态与下载按钮，布局不随所选内核变化。</summary>
    public string OtherKernelNameText => IsMihomoCore ? "sing-box 内核" : "mihomo 内核";
    public string OtherKernelStatusText => IsMihomoCore ? SingBoxStatusText : MihomoStatusText;
    public string OtherKernelButtonText => IsMihomoCore ? "下载 sing-box" : "下载 mihomo";

    /// <summary>内核通道可用性：两个内核目前都有稳定版与 Alpha，按内核声明，缺失的按钮会被隐藏。</summary>
    public bool HasStableChannel => true;
    public bool HasAlphaChannel => true;
    public ICommand CheckUpdateCommand { get; }
    public ICommand UpdateKernelCommand { get; }
    public ICommand DownloadKernelCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ResetImportChoiceCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand AutoStartChanged { get; }
    public ICommand ThemeChanged { get; }
    public ICommand CoreChannelChanged { get; }

    public AppSettings S => _rt.Settings;

    public bool IsDark => _rt.Settings.Theme == "dark";
    public bool IsLight => !IsDark;

    /// <summary>主题/通道按钮的选中态（用 ✓ 前缀，与日志级别按钮一致）。</summary>
    public string ThemeLightLabel => IsLight ? "✓ 浅色" : "浅色";
    public string ThemeDarkLabel => IsDark ? "✓ 深色" : "深色";
    public string ChannelStableLabel => IsStableChannel ? "✓ 稳定版" : "稳定版";
    public string ChannelAlphaLabel => IsStableChannel ? "Alpha" : "✓ Alpha";
    public bool IsStableChannel => _rt.Settings.CoreChannel == "stable";
    public bool IsAlphaChannel => !IsStableChannel;

    public string PortText
    {
        get => _rt.Settings.MixedPort.ToString();
        set { if (int.TryParse(value, out var p)) _portDraft = p; }
    }

    private int _portDraft;

    /// <summary>自动测速间隔输入草稿（按当前单位），点"应用"校验并落盘。</summary>
    public string AutoSpeedTestIntervalText
    {
        get => _autoSpeedTestIntervalDraft.ToString();
        set { if (int.TryParse(value, out var v)) _autoSpeedTestIntervalDraft = v; }
    }

    private int _autoSpeedTestIntervalDraft;

    /// <summary>自动测速间隔单位二选一（✓ 标记当前项）。</summary>
    public string AutoSpeedTestMinuteLabel => _rt.Settings.AutoSpeedTestUnit == "minute" ? "✓ 分钟" : "分钟";
    public string AutoSpeedTestSecondLabel => _rt.Settings.AutoSpeedTestUnit == "second" ? "✓ 秒" : "秒";

    /// <summary>自动测延迟间隔（按当前单位）输入草稿，点"应用"校验并落盘。</summary>
    public string AutoLatencyTestIntervalText
    {
        get => _autoLatencyTestIntervalDraft.ToString();
        set { if (int.TryParse(value, out var v)) _autoLatencyTestIntervalDraft = v; }
    }

    private int _autoLatencyTestIntervalDraft;

    /// <summary>自动测延迟间隔单位二选一（✓ 标记当前项）。</summary>
    public string AutoLatencyTestMinuteLabel => _rt.Settings.AutoLatencyTestUnit == "minute" ? "✓ 分钟" : "分钟";
    public string AutoLatencyTestSecondLabel => _rt.Settings.AutoLatencyTestUnit == "second" ? "✓ 秒" : "秒";

    public string CoreVersionText => _rt.Core.CoreVersionText;

    public string KernelStatusText => _rt.IsCoreInstalled
        ? $"已安装 · {CoreVersionText}"
        : "未安装，点击下方按钮下载官方内核";

    public bool IsCoreMissing => !_rt.IsCoreInstalled;

    public string LatestTag { get; private set; } = "";
    public string UpdateStatusText { get; private set; } = "";
    public string UpdateButtonText => IsCoreMissing ? "立即安装" : "更新到最新";
    public double UpdateProgress { get; private set; }
    public double UpdateRemainder { get; private set; } = 1;
    public bool IsUpdating { get; private set; }

    public string DataFolder => AppSettings.Dir;

    public string LatencyUrlText
    {
        get => _rt.Settings.LatencyTestUrl;
        set { if (Uri.TryCreate(value, UriKind.Absolute, out _)) _rt.Settings.LatencyTestUrl = value; }
    }

    public string LatencyTimeoutText
    {
        get => _rt.Settings.LatencyTimeoutMs.ToString();
        set { if (int.TryParse(value, out var t) && t >= 1000) _rt.Settings.LatencyTimeoutMs = t; }
    }

    /// <summary>拉订阅用的 User-Agent：机场常按 UA 下发不同节点列表。</summary>
    public string SubscriptionUserAgentText
    {
        get => _rt.Settings.SubscriptionUserAgent;
        set { _rt.Settings.SubscriptionUserAgent = value.Trim(); }
    }

    private void ApplyUserAgent()
    {
        _rt.Settings.Save();
        _rt.RegenerateConfig();
        MessageBox.Show("User-Agent 已保存。点订阅上的「更新」重新拉取后才会生效。",
            "User-Agent", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ApplyLatency()
    {
        var url = _rt.Settings.LatencyTestUrl;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("测速地址必须是 http:// 或 https:// 开头的完整链接，当前值未保存。", "测速地址",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _rt.Settings.Save();
        _rt.RegenerateConfig();
        MessageBox.Show("测速参数已保存：手动测速立即生效，内核自动选路的探测地址在下次重启内核后生效。",
            "设置", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>关于页"内核"行：显示当前所选内核。</summary>
    public string KernelDisplayName => _rt.Settings.IsSingBoxCore ? "sing-box" : "mihomo (Clash.Meta)";

    /// <summary>关于页版本号：来自程序集信息，不再写死。</summary>
    public string AppVersionText =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v
            ? $"v{v.Major}.{v.Minor}.{v.Build}"
            : "v1.0.0";

    public void RefreshAll()
    {
        RefreshCoreInfo();
        _portDraft = _rt.Settings.MixedPort;
        OnPropertyChanged(nameof(PortText));
        _autoSpeedTestIntervalDraft = _rt.Settings.AutoSpeedTestUnit == "second"
            ? _rt.Settings.AutoSpeedTestIntervalSeconds
            : Math.Max(1, _rt.Settings.AutoSpeedTestIntervalSeconds / 60);
        OnPropertyChanged(nameof(AutoSpeedTestIntervalText));
        _autoLatencyTestIntervalDraft = _rt.Settings.AutoLatencyTestUnit == "second"
            ? _rt.Settings.AutoLatencyTestIntervalSeconds
            : Math.Max(1, _rt.Settings.AutoLatencyTestIntervalSeconds / 60);
        OnPropertyChanged(nameof(AutoLatencyTestIntervalText));
        OnPropertyChanged(nameof(LatencyUrlText));
        OnPropertyChanged(nameof(LatencyTimeoutText));
    }

    private void RefreshCoreInfo()
    {
        OnPropertyChanged(nameof(CoreVersionText));
        OnPropertyChanged(nameof(KernelStatusText));
        OnPropertyChanged(nameof(IsCoreMissing));
        OnPropertyChanged(nameof(MihomoStatusText));
        OnPropertyChanged(nameof(SingBoxStatusText));
        OnPropertyChanged(nameof(OtherKernelStatusText));
    }

    /// <summary>应用自动测延迟间隔：按单位校验后换算成秒落盘（分钟 1-1440，秒 30-86400）。</summary>
    private void ApplyAutoLatencyTestInterval()
    {
        var isMinute = _rt.Settings.AutoLatencyTestUnit == "minute";
        var min = isMinute ? 1 : 30;
        var max = isMinute ? 1440 : 86400;
        if (_autoLatencyTestIntervalDraft < min || _autoLatencyTestIntervalDraft > max)
        {
            MessageBox.Show($"自动测延迟间隔需在 {min}-{max} {(isMinute ? "分钟" : "秒")} 之间。",
                "自动测延迟", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _rt.Settings.AutoLatencyTestIntervalSeconds = isMinute
            ? _autoLatencyTestIntervalDraft * 60
            : _autoLatencyTestIntervalDraft;
        _rt.Settings.Save();
        OnPropertyChanged(nameof(AutoLatencyTestIntervalText));
    }

    /// <summary>应用自动测速间隔：按单位校验后换算成秒落盘（分钟 1-1440，秒 30-86400）。</summary>
    private void ApplyAutoSpeedTestInterval()
    {
        var isMinute = _rt.Settings.AutoSpeedTestUnit == "minute";
        var min = isMinute ? 1 : 30;
        var max = isMinute ? 1440 : 86400;
        if (_autoSpeedTestIntervalDraft < min || _autoSpeedTestIntervalDraft > max)
        {
            MessageBox.Show($"自动测速间隔需在 {min}-{max} {(_rt.Settings.AutoSpeedTestUnit == "minute" ? "分钟" : "秒")} 之间。",
                "自动测速", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _rt.Settings.AutoSpeedTestIntervalSeconds = isMinute
            ? _autoSpeedTestIntervalDraft * 60
            : _autoSpeedTestIntervalDraft;
        _rt.Settings.Save();
        OnPropertyChanged(nameof(AutoSpeedTestIntervalText));
    }

    private void ApplyPort()
    {
        var p = _portDraft > 0 ? _portDraft : _rt.Settings.MixedPort;
        try
        {
            _rt.SetPort(p);
            MessageBox.Show($"混合端口已改为 {p}，内核已重启。", "端口", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "端口不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
            _portDraft = _rt.Settings.MixedPort;
            OnPropertyChanged(nameof(PortText));
        }
    }

    private async Task CheckUpdateAsync()
    {
        try
        {
            var kernelName = _rt.Settings.IsSingBoxCore ? "sing-box" : "mihomo";
            UpdateStatusText = $"正在检查 {kernelName} 内核更新…";
            OnPropertyChanged(nameof(UpdateStatusText));

            string rel_tag;
            string local;
            if (_rt.Settings.IsSingBoxCore)
            {
                var rel = await _kernel.GetLatestSingBoxAsync();
                rel_tag = rel?.Tag ?? "";
                LatestTag = rel_tag;
                local = _kernel.InstalledSingBoxVersion;
            }
            else
            {
                var rel = await _kernel.GetLatestAsync();
                rel_tag = rel?.Tag ?? "";
                LatestTag = rel_tag;
                // Alpha 是滚动发布（tag=短哈希），exe 自报版本与 tag 对不上，改用版本文件比对；
                // 版本文件为空（从未装过 alpha）时回退到探测版本，让"当前 x"提示仍然准确
                local = _rt.Settings.CoreChannel == "alpha"
                    ? (_kernel.InstalledMihomoFileVersion.Length > 0
                        ? _kernel.InstalledMihomoFileVersion
                        : _kernel.InstalledMihomoVersion)
                    : _kernel.InstalledMihomoVersion;
            }

            if (rel_tag.Length == 0)
            {
                UpdateStatusText = "没有找到可用版本";
            }
            else if (local.Length == 0)
            {
                UpdateStatusText = $"{kernelName} 最新版本 {rel_tag}，尚未安装";
            }
            else if (string.Equals(local, rel_tag, StringComparison.OrdinalIgnoreCase))
            {
                UpdateStatusText = $"已是最新版本 {rel_tag}";
            }
            else
            {
                UpdateStatusText = $"发现新版本 {rel_tag}（当前 {local}）";
            }
            OnPropertyChanged(nameof(LatestTag));
            OnPropertyChanged(nameof(UpdateButtonText));
            OnPropertyChanged(nameof(UpdateStatusText));
        }
        catch (Exception e)
        {
            UpdateStatusText = $"检查失败：{e.Message}";
            OnPropertyChanged(nameof(UpdateStatusText));
        }
    }

    private async Task UpdateKernelAsync()
    {
        if (IsUpdating)
        {
            return;
        }
        try
        {
            IsUpdating = true;
            OnPropertyChanged(nameof(IsUpdating));
            UpdateStatusText = "正在准备…";
            OnPropertyChanged(nameof(UpdateStatusText));

            if (_rt.Settings.IsSingBoxCore)
            {
                // sing-box 内核：tag 已知用缓存，否则现查
                var rel = LatestTag.Length > 0
                    ? new KernelUpdater.ReleaseInfo(LatestTag, "", "", DateTime.MinValue)
                    : await _kernel.GetLatestSingBoxAsync();
                if (rel is null || rel.Tag.Length == 0)
                {
                    UpdateStatusText = "没有可用的 sing-box 版本";
                    return;
                }
                if (rel.AssetUrl.Length == 0)
                {
                    rel = await _kernel.GetLatestSingBoxAsync() ?? rel;
                }
                UpdateStatusText = $"正在下载 sing-box {rel.Tag}…";
                OnPropertyChanged(nameof(UpdateStatusText));
                var tag = await _kernel.InstallSingBoxAsync(rel, p2 =>
                {
                    UpdateProgress = p2;
                    UpdateRemainder = 1 - p2;
                    OnPropertyChanged(nameof(UpdateProgress));
                    OnPropertyChanged(nameof(UpdateRemainder));
                }, _rt.Core);
                UpdateStatusText = $"sing-box 已更新到 {tag}";
                _rt.NotifyCoreRestarted();
                RefreshCoreInfo();
            }
            else
            {
                var rel = LatestTag.Length > 0
                    ? new KernelUpdater.ReleaseInfo(LatestTag, "", "", DateTime.MinValue)
                    : await _kernel.GetLatestAsync();
                if (rel is null || rel.Tag.Length == 0)
                {
                    UpdateStatusText = "没有可用版本";
                    return;
                }
                // tag 已知但 URL 为空（来自上次检查缓存）时重新查询
                if (rel.AssetUrl.Length == 0)
                {
                    rel = await _kernel.GetLatestAsync() ?? rel;
                }

                UpdateStatusText = $"正在下载 mihomo {rel.Tag}…";
                OnPropertyChanged(nameof(UpdateStatusText));
                await _kernel.InstallOrUpdateAsync(rel, p =>
                {
                    UpdateProgress = p;
                    UpdateRemainder = 1 - p;
                    OnPropertyChanged(nameof(UpdateProgress));
                    OnPropertyChanged(nameof(UpdateRemainder));
                }, _rt.Core);

                UpdateStatusText = $"mihomo 已更新到 {rel.Tag}";
                _rt.NotifyCoreRestarted();
                RefreshCoreInfo();
            }
            OnPropertyChanged(nameof(AppVersionText));
        }
        catch (Exception e)
        {
            UpdateStatusText = $"更新失败：{e.Message}";
        }
        finally
        {
            IsUpdating = false;
            OnPropertyChanged(nameof(IsUpdating));
            OnPropertyChanged(nameof(UpdateStatusText));
        }
    }

    private void Reset()
    {
        var r = MessageBox.Show("重置将清除全部设置与订阅记录，继续？", "重置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes)
        {
            return;
        }
        foreach (var p in _rt.Profiles.Items.ToList())
        {
            _rt.DeleteProfile(p.Id);
        }
        MessageBox.Show("已清除全部订阅记录。设置项保留，可在对应行修改。", "重置", MessageBoxButton.OK);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
