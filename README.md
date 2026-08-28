# RouteProxy

- **当前版本**: 1.4.2

## 项目概述

RouteProxy 是一个 Windows 11 本地网站分流工具。它不实现 VPN 协议，而是复用电脑上已经运行的第三方 VPN，并使用 sing-box 连接用户配置的 SOCKS5 或 HTTP 静态代理。

- 普通网站：Windows 应用 → 当前第三方 VPN → Internet
- 指定网站：Windows 应用 → RouteProxy 本地入口 → 当前第三方 VPN → 静态代理 → Internet
- 停止或异常退出：恢复 RouteProxy 启动前的 Windows 当前用户代理设置

1.4.2 使用 Windows PAC 自动代理脚本识别域名，不再创建第二个全局 TUN，也不再使用 NRPT。该方案已在本机 NetGuard VPN 环境中完成正常启停、强制结束恢复及运行中 PAC 被覆盖后的自动重建测试。

## 技术栈

- .NET 10 LTS
- WPF 原生桌面 GUI
- sing-box 1.13.19 稳定版
- Windows PAC / WinINET 当前用户代理设置
- Windows Job Object、DPAPI
- `System.Text.Json`

## 项目思路 / 架构设计

### 1.4.2 流量路径

```text
支持 Windows 系统代理的应用（Chrome / Edge / WebView 等）
                         │
                         ▼
                 RouteProxy 本地 PAC
                  │              │
        未命中域名│              │命中域名
                  ▼              ▼
               DIRECT      sing-box 本地 HTTP/Mixed 入口
                  │              │
                  │              ▼
                  │        SOCKS5 / HTTP 静态代理
                  │              │
                  └──────┬───────┘
                         ▼
                   当前第三方 VPN
                         ▼
                      Internet
```

PAC 根据请求中的主机名进行匹配：填写 `openai.com` 会同时匹配 `openai.com` 和所有 `*.openai.com` 子域名。命中时返回本机 sing-box HTTP 入口；未命中时返回 `DIRECT`，这里的 DIRECT 是“不经过 RouteProxy”，底层网络仍由已经运行的 VPN 接管。

运行中切换 VPN 的自动跟随已实现：sing-box 出站显式绑定当前默认 VPN 接口（自动接口选择会误选物理网卡导致静态链路 EOF），并监听 Windows 网络地址变化。另有每 10 秒一次的轻量看门狗检查上游接口、系统代理与 PAC 是否被覆盖；即使 PAC 在初始网络信号采集前已被 VPN 客户端移除，也会直接通过 PAC 完整性检查发现并重建。测试已覆盖正常启停、强制结束恢复与“PAC 被外部移除后自动重建”；不同 VPN 客户端之间的真实节点切换仍建议首次使用时观察一次日志。

如果上游 VPN 本身依赖一个已启用的 Windows 手动代理，RouteProxy 会把它保存为 PAC 的普通流量出口，并让静态代理通过该上游建立链路。若系统已有第三方 PAC，RouteProxy 会拒绝覆盖并给出提示，避免破坏原网络。

### DNS 处理

1.4.2 不修改系统 DNS，不添加 NRPT，也不设置全局虚拟网卡路由。对于经 HTTP CONNECT / SOCKS5 发送的命中网站，sing-box 将域名交给静态代理链处理；普通网站继续使用当前 VPN 的 DNS 行为。

曾参考 Talpa 的“本地 DNS + NRPT + 动态 /32 路由”设计进行实验，但本机 NetGuard 会拦截 Windows DNS 客户端发往本机回环地址和物理网卡地址的 DNS 请求。独立 UDP 监听测试证明请求未到达本地 DNS 服务，因此 1.4.2 选择 PAC，避免与上游 VPN 的 DNS/TUN 接管竞争。项目没有复制 Talpa 源码。

### 恢复与安全

启动分流前，RouteProxy 记录以下注册表值及其“原本是否存在”的状态：

- `ProxyEnable`
- `ProxyServer`
- `AutoConfigURL`
- `AutoDetect`

备份临时保存在 `%LOCALAPPDATA%\RouteProxy\system-proxy-backup.json`。正常关闭时由主程序同步恢复；GUI 被强制结束时，独立看门狗等待进程退出后恢复。恢复完成即删除备份。

sing-box 仍由 Windows Job Object 管理，GUI 异常终止时核心随之结束。静态代理密码使用当前 Windows 用户的 DPAPI 加密；含明文密码的 sing-box 运行配置在核心启动后立即删除。

1.4.2 不创建 TUN，因此程序使用当前用户权限运行，不要求 UAC。发布包中的恢复脚本仍保留对 1.3 及更早版本残留 TUN/NRPT 的兼容清理；只有确实发现旧版管理员级残留时才会请求提升权限。

### 适用范围

PAC 覆盖遵循 Windows 系统代理设置的应用，例如 Chrome、Edge、WebView2 和多数桌面网站客户端。明确忽略 Windows 系统代理、使用自带直连网络栈的程序不会被 PAC 分流，需要在该程序内单独设置代理。

## 文件结构

```text
20260826-1553-routeproxy/
├── README.md
├── RouteProxy.slnx
├── THIRD_PARTY_NOTICES.md
├── core/
│   ├── sing-box.exe
│   └── LICENSE
├── installer/
│   └── RouteProxy.iss
├── scripts/
│   ├── install-dotnet.ps1
│   ├── publish.ps1
│   ├── Recover-Network.cmd
│   └── Recover-Network.ps1
├── src/RouteProxy/
│   ├── App.xaml
│   ├── App.xaml.cs
│   ├── AppSettings.cs
│   ├── JobObject.cs
│   ├── MainWindow.xaml
│   ├── MainWindow.xaml.cs
│   ├── PacProxyController.cs
│   ├── SingBoxConfigBuilder.cs
│   ├── SingBoxProcess.cs
│   ├── RouteProxy.csproj
│   └── app.manifest
└── tests/
    ├── Test-RouteProxyApp.ps1
    └── Test-VpnSwitch.ps1
```

## 当前进度

- [x] 完成 WPF GUI、SOCKS5/HTTP 静态代理、账号密码与域名列表
- [x] 集成 sing-box 1.13.19 官方稳定版
- [x] 实现 DPAPI 凭据保护和运行配置及时删除
- [x] 实现 Windows PAC 精确域名/子域名匹配
- [x] 普通网站默认不进入 sing-box
- [x] 保存并精确恢复原 Windows 当前用户代理设置
- [x] 正常关闭恢复测试通过
- [x] GUI 强制结束后的看门狗恢复测试通过
- [x] 移除全局 TUN、NRPT 和默认路由修改
- [x] 移除正常使用时的管理员权限与 UAC
- [x] 运行中切换 VPN 的静态链路自动重建（显式绑定 VPN 接口 + 网络变化指纹重建）已实现并通过回归
- [x] PAC 被外部覆盖后的自动重建与关闭恢复回归通过
- [x] 新增 RouteProxy 独立应用图标，并接入 EXE、窗口、快捷方式和安装器
- [ ] 不同第三方 VPN 客户端的真实节点切换人工验收（首次使用时建议观察一次）
- [x] Release 构建通过：0 警告、0 错误
- [x] 构建无 UAC 的当前用户安装包
- [x] 当前 VPN 实测普通出口正常（地址见测试记录，发布前已移除）
- [x] 当前静态代理实测出口正常（地址见测试记录，发布前已移除）
- [x] `chatgpt.com` PAC 自动选择本地静态入口，普通 `example.com` 返回 DIRECT
- [x] 强制结束后代理备份不存在、项目 sing-box 不运行、原代理状态恢复、VPN 网络可用

## 使用方法

从 GitHub Releases 下载 `RouteProxy-Setup-1.4.2.exe`，按当前 Windows 用户安装到用户选择的目录，不需要 UAC；安装到 D 盘同样支持。安装包未签名，Windows SmartScreen 可能显示“未知发布者”。

1. 先连接现有第三方 VPN，确认普通网页可以访问。
2. 运行安装包。1.4.2 正常情况下不弹 UAC。
3. 选择 SOCKS5 或 HTTP，填写静态代理服务器、端口、用户名和密码。
4. 在域名列表中每行填写一个域名；不要填写 `https://` 或路径。
5. 点击“开启分流”。界面会显示普通 VPN 出口和 VPN → 静态代理出口。
6. 保持 RouteProxy 运行，直接正常使用 Chrome、Edge 或其他支持 Windows 系统代理的应用。
7. 不再使用时点击“关闭并恢复”，然后再断开第三方 VPN。

从旧版首次升级到 1.4.2 时，会一次性补齐 OpenAI 静态资源以及 Google、YouTube 常用域名族；保存为 1.4.2 配置后，列表完全由用户编辑，不会再次强制补齐。

若程序被强制结束，看门狗会自动恢复。万一恢复未发生，可双击发布目录中的 `Recover-Network.cmd`；它优先恢复 RouteProxy 的代理备份，不会重置 Winsock、TCP/IP、Wi-Fi、第三方 VPN 或无关系统设置。

## 构建

```powershell
& .\scripts\install-dotnet.ps1
& .\tools\dotnet\dotnet.exe build .\src\RouteProxy\RouteProxy.csproj -c Release
& .\tools\dotnet\dotnet.exe publish .\src\RouteProxy\RouteProxy.csproj `
  -c Release -r win-x64 --self-contained true -o .\publish\win-x64-v1.4
```

必须保留完整发布目录，不能只复制 `RouteProxy.exe`，因为程序需要同目录中的 .NET/WPF 运行文件、`core\sing-box.exe` 和恢复脚本。

## 已完成验证

### 正常启停

- PAC 文件可由本机访问，规则内容检查通过。
- `chatgpt.com` 的系统代理解析结果为 sing-box 本地端口。
- `example.com` 的系统代理解析结果为目标自身，即 DIRECT。
- ChatGPT HTTPS 返回 HTTP 403；这代表链路到达网站，响应来自站点访问控制而非网络失败。
- 关闭后注册表状态与启动前序列化快照完全一致。

### 异常退出

- 测试直接强制结束 RouteProxy GUI，没有点击“关闭并恢复”。
- 看门狗随后删除代理备份并恢复原注册表状态。
- Job Object 终止本项目 sing-box。
- `AutoConfigURL`、`ProxyEnable`、`ProxyServer` 和 `AutoDetect` 均恢复为启动前状态。
- 恢复后 VPN 出口不变，网络可用。

## 如何继续

- 如更换到新的 VPN 客户端，建议先完成一次真实节点切换验收并观察运行日志。
- 安装包保留默认域名，但不包含静态代理地址、用户名、密码或测试出口记录。
- 不要重新开启旧版全局 TUN/NRPT 路径。
