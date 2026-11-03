using System.Windows.Forms;
using Shadowrocket.Core;

namespace Shadowrocket.Views;

/// <summary>托盘菜单需要的宿主回调：UI 线程封送 + 显示主窗口 + 退出。</summary>
public sealed class TrayHost
{
    public required Action<Action> InvokeOnUi { get; init; }
    public required Action ShowMainWindow { get; init; }
    public required Action ExitApp { get; init; }
}

/// <summary>
/// 托盘菜单（ui-spec §3.5）：代理模式 / 路由模式 / 节点快速切换 / 显示主窗口 / 退出。
/// 独立成类，便于在不依赖鼠标点托盘的情况下验证菜单结构与动作。
/// </summary>
public static class TrayMenu
{
    public static void Build(ContextMenuStrip menu, AppRuntime rt, TrayHost host)
    {
        menu.Items.Clear();

        var modeItem = new ToolStripMenuItem("代理模式");
        foreach (var (label, mode) in new[]
                 {
                     ("TUN 模式", TunnelMode.Tun),
                     ("系统代理", TunnelMode.SystemProxy),
                     ("关闭", TunnelMode.Off),
                 })
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = rt.Settings.TunnelMode == mode,
                CheckOnClick = false,
            };
            var captured = mode;
            item.Click += async (_, _) =>
            {
                await rt.SwitchModeAsync(captured);
                host.InvokeOnUi(() => menu.Items.Clear());   // 让下次展开重新反映状态
                rt.NotifyNodesChanged();
            };
            modeItem.DropDownItems.Add(item);
        }
        menu.Items.Add(modeItem);

        var routeItem = new ToolStripMenuItem("路由模式");
        foreach (var (label, value) in new[] { ("规则模式", "rule"), ("全局模式", "global"), ("直连模式", "direct") })
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = rt.Settings.RouteMode == value,
                CheckOnClick = false,
            };
            var captured = value;
            item.Click += async (_, _) =>
            {
                await rt.SetRouteModeAsync(captured);
                host.InvokeOnUi(() => menu.Items.Clear());
            };
            routeItem.DropDownItems.Add(item);
        }
        menu.Items.Add(routeItem);

        var nodeItem = new ToolStripMenuItem("节点快速切换");
        nodeItem.DropDownOpening += (_, _) => _ = FillNodesAsync(nodeItem, rt, host);
        _ = FillNodesAsync(nodeItem, rt, host);
        menu.Items.Add(nodeItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("显示主窗口", null, (_, _) => host.ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => host.ExitApp());
    }

    /// <summary>用 PROXY 组的真实成员填「节点快速切换」，当前出口打勾。</summary>
    public static async Task FillNodesAsync(ToolStripMenuItem host, AppRuntime rt, TrayHost ui)
    {
        List<string> members = [];
        var current = "";
        try
        {
            var g = await rt.Api.GetGroupAsync("PROXY");
            if (g != null)
            {
                (current, members) = g.Value;
            }
        }
        catch
        {
            // 内核没在跑：菜单留空并禁用
        }
        ui.InvokeOnUi(() =>
        {
            host.DropDownItems.Clear();
            host.Enabled = members.Count > 0;
            host.Text = members.Count > 0 ? $"节点快速切换（{members.Count}）" : "节点快速切换（内核未运行）";
            foreach (var m in members)
            {
                var item = new ToolStripMenuItem(m)
                {
                    Checked = m == current,
                    CheckOnClick = false,
                };
                var captured = m;
                item.Click += async (_, _) =>
                {
                    try
                    {
                        await rt.Api.SelectInGroupAsync("PROXY", captured);
                    }
                    catch
                    {
                        // 组里没有该成员（内核刚重启）：忽略，下次展开会刷新
                    }
                    rt.NotifyNodesChanged();
                };
                host.DropDownItems.Add(item);
            }
        });
    }
}
