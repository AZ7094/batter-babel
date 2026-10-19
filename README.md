# Batter Babel

Windows 桌面网络优选工具：为已安装的游戏挑选更快的 CDN / API 线路，写入 hosts，并提供游戏加速、系统网络调优与开机恢复。

[English](README.en.md) · [下载 1.0](https://github.com/AZ7094/batter-babel/releases)

![Batter Babel 界面](docs/screenshot.png)

## 功能

- **游戏识别**：自动扫描 Steam / Epic 安装与运行中的游戏，支持 7 个内置画像（Limbus Company、CS2、Apex、Hunt 等）。
- **线路优选**：Cloudflare 地址池（2000 个地址）→ TCP 预筛 → 下载决赛 → 逐域名 HTTPS 验证；另有 10 个精选地址与学习库（`cf-good-ips.json`）参与竞争。
- **hosts 写入与还原**：整文件快照、原子替换、写后读回；只改动程序自己的标记块，保留其它条目。
- **游戏加速**：提升游戏进程调度优先级；按程序写 QoS DSCP 策略（当前系统的策略存储能力自动探测）。
- **系统调优**：接收窗口自动调整、RSS 等网络参数，先快照再写入，支持一键还原。
- **开机恢复**：登录后自动重放 hosts / QoS 策略，避免活动存储重启丢失。
- **中英界面**：右上角一键切换中文 / English，选择会被记住。
- **日志与错误处理**：所有操作在界面日志中留痕，失败给出可读原因。

## 系统要求

- Windows 10 1809 及以上 / Windows 11，x64。
- 发布包为**自包含**版本，不需要安装 .NET 运行时。
- 需要 Microsoft Edge WebView2 运行时：Windows 11 自带；Windows 10 若缺失，程序会提示下载地址。

## 使用

下载 `BatterBabel-1.0-win-x64.zip`，解压后直接运行 `BatterBabel.exe`，无需安装。

命令行入口（可选）：

```
BatterBabel.exe --web-check            页面与宿主通信自检（13 项）
BatterBabel.exe --web-shot             首屏截图到 evidence 目录
BatterBabel.exe --web-live --apply     真实测速 → 写 hosts → 还原
BatterBabel.exe --scan-dump out.json   扫描结果导出
BatterBabel.exe --optimize-dump out.json --game limbus   真实优选结果导出
BatterBabel.exe --install-recovery     安装登录恢复任务（管理员）
BatterBabel.exe --hosts-selftest out.json   临时目录里的 hosts 事务自检
```

数据目录：`%LOCALAPPDATA%\BatterBabel\`（hosts 备份、调优快照、恢复状态、学习库、WebView2 缓存）。

## 从源码构建

要求 .NET 10 SDK。WebView2 依赖包已放在 `wpf/packages/`，克隆后无需联网即可还原：

```
dotnet build wpf/BatterBabel.sln -c Release
dotnet run --project wpf/src/BatterBabel.App
```

自包含发布：

```
dotnet publish wpf/src/BatterBabel.App -c Release -r win-x64 --self-contained true
```

## 自检

```
dotnet run --project wpf/tools/BatterBabel.SelfCheck      # 56 项核心断言
dotnet run --project wpf/tools/BatterBabel.TuneCheck      # 调优引擎
dotnet run --project wpf/tools/BatterBabel.ConfigCheck    # 游戏配置托管块
dotnet run --project wpf/tools/BatterBabel.QosCheck       # QoS 策略
dotnet run --project wpf/tools/BatterBabel.RecoveryCheck  # 开机恢复
dotnet run --project wpf/tools/BatterBabel.RouteQosCheck  # 目标 IP 策略
node --test tests/final-ui.test.js                        # 前端契约
```

## 目录结构

```
wpf/src/BatterBabel.App     WPF + WebView2 宿主、RPC 桥、动作实现
wpf/src/BatterBabel.Core    游戏发现、线路探测、hosts、QoS、调优、恢复
wpf/tools/                  各子系统自检程序
src/web/                    界面（HTML/CSS/JS + MiSans 字体 + 图标）
assets/icons/               应用图标（SVG/PNG/ICO）
tests/                      前端契约测试
```

## 已知限制

- 目标 IP 的 QoS 策略在本机 Windows 10.0.26200 上会被系统丢弃匹配条件，程序会检测并拒绝写入（不假装成功）。
- 没有组策略 QoS 存储时，策略写入活动存储，重启后需要登录恢复任务重新应用。
- 线路测速依赖公网，结果随网络状况波动；界面显示的是本次实测值。
- 界面目前为中文；英文界面在计划中。

## 许可

MIT（见 [LICENSE](LICENSE)）。

界面使用 **MiSans** 字体（小米），许可见 [assets/fonts/LICENSE-MiSans.txt](assets/fonts/LICENSE-MiSans.txt) 与 <https://hyperos.mi.com/font/faq>。
