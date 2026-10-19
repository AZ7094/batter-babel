# Batter Babel

游戏网络优选工具：为支持的游戏挑选更快的 CDN / API 线路，并可写入 hosts、优化系统网络参数、提升游戏进程调度优先级。

## 功能

- **游戏检测**：从 Steam 库、Epic 清单和运行中的进程识别已安装游戏，支持手动重新扫描。
- **线路优选**：Cloudflare 下载线路使用「精选地址 + 学习库 + 地址池 → TCP 预筛 → 下载决赛 → 逐域名 HTTPS 验证」的流水线；API 线路按同样规则验证。最优结果可写入 hosts。
- **写入与还原**：hosts 写入带标记块、自动备份、原子替换、写后读回校验；一键还原，必要时通过 UAC 提权。
- **游戏加速**：提升游戏进程调度优先级，并按需为该进程建立 DSCP 46 的 QoS 策略。
- **系统调优**：调整 TCP 自动调优级别与 RSS，写入前保存快照，可一键回滚；支持网卡节能相关设置。
- **恢复机制**：登录计划任务在下次登录时按记录恢复 hosts / QoS / 调优。
- **界面**：WebView2 承载的本地 HTML 界面（白底浅色，单窗口），中文界面。

## 技术栈

- C# / .NET 10（WPF 宿主，`net10.0-windows`）
- Microsoft WebView2（官方 SDK）
- HTML / CSS / JavaScript（`src/web`，无前端构建步骤）
- PowerShell（通过 `-EncodedCommand` 执行 netsh / QoS 等系统命令）

## 环境要求

- Windows 10 / 11 x64
- .NET 10 Desktop Runtime（开发需 .NET 10 SDK）
- WebView2 Runtime（Windows 11 自带）
- 修改 hosts、QoS、系统调优需要管理员权限；程序会在需要时弹出 UAC

## 构建与运行

```powershell
# 还原 + 构建（首次需要访问 nuget.org）
dotnet build wpf/BatterBabel.sln -c Release

# 运行
wpf/src/BatterBabel.App/bin/Release/net10.0-windows/BatterBabel.exe
```

发布为便携目录：

```powershell
dotnet publish wpf/src/BatterBabel.App/BatterBabel.App.csproj -c Release -r win-x64 --self-contained false -o dist/BatterBabel-win-x64
```

自检：

```powershell
# 页面 ↔ 宿主通信与业务渲染（WebView2，无系统写入）
dist/BatterBabel-win-x64/BatterBabel.exe --web-check

# 核心库单元自检
dotnet run --project wpf/tools/BatterBabel.SelfCheck -c Release
```

界面资源在 `src/web`，构建时会复制到输出目录的 `assets/`，因此发布目录可以脱离仓库独立运行。

## 项目结构

```
wpf/
  BatterBabel.sln
  src/BatterBabel.App/      WPF + WebView2 宿主、RPC 桥、真实测速与系统操作
  src/BatterBabel.Core/     业务核心：发现、探测、hosts、QoS、调优、恢复
  tools/BatterBabel.*/      各子系统自检程序
src/web/                    界面（index.html / app.js / app.css / 字体 / 图标）
assets/icons/               应用图标（SVG / PNG / ICO）
tests/                      前端契约测试（node --test）
```

## 已知限制

- 线路质量受本地网络与运营商影响，优选结果只代表测量时刻。
- Windows 的 QoS 策略默认写入活动存储，重启后需要重新应用；登录恢复任务会处理这一点。
- 部分 Windows 版本的 QoS 驱动会丢弃 `IPDstPrefixMatchCondition`，此时 IP 级 QoS 会以失败关闭（fail-closed），不影响 hosts 优选。
