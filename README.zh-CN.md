# Batter Babel

[English](README.md) | **简体中文**

一款 Windows 游戏网络优化工具。它会自动识别你电脑上**已安装的联网游戏**，为选中的游戏测速其 CDN / API 线路，并在你确认后把最快的节点写入 `hosts`。另外还提供一个**与游戏无关**的一键系统网络调优。

全部逻辑都在本地运行。没有云端服务、不需要账号、不使用第三方代理节点、不转发任何流量。

## 功能特性

**能做的**：加速**基于域名的服务**，也就是登录、更新、大厅、好友、成就、商店、官方站点、资源 CDN。

**不能做的**：
- **游戏内延迟** —— 对局流量是 UDP 直连服务器（或 P2P 直连玩家），**不经过域名**，`hosts` 无从下手。这类游戏软件会自动显示琥珀色提示说明。
- **游戏内下载**（地图 / mod / 存档同步）—— Steam 是先向 Connection Manager 要一份**内容服务器 IP 列表**再直连那些 IP，P2P 传输更是完全没有域名。想加速 Steam 下载请用官方开关：**Steam → 设置 → 下载 → 下载区域**，比任何 `hosts` 优化都有效。

### 加速游戏（本机 QoS）

这是唯一能改善**对局延迟**的手段（对 Hunt: Showdown 这类服务器动态分配的游戏尤其有用）。

- 为选中的游戏可执行文件创建 Windows QoS 规则（`DSCP 46`，全部网络配置文件），**只匹配这一个 exe**，不影响其它程序。
- **所有 Windows 版本都能用。** 家庭版没有组策略，`-PolicyStore Local` 必然报「找不到网络路径」（System Error 53）；软件会先试 `Local`（重启后仍有效），失败则**自动降级到 `ActiveStore`**，并注册一个**登录计划任务**在每次重启后自动重建规则——家庭版用户不需要每次手动点。取消最后一个加速时任务会自动注销。
- 按钮状态保存在 `%LOCALAPPDATA%\BatterBabel\boosted.json`（直接读 QoS 存储需要管理员权限，普通扫描没有）。记录里还包含**存储类型**和**开机时间**，所以重启后能正确判断规则是否已失效。

### 游戏检测

- Steam 把每个 app 的**分类标记**存在本地 `appcache/appinfo.vdf` 里，键名就是 `category_<id>`（Steam 官方 ID：`1` 多人、`9` 合作、`20` MMO、`27` 跨平台多人、`36` 在线 PvP、`38` 在线合作）。软件直接解析这个二进制 KeyValues 文件——16 字节头（magic `0x07564428`/`0x07564429`、universe、字符串表偏移）→ 重复的 `[appid][size][binary KV]` 条目 → 末尾字符串表（键名**按索引引用**）——**完全离线**判断是否联网，不需要商店 API（很多网络下压根连不上）。
- 遍历所有 Steam 库（`libraryfolders.vdf` + 注册表，解析每个 `appmanifest_*.acf`），独立客户端则通过卸载注册表和常见安装目录（`STOVE`、`Neowiz` 等）查找。库路径按大小写不敏感去重（`d:\steam` 和 `D:\steam` 是同一个库），Steam 的运行时组件（appid 228980）会被跳过。

### 平台目标：检测到的游戏都能优选

大多数游戏只会连那么几个平台服务，所以域名按**平台**定义（`$PlatformTargets`：Ubisoft、Epic、Blizzard、EA），检测到的游戏自动继承所属平台的目标。彩虹六号用育碧 Connect 的域名。

**Steam 自身的域名不参与优选**（`api/login.steampowered.com`、Steam CDN）。大多数人本来就用加速器加速 Steam，而 Steam 下载速度的官方控制项是「设置 → 下载 → 下载区域」，优选这些域名没有意义。所以只有 Steam 域名的游戏（CS2、Hunt: Showdown 等）会显示为**无优选线路**——它们请用「**加速游戏**」（本机 QoS）来改善对局延迟。
游戏 ID 只做格式校验，**游戏定义的唯一位置就是 `optimization.ps1` 里的 `$Catalog`**——新增游戏不需要动 Rust 层，也没有 `ValidateSet` 要同步。

### 线路优选

- **Cloudflare** —— 从官方 IPv4 段均匀采样 **2000 个地址**，用并发 TCP:443 握手筛选（整池约 10 秒），再对 10 个精选地址、以往学到的快速地址、以及延迟最低的 20 个发现（共 30 个）跑**真实下载测速**（`curl --resolve`，仍然校验 TLS 链），按实测速度排名。
  - **自我学习** —— 每次运行都会把最快的几个下载地址记到 `%LOCALAPPDATA%\BatterBabel\cf-good-ips.json`。连续两次进入前列的地址可获得**永久免筛直通**，跳过那个并不预测吞吐量的延迟筛选阶段。库上限 30 条。
  - **失效处理** —— 每个被测速的地址都会刷新健康度：测到速度清零失败计数，失败则累加，**连续两次失败即淘汰**。精选地址只降级不删除，避免「删除→下轮免筛→又失败」的死循环。从未赢过的地址不入库，防止探索噪音挤掉真正的优胜者。
- **CloudFront / 通用 CDN** —— 从系统解析器 **加上** AliDNS、DNSPod 的 DoH 收集候选 IP，逐域名探测（保留真实 TLS SNI、证书、Host 头），取延迟中位数最低的那个。
- **写入 hosts** —— 结果按厂商分块写入（`CF`、`AMAZON`、`STEAM`、`EA`、`KLEI`、`UBISOFT`、`EPIC`、`BLIZZARD`），自动备份到 `hosts.batterbabel.bak`，一键还原。标记块之外的任何内容都不会被改动。`CF` 块还会**拒绝任何非 Cloudflare 官方地址**。

### 系统调优（与游戏无关，一键完成）

通用的降低延迟设置，对任何联网游戏都有益：

- **TCP 低延迟** —— 按接口关闭 Nagle / 延迟 ACK（`TcpAckFrequency`=1、`TCPNoDelay`=1、`TcpDelAckTicks`=0），小包立即发出
- **TCP 全局参数** —— `autotuninglevel=normal`、关闭启发式、启用 RSS
- **网卡低延迟属性** —— 关闭中断节流、节能以太网、流控、设备省电
- **Windows 网络节流** —— 关闭 `NetworkThrottlingIndex`，`SystemResponsiveness`=10
- **Wi-Fi 电源** —— 设为最高性能
- **后台带宽检查** —— 列出占用带宽的程序（百度网盘、迅雷、视频与 BT 客户端等）
**还原系统调优** —— 首次修改前会把原始值快照到 `%LOCALAPPDATA%\BatterBabel\tune-state.json`，点一下即可全部还原。

## 下载

预编译安装包发布在 [Releases](../../releases) 页面：

- `Batter Babel_<版本>_x64-setup.exe` —— NSIS 安装包（桌面 + 开始菜单快捷方式），约 1.1 MB。

> 需要 **WebView2 运行时**（Windows 11 和已更新的 Windows 10 都自带）。

## 使用
只有 **写入 hosts**、**还原 hosts**、**加速游戏 / 取消游戏加速**、**系统调优**、**还原系统调优** 会请求管理员权限。拒绝授权不会改动你的任何网络设置。

## 从源码构建

需要 [Rust](https://rustup.rs)（stable，MSVC 工具链）和 WebView2 运行时。

```powershell
# 安装 Tauri CLI
cargo install tauri-cli --version "^2"

# 构建安装包，产物在 src-tauri/target/release/bundle/
cargo tauri build
```

图标由 `build/make_tauri_icons.py`（Python + Pillow）生成，需要时可用它重新生成。

## 工作原理

- 一个很小的 [Tauri](https://tauri.app) 外壳（`src-tauri/`，Rust）在系统 WebView2 里承载静态界面（`src/public/`），向前端暴露两个命令。
- `run_action` 是 **async + `spawn_blocking`**：PowerShell（以及 UAC 授权）可能阻塞一分钟，绝不能跑在 UI 线程上。
- Rust 后端调用 PowerShell 后端（`scripts/optimization.ps1`）完成游戏扫描、线路探测、Cloudflare / CloudFront 优选、hosts 写入与系统调优。
- **实时进度走两条独立通道**：PowerShell 把 `百分比|消息` 追加到 `-ProgressFile` 指定的文件；Rust 轮询线程把每次变化既镜像成 `bb-progress` 事件，**也**写进一个全局变量供 `get_progress` 返回。界面读的是轮询值（操作期间每 250 ms 一次 `invoke('get_progress')`），事件只是低延迟补充——万一 `window.__TAURI__.event` 不可用，`listen` 会静默失效，那样进度条就只剩自己的计时器在动了。
- Cloudflare 下载测速使用系统 `curl.exe`，参数 `--ssl-no-revoke --resolve`：仍然校验 TLS 链和主机名，仅跳过吊销检查。
- 新增游戏只改**一个地方**：`optimization.ps1` 里的 `$Catalog`。其余全部由 Steam 本地元数据自动识别。

## 安全性

- 只会测试和写入 **Cloudflare 官方公开 IP 段**（来自 `https://www.cloudflare.com/ips-v4`）。`cf-apply` 会在写入 `CF` 块之前**硬性拒绝**任何非 Cloudflare 地址，并报告拒绝条数。
- hosts 写入被包在 `# ==== Batter Babel <TAG> START/END` 标记块里（TAG = `CF`、`AMAZON`、`STEAM`、`EA`、`KLEI`、`UBISOFT`、`EPIC`、`BLIZZARD`），备份到 `hosts.batterbabel.bak`，可用 **还原 hosts** 移除。
- **不接管 DNS、不创建代理、不修改路由表。**
- 写 hosts 只对真正使用该 CDN 的域名有效。把域名指向错误厂商的 IP 会导致其无法访问——用 **还原 hosts** 恢复。
- 系统调优只动 TCP / Nagle 定时器、网卡电源与低延迟属性、多媒体网络节流索引、Wi-Fi 电源模式。原始值会在修改前输出到日志。
- QoS 加速**只匹配选中游戏的可执行文件路径**，不会限速或降级任何其它程序。

## 免责声明

本工具供个人在你有权管理的网络上合法使用。使用 `hosts` 条目、QoS 规则或系统网络设置，可能违反某些网络或服务的条款。使用方式由你自行负责。

## 许可证

[MIT](LICENSE)
