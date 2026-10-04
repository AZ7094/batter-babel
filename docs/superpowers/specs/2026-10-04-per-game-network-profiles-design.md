# 每游戏网络参数档位（系统调优重构）设计

日期：2026-10-04

## 背景

用户诉求：「系统调优」不应该只对 CS2 生效，应该**给每个游戏**做这类网络参数优化。

现状核查（只读实测，本机）：

1. `tune-system`（optimization.ps1 第 2017 行起，`restore-tune` 于第 2298 行接续）已实现 7 项：TCP Nagle、TCP 全局、网卡低延迟属性、多媒体限流、Wi-Fi 电源、后台占用检查、CS2 autoexec.cfg。其中 CS2 那 6 行参数是**硬编码**的，只对 CS2 生效；选其他游戏点这个按钮，只会得到与游戏无关的全局改动。
2. 实测本机 5 个已安装游戏（Limbus Company / CS2 / How to Fish / Crimson Desert / SCP:SL）：**只有 CS2 存在客户端网络参数面**（`game/csgo/cfg/autoexec.cfg`）。其余要么是 Unity 游戏、安装目录没有任何配置；要么配置与网络无关（Crimson Desert 的 `bin64/OptiScaler.ini` 是画质超分工具配置；SCP:SL 的 `ConfigTemplates/*.template.txt` 是**服务端**配置模板）。
3. CS2 那 6 行参数中，**3 条在 CS2 中无效、1 条未文档化、2 条有效但其中 1 条不属于网络参数**（证据见下）。
4. 系统级参数（TCP/网卡/多媒体限流）**天生是全局的**，不可能真正 per-process。因此「每个游戏」只能在**档位**层面兑现，不能靠给每个游戏写一份配置文件。

### CS2 参数核实结论

判据：Total CS 的每条命令页顶部有兼容性徽章，`rate` 页标 "CS2 & CS:GO Compatible"，`cl_interp` / `cl_interp_ratio` 页标 "CS:GO Command"（其 CS2 命令库默认隐藏 CS:GO 命令），`cl_allow_animated_avatars` 页标 "CS2 Command"。辅以 CSDB.gg 对 `net_graph` 的 "Removed in CS2" 标注。

| `tune-system` 现写入 | 证据 | 处置 |
| --- | --- | --- |
| `rate 196608` | [rate](https://totalcsgo.com/commands/rate)：徽章 **CS2 & CS:GO Compatible**，默认值 **786432**；该页示例注明 786432 为 "maximum value (unrestricted)"，24576 为最低值 | **删除**（用户已确认不保留）。注意：这是一个**有依据的取舍**而非乱写——该页明确建议「网络差就调低」，而 196608 只有默认上限的 25%，等于主动压低带宽上限 |
| `cl_interp 0.031` | [cl_interp](https://totalcsgo.com/commands/clinterp)：徽章 **CS:GO Command**（非 CS2）；默认值 0.03125。该页原文：「We do not recommend adjusting `cl_interp` as the value of this command is **no longer used** (in other words, **it does nothing**)」 | 删除（写法上还恰好等于 CS:GO 的默认值） |
| `cl_interp_ratio 2` | [cl_interp_ratio](https://totalcsgo.com/commands/clinterpratio)：徽章 **CS:GO Command**（非 CS2）；默认值 2。该页把 2 列为「Poor / Laggy Internet Connections」的推荐值 | 删除（CS2 中不生效） |
| `cl_net_buffer_ticks 64` | Total CS 命令库**无此条目**（`/commands/clnetbuffersticks` 返回 404，而同规则的 `clinterpratio` 正常返回） | 删除（未文档化的引擎 cvar，不写） |
| `net_graph 1` | CSDB.gg 标注 **Removed in CS2**：「the CS:GO overlay no longer exists. Its replacement is the Telemetry section under Settings → Game」 | 删除 |
| `cl_allow_animated_avatars false` | [cl_allow_animated_avatars](https://totalcsgo.com/commands/clallowanimatedavatars)：徽章 **CS2 Command**，默认值 **1** | **在 CS2 中有效**。但它属于画面/性能开关而非网络参数；本设计的原则是「网络配方只写网络参数」，故从网络配方中移除。**开放问题**：若要保留这条非网络优化，需要单独的决定（见「开放问题」） |

配套评估：CS2 中 `rate` 默认已是 Unrestricted（786432），`cl_timeout` 与延迟无关；对国内（Perfect World）服务器玩家而言，`mm_dedicated_search_maxping` 这类匹配限速参数意义有限。因此**CS2 的配置文件配方内容为空**，动作改为「清理此前写入的无效参数块」。给 CS2 的真实收益来自 latency 档位，不来自 cfg。

## 目标

- 「系统调优」改为**按当前选中游戏**执行：套用该游戏的系统参数档位，并在该游戏确有客户端配置面时写入配置文件。
- 结果里逐项如实标注「真生效 / 无客户端网络参数 / 已清理旧无效参数」，不得把被游戏忽略的参数报成成功。
- 把每游戏配方做成**数据**，新增游戏只需加一条数据、不改执行逻辑。
- 清理 CS2 现存 `autoexec.cfg` 里那 6 行：3 条在 CS2 中无效、1 条未文档化、1 条把带宽上限压低到默认 25% 的 `rate`、1 条虽在 CS2 中有效但属非网络参数（见「开放问题」）。

## 非目标

- **不做后台常驻 / 游戏启动自动切档**（用户选择手动触发）。
- **不代写 Steam 启动项**（`localconfig.vdf` 在 Steam 运行时会被覆盖，且属用户个人配置）。仅在结果里提示「若 cfg 未生效，可在启动项加 `+exec autoexec.cfg`」。关于 CS2 是否会自行加载 autoexec.cfg，公开来源互相冲突（有来源称会自动加载，也有多份 2024 年后的指南称必须加启动项），**本设计不依赖任何一方**：写入受管块是幂等的，是否自动加载只影响它何时生效。
- **不写任何会被游戏忽略的参数**——这是本设计的核心约束。
- 不引入 JSON 配方数据文件（PowerShell 5.1 下 JSON 弱类型且失去语法检查）。
- 不改 `cf-optimize`（线路优选）与 `boost`/`optimize`（QoS）流程。
- 不新增第三方测试框架。

## 已确认的决策

- 结构采用**独立模块** `scripts/net-profiles.ps1`（与 `ip-core.ps1` / `cf-probe.ps1` 同模式，可单测），而不是把配方内嵌进 `optimization.ps1`，也不使用 JSON 数据文件。
- CS2 的 `rate 196608` **不保留**（不改为写默认值 786432，直接不写 `rate`，尊重游戏内设置）。
- 两档的 `autotuninglevel` **都用 `normal`**（不采用激进的 `restricted`）。
- 不写 `mm_dedicated_search_maxping`。
- 触发方式：**手动**——选中游戏后点「系统调优」。
- 配置文件写入使用**受管标记块**，只改块内内容。
- 系统参数还原语义沿用现有「首次快照原值、之后永不覆盖」，还原永远回到最初原值。

## 架构

### 1. 新模块 `scripts/net-profiles.ps1`

纯数据 + 纯函数，保持**纯 ASCII、无 BOM**（PowerShell 5.1 无 BOM 时按 ANSI 读 .ps1，含中文会乱码并导致解析失败）。

```
$NetProfileTiers    # 档位定义表（latency / throughput），纯数据
$NetGameRecipes     # 每游戏配方表，纯数据
Get-NetTier         # <tierId> -> 档位定义；未知 id 抛错
Get-NetRecipe       # <gameId> -> 配方；未登记的 gameId 返回默认配方
Get-NetTierDelta    # <fromTier, toTier> -> @{ Set = @(...); Restore = @(...) }  纯函数
Merge-ManagedBlock  # <text, lines> -> 新文本   纯函数，幂等
Remove-ManagedBlock # <text> -> 新文本          纯函数，无标记时原样返回
```

`Get-NetTierDelta` 与两个受管块函数是本设计里最容易写错、也最值得单测的部分：切档时必须主动还原上一档设过、本档不设的值，否则会残留。

### 2. 档位定义

| 参数 | latency（实时对战） | throughput（下载为主） |
| --- | --- | --- |
| `TcpAckFrequency` / `TCPNoDelay` / `TcpDelAckTicks`（Nagle 三件套） | 设 `1` / `1` / `0` | **还原为快照原值**（通常为删除该值） |
| `*InterruptModeration` | `0`（低延迟，吃 CPU） | `1`（大流量吞吐更好） |
| `*EEE` | `0` | `0` |
| `*FlowControl` | `0` | `0` |
| `netsh int tcp set global autotuninglevel` | `normal` | `normal` |
| `netsh int tcp set global rss` | `enabled` | `enabled` |
| `NetworkThrottlingIndex` / `SystemResponsiveness` | `0xFFFFFFFF` / `10` | `0xFFFFFFFF` / `10` |
| 网卡电源管理 `AllowComputerToTurnOffDevice` | `Disabled` | `Disabled` |
| Wi-Fi 电源模式 | 最高性能 | 最高性能 |

**必须如实记录：两档的真实差异只有 2 项**（Nagle 三件套、`*InterruptModeration`）。其余项两档相同，不构成「按游戏优化」的实际差异。

### 3. 应用与还原语义

- **幂等**：套用档位 X ＝「按 X 设值」+「把 X 未覆盖的受管项**还原成 `tune-state.json` 里的原始值**」。现有 `tune-system` 只设值、从不还原，直接复用会在切档时留下残留（例如 latency→throughput 后 Nagle 三件套仍是 `1/1/0`），因此必须新增「按快照还原单项」的辅助函数。
- **系统参数快照**：沿用现有机制（首次运行写 `tune-state.json`，此后永不覆盖），因此反复切档不会污染还原基线，「还原」始终回到**最初原值**。
- **配置文件**：在目标文件内写入受管块

  ```
  // >>> Batter Babel >>>
  ...受管内容...
  // <<< Batter Babel <<<
  ```

  块外内容一律不碰；重复执行时先移除旧块再写入新块（幂等）。首次写入前若文件已存在且无 `.batterbabel.bak`，则复制一份备份。还原＝移除受管块（文件因此变空时保留空文件，不删除用户文件）。
- 现有的「直接备份为 `autoexec.cfg.batterbabel.bak` 再整体覆盖」策略**废弃**：它会吞掉用户在 autoexec.cfg 里的自有内容。

### 4. CS2 配方重做

- `ConfigFiles`：一条，指向 `<CS2根>\game\csgo\cfg\autoexec.cfg`，受管块内容为**注释 + 空**，即清掉旧块内容并留下说明「Batter Babel 不再写入 CS2 网络 cvar，原因见设计文档」。
- 迁移：现存文件是旧版整体覆盖产物（内容即那 6 行），需能被新版安全接管——受管块函数在**找不到标记**时按「旧格式整体接管」处理：先备份，再以受管块重写。
- `Tier`：`latency`。
- 结果项需说明「已移除 3 条在 CS2 中无效的参数、1 条未文档化参数，以及 1 条把带宽上限压低到默认 25% 的 `rate`」。

### 5. 每游戏档位映射

| 档位 | 游戏 | 依据 |
| --- | --- | --- |
| `latency` | cs2、apex、hunt、dst、terraria、scp | 实时对战/联机，小包多、延迟敏感 |
| `throughput` | limbus、bd2 | 回合制，瓶颈是大体积 CDN 下载 |
| `latency`（默认） | `steam-<appid>` 等动态条目 | 无法预知，取保守的实时假设 |

未登记的 gameId 一律走默认档位与空配置文件列表，并如实报「该游戏无客户端网络参数」。

### 6. 接口与前端改动

- `scripts/optimization.ps1`：
  - `tune-system` 新增可选 `-GameId`（缺省时使用默认档位，保持从无游戏上下文的调用可用）。
  - 执行体改为「取配方 → 套档位（含还原差集）→ 写受管块 → 汇总结果」，替换现有 7 项硬编码流程中第 7 项（CS2）与档位相关部分。
  - `restore-tune` 连带移除受管块。
- `src-tauri/src/commands.rs`：**无需改动**。`game_id` 已是通用透传（`if let Some(g) = &game_id { cmd.arg("-GameId").arg(g) }`），且 `tune-system` 已在 `VALID_ACTIONS` 与 `ELEVATED_ACTIONS` 列表中。
- `src/public/app.js`：调用 `tune-system` 时带上当前 `gameId`；渲染新增结果项；按现有 `LOCALIZE` 规则表补中文本地化。
- **提权**：`tune-system` 已在提权列表中，档位改动不需要新的提权逻辑。

### 7. 部署约束

`src-tauri/tauri.conf.json` 的 `bundle.resources` 是**逐文件白名单**，当前列出 `optimization.ps1`、`ip-core.ps1`、`cf-probe.ps1`。新增 `net-profiles.ps1` 后**必须**同时加入，否则安装版会在 dot-source 处找不到文件，导致每条操作都失败。CI 已有资源白名单一致性检查会拦下遗漏。

版本号在 `src-tauri/tauri.conf.json` 与 `src-tauri/Cargo.toml` 一并升到 `0.10.2`。

### 8. 测试

新增 `scripts/tests/net-profiles.tests.ps1`，挂进 `scripts/tests/run-tests.ps1`，覆盖：

- `Get-NetTierDelta`：同档 delta 为空；latency→throughput 必须把 Nagle 三件套列入 `Restore`；throughput→latency 必须列入 `Set`。
- `Merge-ManagedBlock` / `Remove-ManagedBlock`：块外内容零改动；重复执行幂等；只有起始标记、或标记顺序颠倒时不误删内容。
- `Get-NetRecipe`：已知 gameId 返回登记配方；未知 gameId 返回默认配方且 `ConfigFiles` 为空。
- 纯函数测试，不触碰注册表、网卡、文件系统。

## 验收标准

1. 选中任一已安装游戏执行「系统调优」，结果展示该游戏所用档位，且系统参数与档位定义一致。
2. 从 latency 切到 throughput 后，注册表中 Nagle 三件套回到快照原值（不再残留 `1/1/0`）。
3. CS2 的 `autoexec.cfg` 在受管块内的旧 6 行被清除；块外用户内容保持不变。
4. 未登记游戏得到明确「无客户端网络参数」的结果，而不是空成功。
5. 新单测全部通过；CI 六项检查（纯 ASCII / 解析 / `node --check app.js` / 资源白名单 / PS 测试 / cargo）全绿。
6. `restore-tune` 能把系统参数与受管块都还原干净。

## 开放问题

- `cl_allow_animated_avatars 0` 已核实为 **CS2 有效命令**（默认 1），只是不属于网络参数。本设计按「网络配方只写网络参数」将其移除；若希望保留这条非网络优化，需要在实现前明确决定，并给它一个不叫「网络参数」的归属（例如单独的画面/性能项）。

## 风险

- **切档残留**：若不实现「按快照还原差集」，档位切换会静默残留上一档的值。这是本设计最主要的技术风险，已由 `Get-NetTierDelta` + 单测针对性覆盖。
- **受管块误删**：标记不全时若直接删除块内内容会破坏用户文件。函数在标记不完整时**不修改**输入并返回原文。
- **资源白名单遗漏**：安装版全面失败。由配置同步 + CI 检查双重拦截。
- **结论的时效性**：参数有效性的依据来自第三方命令库（Total CS / CSDB.gg）而非 Valve 官方文档，且判据是「该库是否把命令标为 CS2 兼容」。Valve 若在未来版本恢复/新增网络 cvar，需重新核实配方，而不是假定旧结论永真。
- **探索性动作**：`tune-system` 需提权，实际套用会触发 UAC。
