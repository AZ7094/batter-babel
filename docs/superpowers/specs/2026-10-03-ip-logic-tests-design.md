# IP 逻辑测试基线设计

日期：2026-10-03

## 背景

Batter Babel 的 Cloudflare/CloudFront 优选依赖一段纯函数 IP/CIDR 逻辑，但这段代码零测试：

- `ConvertTo-IpString` / `ConvertFrom-IpString`（uint32 ↔ 点分十进制，曾因 `173 << 24` 溢出踩坑，靠 `[long]` 修复）
- `Test-IpInCidr` / `Test-IpInCloudflare`（CIDR 归属判断）
- `Get-CfCandidatePool`（从官方 Cloudflare 网段按固定步长采样凑候选池）

这段逻辑承载优选的第一步（候选池采样），且历史上已记录两条事故（数组被强转成字符串、TCP 批测串行等待），但没有任何自动回归保护。

Rust 侧 `commands.rs` 的入口校验 `is_ipv4` / `is_valid_game_id` 同样零测试，且 `is_ipv4` 有一个真实缺口：它接受带前导零的 IPv4（如 `010.1.1.1`），而 PowerShell 的 `[System.Net.IPAddress]::Parse` 会把前导零按八进制解释（`010.1.1.1` → `8.1.1.1`，`08.1.1.1` 直接抛异常），造成静默的 IP 漂移。

## 目标

- 为 PowerShell 的 IP/CIDR 纯逻辑建立可单跑、无网络的回归测试，覆盖历史事故点。
- 为 Rust 入口校验补单元测试。
- 顺手修复 `is_ipv4` 的前导零缺口（TDD：先写失败测试再修）。
- 把可测试的纯函数从 2690 行脚本抽出为独立可 dot-source 的文件，脚本行为不变。

## 非目标

- 不引入 Pester / xunit 等第三方测试框架——手写断言跑分器，对齐 LLC_BABEL 自造 console runner 的思路。
- 不改动优选结果，不写任何依赖网络的测试。
- 不重构 `Get-CfOptimize` 主流程，不抽其他函数。

## 已确认的决策

- 测试跑分器用 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\run-tests.ps1` 运行，失败返回非零退出码，输出 `ok -` / `fail -` 行。
- 抽出后的 `scripts/ip-core.ps1` 保持纯 ASCII（PowerShell 5.1 无 BOM 时按 ANSI 读 .ps1，含中文会乱码并导致解析失败）。
- Rust 测试用内建 `cargo test`，不新增 dev-dependency。
- `is_ipv4` 收紧为「只接受规范十进制 IPv4」：拒绝空段、非数字、越界、前导零、多余段。

## 架构

### 1. PowerShell 拆分

新建 `scripts/ip-core.ps1`，内容为 `$CloudflareRanges` 加 5 个纯函数（`ConvertTo-IpString`、`ConvertFrom-IpString`、`Test-IpInCidr`、`Test-IpInCloudflare`、`Get-CfCandidatePool`）。optimization.ps1 在原定义处改为 `. "$PSScriptRoot\ip-core.ps1"`——dot-source 使变量与函数进入当前作用域，后续 `Get-CfOptimize`（第 1568 行）与 `cf-apply` 校验（第 2060 行）的引用不受影响。

**部署约束**：`src-tauri/tauri.conf.json` 的 `bundle.resources` 目前只列出 `../scripts/optimization.ps1` 一个文件，安装后落在安装目录的 `_up_` 子目录里。拆出 `ip-core.ps1` 后必须同时把 `../scripts/ip-core.ps1` 加进 `bundle.resources`，否则安装版会在 dot-source 处找不到文件，导致每条操作都失败。

### 2. PowerShell 测试跑分器

- `scripts/tests/TestAssert.ps1`：`Assert-Equal` / `Assert-True` / `Assert-Throws`，内部维护通过/失败计数。
- `scripts/tests/run-tests.ps1`：dot-source `ip-core.ps1` + `TestAssert.ps1`，再 dot-source 所有 `*.tests.ps1`（用例在顶层内联执行），汇总并设置退出码。
- `scripts/tests/ip-core.tests.ps1`：具体用例。

### 3. Rust 测试

`commands.rs` 内加 `#[cfg(test)] mod tests`，覆盖 `is_ipv4` 与 `is_valid_game_id` 边界，含一条先红后绿的前导零用例。
