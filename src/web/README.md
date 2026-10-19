# public-final · 定稿白底界面

这是 2026-10-19 用户确认的最终视觉稿的实现，也是 `BatterBabel.exe` 现在的默认页面
（`PageAssets` 的查找顺序：exe 旁 `assets/` → `src/public-final` → `src/public-original` → `src/public`）。

## 文件

| 文件 | 作用 |
|---|---|
| `index.html` | 固定结构：标题行 / 操作区 / 进度行 / 三行说明 / 两张结果卡 / hosts 行 / 日志框 |
| `app.css` | 白底视觉（浅蓝 CDN 卡、浅橙 API 卡、绿底主按钮、克制动效） |
| `app.js` | scan / cf-optimize / get_progress 的渲染与状态切换；没有宿主时用 FIXTURE 模拟数据 |
| `bridge.js` | **构建生成**：由 `BatterBabel.App.csproj` 从 `../public/bridge.js` 复制，不要手改 |

## 运行

```powershell
# 真实宿主（默认页面就是这里）
wpf\src\BatterBabel.App\bin\Release\net10.0-windows\BatterBabel.exe

# 开发时指定页面目录
BatterBabel.exe --public-dir src\public-final

# 浏览器里看静态界面与状态（FIXTURE 模拟数据，会写日志提示）
# 直接双击 index.html，或：
& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" src\public-final\index.html
```

`?state=` 不是本页面的接口；状态由真实桥或浏览器模拟数据驱动。

## 状态

- 初始：两张卡显示域名与 `—`，进度「等待开始 · 0%」。
- 测速中：进度条前进、百分比与状态文字同步，卡片显示「测速中…」。
- 完成：每张卡两行线路结果（`IP (xx MB/s, xx ms)`），写 hosts 按钮启用。
- 失败：红色行内提示 + 日志（宿主原文），不假装成功。
- 无游戏：下拉显示「没有可优选的游戏」，状态给出重试提示。

## 契约

页面只调用三个命令（经 `bridge.js`）：

- `run_action {action:"scan"|"scan-force"}` → `{games:[{id,name,installed,support,optGroups:[{tag,label,mode,domains}]}],qos}`
- `start_job {action:"cf-optimize",gameId}` → `{jobId,protocolVersion:1}`
- `run_action {action:"cf-optimize",gameId,jobId}` → `{ok,status,groups:[{mode:"cloudflare",domains,lines:[{ip,downloadMbps,latency}]} | {mode:"probe",domains,items:[{ok,ip,latency,downloadMbps,reason}]}],hostsPath}`
- `get_progress {jobId}` → `{percent,message,status,sequence}`

写操作（`cf-apply` / `cf-restore` / `boost` / `tune-system` / `restore-tune`）照发，宿主拒绝时原样显示
`{ok:false,status:"FAILED",message}`。

## 数据来源

- 正常窗口：`scan` 走真实磁盘发现（`Discovery/WindowsGameScanner.cs`）；`cf-optimize` 目前仍是
  `DemoRoutes` 的 **FIXTURE 固定样本**（页面会写日志说明），真实探测在切片 S3 接入。
- `--web-check` / `--web-shot` 自检模式：全部用 fixture，保证断言稳定。
