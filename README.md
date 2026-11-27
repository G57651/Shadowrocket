# Shadowrocket

![Windows 10 / 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows11&logoColor=white)
![.NET 10 · WPF](https://img.shields.io/badge/.NET-10%20%C2%B7%20WPF-512BD4?logo=dotnet&logoColor=white)
![内核](https://img.shields.io/badge/%E5%86%85%E6%A0%B8-mihomo%20%7C%20sing--box-2EA043)

Windows 原生代理客户端，使用 WPF（.NET 10）编写，可在 **mihomo** 与 **sing-box** 两种内核之间切换。界面为中文，支持明暗主题。

> 本项目处于个人开发阶段，暂未提供编译好的发行包，需要自行从源码构建（见[从源码构建](#从源码构建)）。
> 软件仅供学习与研究，请遵守所在地区的法律法规，在合法合规的前提下使用。

## 功能

### 双内核

- 代理内核可在 **mihomo**（Clash 生态，默认）与 **sing-box** 之间切换，两种内核都支持系统代理、TUN 模式与规则 / 全局 / 直连路由
- 内核从上游官方 GitHub Releases 下载，支持**稳定版 / Alpha 通道**，并在设置页一键检查更新
- 完整性校验：`wintun.dll` 比对内置的官方 SHA-256；内核优先比对官方校验和，缺失时回退内置哈希清单并要求确认，无任何校验来源时拒绝安装

### 订阅与配置

- 添加、管理机场订阅，查看节点、代理组与分流规则
- 每个订阅独立的自动更新策略：手动 / 固定间隔 / 每天定时 / Cron 表达式
- 可自定义拉取订阅时使用的 User-Agent（部分机场会按 UA 下发不同数量的节点）

### 代理模式

- **关闭 / 系统代理 / TUN 模式**三选一；TUN 模式需要管理员权限，首次开启时自动下载并校验 `wintun.dll`
- 路由模式：规则 / 全局 / 直连
- 分流规则：智能分流（国内直连 + 广告拦截）/ 使用订阅自带规则 / 全部走代理
- 系统代理守卫：退出、会话结束或异常崩溃后，下次启动时自动还原系统代理设置

### 节点与测速

- 节点网格（每行 1–6 栏可调），带国旗标记，一键切换当前节点
- 延迟测试与真实下载测速；自动测速、自动测延迟定时任务
- 排序：默认 / 名称 / 延迟 / 速度

### 数据面板

- 首页实时速率图表与当前节点延迟，刷新间隔可调
- 活动连接：主机、代理链、命中规则、时长、进程，可按主机或进程过滤
- 历史流量（今日 / 近 7 天 / 本月）与内核日志

### 托盘与一键导入

- 托盘菜单：快速切换代理模式、路由模式与节点，不必打开主窗口
- 注册 `clash://`、`clashmeta://`、`clash-verge://`、`sing-box://`、`hiddify://`、`shadowrocket://` 等链接，机场网页的「一键导入」可直接唤起本软件；也可以选择把链接转发给本机的其它客户端
- 单实例运行：第二个实例带订阅链接启动时，会转交给已运行的实例

### 通用

- 明 / 暗主题、开机自启、关闭到托盘、高 DPI（PerMonitorV2）适配
- 混合端口（HTTP 与 SOCKS5 共用）、允许局域网、IPv6、Fake-IP DNS 等网络选项

## 环境要求

- Windows 10 / 11（x64）
- 运行：.NET 10 Desktop Runtime（或发布为自包含版本）
- 构建：.NET 10 SDK

## 从源码构建

```bash
git clone https://github.com/G57651/Shadowrocket.git
cd Shadowrocket
dotnet build -c Release
```

构建产物在 `bin\Release\net10.0-windows\`。如需免安装分发，可用 `dotnet publish -c Release -r win-x64 --self-contained true` 发布为自包含版本。

首次启动后在首页点击「去下载」获取内核（也可在设置页手动更新）。

## 数据目录

设置、订阅与内核保存在 `%APPDATA%\Shadowrocket\`：

```text
%APPDATA%\Shadowrocket\
  settings.json       设置（损坏时自动从 settings.json.bak 恢复）
  config.yaml         mihomo 配置
  sing-box.json       sing-box 配置
  profiles\           订阅
  core\               mihomo.exe / sing-box.exe / wintun.dll
```

## 安全设计

- 进程启动统一经参数列表转义，避免路径或参数注入
- 只信任数据目录 `core\` 下的内核，或用户显式指定的内核路径
- 外部传入的订阅链接（一键导入）会先清洗再使用
- 内核与驱动下载后先做完整性校验再落盘

## 第三方组件

本软件只是图形界面，代理能力来自以下开源项目：

| 组件 | 用途 |
|---|---|
| [mihomo](https://github.com/MetaCubeX/mihomo) | 代理内核（默认） |
| [sing-box](https://github.com/SagerNet/sing-box) | 代理内核（可选） |
| [Wintun](https://www.wintun.net) | TUN 模式依赖的虚拟网卡驱动 |
| [meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat) | 地理数据与分流规则 |
| [clash-rules](https://github.com/Loyalsoldier/clash-rules) | 分流规则 |

## 许可证

[MIT](LICENSE)
