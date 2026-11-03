using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Shadowrocket.Core;

/// <summary>系统代理：WinINET 每用户代理 + 即时刷新（与 Clash Verge 同机制）。</summary>
public static class SystemProxyService
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static bool IsSet(int expectedPort)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings);
        if (key?.GetValue("ProxyEnable") is not 1)
        {
            return false;
        }
        return key.GetValue("ProxyServer") as string == $"127.0.0.1:{expectedPort}";
    }

    /// <summary>
    /// 开启系统代理。覆盖前先把用户原有设置快照进 settings 并落盘，
    /// 保证退出/崩溃/切换失败后能原样还原（含用户手工配置的企业代理与例外清单）。
    /// 快照只在"尚无快照且当前值不是本软件"时写入一次：端口变更等场景反复 Enable 时复用原快照，
    /// 避免把本软件自己的旧值当成"用户原值"快照进去（审计修复轮第 4 条）。
    /// </summary>
    public static void Enable(int port, AppSettings settings)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings, writable: true)!;
        var noSnapshotYet = settings.PreviousProxyEnable == null && settings.PreviousProxyServer == null;
        if (noSnapshotYet && key.GetValue("ProxyServer") as string != $"127.0.0.1:{port}")
        {
            settings.PreviousProxyEnable = key.GetValue("ProxyEnable") as int? ?? 0;
            settings.PreviousProxyServer = key.GetValue("ProxyServer") as string;
            settings.PreviousProxyOverride = key.GetValue("ProxyOverride") as string;
            try { settings.Save(); } catch { }
        }
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{port}");
        key.SetValue("ProxyOverride", "localhost;127.*;10.*;172.16.*;192.168.*;<local>");
        Refresh();
    }

    /// <summary>
    /// 关闭系统代理并还原快照。还原前先核对当前代理归属：
    /// 当前值不是本软件设的（用户已手工改动）→ 不动注册表、仅作废过期快照；
    /// 是本软件设的 → 还原用户原设置（包括原本未启用/值为空的情形），无快照则仅置 0。
    /// </summary>
    public static void Disable(AppSettings settings, int expectedPort)
    {
        var hasSnapshot = settings.PreviousProxyEnable != null || settings.PreviousProxyServer != null;
        if (!IsSet(expectedPort))
        {
            // 当前系统代理不指向本软件：用户已自行改动（或已被清理），不得用快照覆盖用户现状
            if (hasSnapshot)
            {
                settings.PreviousProxyEnable = null;
                settings.PreviousProxyServer = null;
                settings.PreviousProxyOverride = null;
                try { settings.Save(); } catch { }
            }
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings, writable: true);
        if (key == null)
        {
            return;
        }
        if (hasSnapshot)
        {
            key.SetValue("ProxyEnable", settings.PreviousProxyEnable ?? 0, RegistryValueKind.DWord);
            if (settings.PreviousProxyServer != null)
            {
                key.SetValue("ProxyServer", settings.PreviousProxyServer);
            }
            else
            {
                key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            }
            if (settings.PreviousProxyOverride != null)
            {
                key.SetValue("ProxyOverride", settings.PreviousProxyOverride);
            }
            else
            {
                key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
            }
            settings.PreviousProxyEnable = null;
            settings.PreviousProxyServer = null;
            settings.PreviousProxyOverride = null;
            try { settings.Save(); } catch { }
        }
        else
        {
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        }
        Refresh();
    }

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }
}
