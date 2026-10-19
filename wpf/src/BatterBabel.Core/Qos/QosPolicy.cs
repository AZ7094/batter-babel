namespace BatterBabel.Core.Qos;

using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

/// <summary>
/// 一条 QoS 结果。字段就是 UI / BbRpcHost 需要的那几个，别再加：
/// Ok=false 时 Message 是可以直接展示的中文短句，PolicyName/Executable/Dscp 尽量填成
/// "这次操作实际涉及的策略"，方便调用方在失败后决定要不要再调 RemoveAsync。
/// </summary>
public sealed record QosResult(bool Ok, string Message, string PolicyName, string? Executable, string? Dscp, bool AlreadyPresent, string? Store = null, string? FeatureStatus = null);

/// <summary>本机 QoS 能力（真实探测值）：Available = ms_pacer 绑定全部启用；Persistent = 组策略 QoS 存储可读。</summary>
public sealed record QosCapability(bool Available, bool Persistent, string Store, string Reason);

/// <summary>
/// 「加速游戏」的 QoS 策略读写，复刻 scripts/optimization.ps1 的实际行为（BB-110/BB-111），
/// 不是另起一套规则。
///
/// 一手来源与关键差异（都以脚本实测/注释为准）：
///   * 策略命名：脚本用 <c>BatterBabel-&lt;Id&gt;</c>；本切片按要求用 <c>BatterBabel-Game-&lt;8位哈希&gt;</c>，
///     使同一 exe 幂等、不同游戏可共存。
///   * 创建参数逐字沿用 optimization.ps1:424 —— <c>-AppPathNameMatchCondition / -DSCPAction /
///     -NetworkProfile All / -PolicyStore</c>，**不带 -Precedence**（脚本不传，读回默认 127）。
///   * 存储顺序：Local（Group Policy，重启后仍在）先试，失败回落 ActiveStore（内存，重启即失）。
///     实测本机没有 Local 存储（Win32 53 "找不到网络路径"），所以在这台机器上策略只活到本次开机。
///   * 读：Get-NetQosPolicy 的 -Name 接受通配符；空的 -Name 会抛 CmdletizationQuery_NotFound_Name
///     （脚本 BB-111 实测），所以永远不带空 -Name。读不出来的存储 = 读不出来，不是"没有"。
///   * 写后读回：Create 之后必须再用名字读一次，AppPath 与 DSCP 都对上才宣告成功。
///   * 幂等：同 exe 且 DSCP 相同 -> AlreadyPresent=true，零写入（脚本 apply 的第 3 条契约）。
///   * 权限：只检测不弹 UAC。需要管理员时返回 Ok=false + 固定文案，由宿主决定怎么提权。
///
/// 明确不做：路由、防火墙、注册表、计划任务。本类只碰 QoS 策略。
/// </summary>
public static class QosPolicy
{
    /// <summary>本产品拥有的策略名前缀。清理只按这个前缀，绝不删别人的规则。</summary>
    public const string PolicyPrefix = "BatterBabel-Game-";

    /// <summary>DSCP 46 = EF（加速转发），与脚本一致。</summary>
    public const int DefaultDscp = 46;

    /// <summary>需要管理员权限时的固定文案（宿主可据此判断要不要走提权分支）。</summary>
    public const string ElevationMessage = "需要管理员权限（QoS 策略写入）";

    /// <summary>
    /// 读出本机所有 BatterBabel-Game-* 策略。只读，不需要管理员。
    /// Ok=true 表示查询本身跑通了（哪怕结果为空）；某个存储读不出来会写进 Message，
    /// 但仍然返回 Ok=true —— "读不到" 不等于 "不存在"，把两者合一会让清理结果说谎。
    /// </summary>
    public static async Task<QosResult> InspectAsync(CancellationToken ct = default)
    {
        var query = await QueryAsync(ct).ConfigureAwait(false);
        if (query.Failure is not null)
        {
            return new QosResult(false, query.Failure, string.Empty, null, null, false);
        }

        var message = Describe(query);
        if (query.Rules.Count == 0)
        {
            return new QosResult(true, message, string.Empty, null, null, false);
        }

        var first = query.Rules[0];
        return new QosResult(true, message, first.Name, first.AppPath, first.Dscp.ToString(), true);
    }

    /// <summary>
    /// 给一个游戏 exe 打 DSCP 标记。同一 exe 重复调用是安全的：
    /// 已有的策略目标一致且 DSCP 一致时直接返回 AlreadyPresent，不删不建。
    /// </summary>
    public static async Task<QosResult> ApplyAsync(string executablePath, int dscp = DefaultDscp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new QosResult(false, "没有给出游戏可执行文件路径", string.Empty, null, null, false);
        }

        if (dscp is < 0 or > 63)
        {
            return new QosResult(false, $"DSCP 取值必须在 0–63 之间（收到 {dscp}）", string.Empty, null, null, false);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath.Trim());
        }
        catch (Exception ex)
        {
            return new QosResult(false, $"可执行文件路径无效（{QosProcessOutcome.ShortError(ex)}）", string.Empty, null, null, false);
        }

        if (!File.Exists(fullPath))
        {
            // 不给不存在的 exe 建策略：脚本里 exe 是先由 Find-Game 找到才进创建路径的。
            return new QosResult(false, $"找不到可执行文件：{fullPath}", string.Empty, null, null, false);
        }

        if (!IsElevated())
        {
            return new QosResult(false, ElevationMessage, string.Empty, null, null, false);
        }

        var dscpText = dscp.ToString();
        var before = await QueryAsync(ct).ConfigureAwait(false);
        if (before.Failure is not null)
        {
            return new QosResult(false, before.Failure, string.Empty, fullPath, dscpText, false);
        }

        // 1) 同一 exe 已有策略：DSCP 相同就什么都不做（脚本 apply 的第 3 条契约）。
        var sameApp = before.Rules.FirstOrDefault(rule => SameApp(rule.AppPath, fullPath));
        if (sameApp is not null)
        {
            if (sameApp.Dscp == dscp)
            {
                return new QosResult(
                    true,
                    $"该游戏已有 DSCP {dscp} 策略（{sameApp.Name} @ {sameApp.Store}），未重复创建",
                    sameApp.Name,
                    sameApp.AppPath,
                    dscpText,
                    true);
            }

            // 目标相同但 DSCP 不同：替换，而不是并排挂两条互相打架的规则。
            var replaced = await RemoveOneAsync(sameApp, ct).ConfigureAwait(false);
            if (!replaced.Ok)
            {
                return new QosResult(false, $"无法替换旧策略 {sameApp.Name}：{replaced.Message}", sameApp.Name, sameApp.AppPath, dscpText, false);
            }
        }

        var name = PolicyPrefix + Hash8(fullPath);

        // 2) 同名策略但目标不是这个 exe（哈希碰撞或残留）：先删掉，否则 New-NetQosPolicy 报 87。
        var sameName = before.Rules.FirstOrDefault(rule => string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase));
        if (sameName is not null && !SameApp(sameName.AppPath, fullPath))
        {
            var cleared = await RemoveOneAsync(sameName, ct).ConfigureAwait(false);
            if (!cleared.Ok)
            {
                return new QosResult(false, $"无法清理同名旧策略 {sameName.Name}：{cleared.Message}", sameName.Name, fullPath, dscpText, false);
            }
        }

        var create = await RunCreateAsync(name, fullPath, dscp, ct).ConfigureAwait(false);
        if (create.Failure is not null)
        {
            return new QosResult(false, create.Failure, name, fullPath, dscpText, false);
        }

        if (string.IsNullOrEmpty(create.Store))
        {
            return new QosResult(
                false,
                $"无法创建 QoS 策略：{Join(create.Errors)}",
                name,
                fullPath,
                dscpText,
                false);
        }

        if (create.Readback is null)
        {
            return new QosResult(
                false,
                $"策略已写入 {create.Store}，但读回失败：{Join(create.Errors)}（可用 RemoveAsync 清理）",
                name,
                fullPath,
                dscpText,
                false);
        }

        var readback = create.Readback;
        if (!SameApp(readback.AppPath, fullPath) || readback.Dscp != dscp)
        {
            return new QosResult(
                false,
                $"策略读回与写入不一致：期望 {fullPath} / DSCP {dscp}，读回 {readback.AppPath} / DSCP {readback.Dscp}（可用 RemoveAsync 清理）",
                readback.Name,
                readback.AppPath,
                readback.Dscp.ToString(),
                false);
        }

        var message = replacedNote(sameApp) + $"已在 {create.Store} 建立 {readback.Name}（DSCP {dscp}）";
        if (!string.Equals(create.Store, "Local", StringComparison.OrdinalIgnoreCase))
        {
            // 这是本机实测事实：没有 Local 存储时规则只在内存里，重启就没了。
            message += "；本机 Local 策略存储不可用，该策略只在本次开机内有效";
        }

        if (before.UnreadableStores.Count > 0)
        {
            message += $"；{string.Join("、", before.UnreadableStores)} 存储无法读取，未检查其中是否已有同名策略";
        }

        return new QosResult(true, message, readback.Name, readback.AppPath, readback.Dscp.ToString(), false);

        string replacedNote(QosRule? replacedRule)
            => replacedRule is null ? string.Empty : $"已用 DSCP {dscp} 替换旧策略；";
    }

    /// <summary>
    /// 清理本机所有 BatterBabel-Game-* 策略，清理数量写进 Message。
    /// 失败语义：某条删不掉 -> Ok=false 并保留名字；某个存储读不出来 -> 不计入清除数，但明确写出来。
    /// </summary>
    public static async Task<QosResult> RemoveAsync(CancellationToken ct = default)
        => await RemovePrefixAsync(PolicyPrefix, ct).ConfigureAwait(false);

    // ---- 发布版 0.10.3 语义（BatterBabel-<gameId> / BatterBabel-route-<target>）---------------------
    //
    // 发布版 optimize 的策略名是 BatterBabel-<gameId>（不是哈希），apply-route 是
    // BatterBabel-route-<target>；status/restore 都用 BatterBabel-* 这个总前缀。这里保留旧的
    // BatterBabel-Game-<hash> 入口不动（QosCheck 工具还在用），新增按发布版命名的入口。

    /// <summary>发布版所有策略共用的总前缀（status / restore 的清理范围）。</summary>
    public const string GamePrefix = "BatterBabel-";

    /// <summary>apply-route 的策略名前缀。</summary>
    public const string RoutePrefix = "BatterBabel-route-";

    public static string GamePolicyName(string gameId) => GamePrefix + gameId;

    /// <summary>当前进程是否管理员（发布版 status.elevated 的真实来源）。</summary>
    public static bool CurrentProcessElevated() => IsElevated();

    /// <summary>
    /// 发布版 optimize：给游戏 exe 建 <c>BatterBabel-&lt;gameId&gt;</c> 的 DSCP 策略。
    /// 同名策略已存在且目标/DSCP 一致时幂等返回 AlreadyPresent（不删了重建）。
    /// 同名但目标不同（旧路径/旧版本残留）才删掉重建，否则 New-NetQosPolicy 会报 87。
    /// </summary>
    public static async Task<QosResult> ApplyForGameAsync(
        string gameId,
        string executablePath,
        int dscp = DefaultDscp,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameId) || gameId.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-' && ch != '_'))
        {
            return new QosResult(false, "游戏 id 不能用于策略名", string.Empty, null, null, false);
        }

        if (dscp is < 0 or > 63)
        {
            return new QosResult(false, $"DSCP 取值必须在 0–63 之间（收到 {dscp}）", string.Empty, null, null, false);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath.Trim());
        }
        catch (Exception ex)
        {
            return new QosResult(false, $"可执行文件路径无效（{QosProcessOutcome.ShortError(ex)}）", string.Empty, null, null, false);
        }

        if (!File.Exists(fullPath))
        {
            return new QosResult(false, $"找不到可执行文件：{fullPath}", string.Empty, null, null, false);
        }

        if (!IsElevated())
        {
            return new QosResult(false, ElevationMessage, string.Empty, null, null, false);
        }

        var name = GamePolicyName(gameId);
        var dscpText = dscp.ToString();
        var before = await QueryAsync(GamePrefix, ct).ConfigureAwait(false);
        if (before.Failure is not null)
        {
            return new QosResult(false, before.Failure, name, fullPath, dscpText, false);
        }

        var sameName = before.Rules.FirstOrDefault(rule => string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase));
        if (sameName is not null && SameApp(sameName.AppPath, fullPath) && sameName.Dscp == dscp)
        {
            return new QosResult(
                true,
                $"策略 {name} 已存在（{sameName.Store}，DSCP {dscp}），未重复创建",
                sameName.Name,
                sameName.AppPath,
                dscpText,
                true,
                sameName.Store);
        }

        if (sameName is not null)
        {
            var cleared = await RemoveOneAsync(sameName, ct).ConfigureAwait(false);
            if (!cleared.Ok)
            {
                return new QosResult(false, $"无法清理同名旧策略 {sameName.Name}：{cleared.Message}", sameName.Name, fullPath, dscpText, false, sameName.Store);
            }
        }

        var create = await RunCreateAsync(name, fullPath, dscp, ct).ConfigureAwait(false);
        if (create.Failure is not null)
        {
            return new QosResult(false, create.Failure, name, fullPath, dscpText, false);
        }

        if (string.IsNullOrEmpty(create.Store))
        {
            return new QosResult(false, $"无法创建 QoS 策略：{Join(create.Errors)}", name, fullPath, dscpText, false);
        }

        if (create.Readback is null)
        {
            return new QosResult(false, $"策略已写入 {create.Store}，但读回失败：{Join(create.Errors)}", name, fullPath, dscpText, false, create.Store);
        }

        var readback = create.Readback;
        if (!SameApp(readback.AppPath, fullPath) || readback.Dscp != dscp)
        {
            return new QosResult(
                false,
                $"策略读回与写入不一致：期望 {fullPath} / DSCP {dscp}，读回 {readback.AppPath} / DSCP {readback.Dscp}",
                readback.Name,
                readback.AppPath,
                readback.Dscp.ToString(),
                false,
                create.Store);
        }

        return new QosResult(
            true,
            $"已在 {create.Store} 建立 {readback.Name}（DSCP {dscp}）",
            readback.Name,
            readback.AppPath,
            readback.Dscp.ToString(),
            false,
            create.Store);
    }

    /// <summary>发布版 restore 带 gameId：删掉 <c>BatterBabel-&lt;gameId&gt;</c>（Local + ActiveStore），并读回确认。</summary>
    public static async Task<QosResult> RemoveGameAsync(string gameId, CancellationToken ct = default)
    {
        if (!IsElevated())
        {
            return new QosResult(false, ElevationMessage, string.Empty, null, null, false);
        }

        var name = GamePolicyName(gameId);
        var before = await QueryAsync(GamePrefix, ct).ConfigureAwait(false);
        if (before.Failure is not null)
        {
            return new QosResult(false, before.Failure, name, null, null, false);
        }

        var hits = before.Rules.Where(rule => string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var hit in hits)
        {
            var one = await RemoveOneAsync(hit, ct).ConfigureAwait(false);
            if (!one.Ok)
            {
                return new QosResult(false, $"删除 {hit.Name}@{hit.Store} 失败：{one.Message}", name, hit.AppPath, null, false);
            }
        }

        var after = await QueryAsync(GamePrefix, ct).ConfigureAwait(false);
        if (after.Failure is not null)
        {
            return new QosResult(false, $"删除后无法复查：{after.Failure}", name, null, null, false);
        }

        var left = after.Rules.Count(rule => string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase));
        if (left > 0)
        {
            return new QosResult(false, $"{name} 仍有 {left} 条残留", name, null, null, false);
        }

        return new QosResult(true, $"已删除 {name}（{hits.Length} 条）", name, null, null, hits.Length > 0);
    }

    /// <summary>发布版 status.activePolicies：真实读 Get-NetQosPolicy，返回 BatterBabel-* 的策略名。</summary>
    public static async Task<(bool Ok, IReadOnlyList<string> Names, string Message)> ActivePoliciesAsync(CancellationToken ct = default)
    {
        var query = await QueryAsync(GamePrefix, ct).ConfigureAwait(false);
        if (query.Failure is not null)
        {
            return (false, Array.Empty<string>(), query.Failure);
        }

        var names = query.Rules
            .Select(rule => rule.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (true, names, names.Length == 0 ? "没有 BatterBabel-* 策略" : $"{names.Length} 条策略");
    }

    /// <summary>
    /// 读回单个游戏策略的真实参数（自测用，只读）：名字、AppPath、DSCP、Store。
    /// 用于证明 optimize 建出来的规则能被 Get-NetQosPolicy 读回来（而不是相信创建命令的退出码）。
    /// </summary>
    public static async Task<(bool Ok, string? Name, string? AppPath, int Dscp, string? Store, string Message)> InspectGameAsync(
        string gameId,
        CancellationToken ct = default)
    {
        var name = GamePolicyName(gameId);
        var query = await QueryAsync(GamePrefix, ct).ConfigureAwait(false);
        if (query.Failure is not null)
        {
            return (false, null, null, -1, null, query.Failure);
        }

        var rule = query.Rules.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return rule is null
            ? (true, null, null, -1, null, $"{name} 不存在")
            : (true, rule.Name, rule.AppPath, rule.Dscp, rule.Store, "ok");
    }

    /// <summary>发布版 restore 不带 gameId：删掉所有 BatterBabel-* 策略。</summary>
    public static async Task<QosResult> RemoveAllAsync(CancellationToken ct = default)
        => await RemovePrefixAsync(GamePrefix, ct).ConfigureAwait(false);

    private const string CapabilityScript = """
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$bindings = @(Get-NetAdapterBinding -ComponentID 'ms_pacer' -ErrorAction SilentlyContinue)
$enabled = @($bindings | Where-Object { $_.Enabled })
$available = ($bindings.Count -gt 0 -and $enabled.Count -eq $bindings.Count)
$local = $false
try { Get-NetQosPolicy -PolicyStore Local -ErrorAction Stop | Out-Null; $local = $true } catch { $local = $false }
[pscustomobject]@{ available = $available; persistent = $local; bindings = $bindings.Count; enabled = $enabled.Count } | ConvertTo-Json -Compress
""";

    /// <summary>
    /// 真实探测本机 QoS 能力（对应发布版 Get-QosCapability）：ms_pacer 绑定是否全部启用、
    /// 组策略 QoS 存储（PolicyStore Local）是否可读。读不到就是读不到，不冒充可用。
    /// </summary>
    public static async Task<QosCapability> CapabilityAsync(CancellationToken ct = default)
    {
        var outcome = await QosPowerShell.RunAsync(CapabilityScript, new Dictionary<string, string>(), ct).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            return new QosCapability(false, false, "ActiveStore", "QoS 能力探测失败：" + FailureText(outcome));
        }

        try
        {
            using var document = JsonDocument.Parse(ExtractJson(outcome.StdOut));
            var root = document.RootElement;
            var available = root.TryGetProperty("available", out var availableElement) && availableElement.ValueKind == JsonValueKind.True;
            var persistent = root.TryGetProperty("persistent", out var persistentElement) && persistentElement.ValueKind == JsonValueKind.True;
            var bindings = root.TryGetProperty("bindings", out var bindingsElement) && bindingsElement.TryGetInt32(out var bindingCount) ? bindingCount : 0;
            var enabled = root.TryGetProperty("enabled", out var enabledElement) && enabledElement.TryGetInt32(out var enabledCount) ? enabledCount : 0;
            var store = persistent ? "Local" : "ActiveStore";
            var reason = available
                ? persistent
                    ? "ms_pacer 已全部启用，组策略 QoS 存储可用；写入策略需要管理员权限"
                    : "ms_pacer 已全部启用；本机没有可用的组策略 QoS 存储，策略写入活动存储，重启后由开机恢复重施；写入需要管理员权限"
                : $"未找到已启用的 ms_pacer 绑定（{enabled}/{bindings} 启用），QoS 标记可能不会生效；写入仍需管理员权限";
            return new QosCapability(available, persistent, store, reason);
        }
        catch (Exception ex)
        {
            return new QosCapability(false, false, "ActiveStore", "无法解析 QoS 能力探测结果（" + ex.Message + "）");
        }
    }

    /// <summary>
    /// apply-route 转发到 S12 的 <see cref="RouteQosPolicy"/>：读回校验 / fail-closed 回滚只有一处实现，
    /// 这里仅做返回类型适配（历史调用点保留，避免两套 IP 条件逻辑并存）。
    /// </summary>
    public static async Task<QosResult> ApplyRouteAsync(string target, string ip, int dscp = DefaultDscp, CancellationToken ct = default)
    {
        var result = await RouteQosPolicy.ApplyAsync(new[] { new RouteQosEntry(target, target, ip) }, dscp, ct).ConfigureAwait(false);
        var item = result.Items.FirstOrDefault();
        return result.Ok
            ? new QosResult(true, item?.Message ?? result.Message, item?.PolicyName ?? RoutePrefix + target, null, dscp.ToString(), item?.AlreadyPresent ?? false, "ActiveStore")
            : new QosResult(false, item?.Message ?? result.Message, item?.PolicyName ?? RoutePrefix + target, null, dscp.ToString(), false, "ActiveStore", item is { HasDstPrefix: false } ? "BLOCKED_ON_PLATFORM" : null);
    }

    /// <summary>清理 BatterBabel-route-* 策略（自测/回退用，发布版没有单独入口）。</summary>
    public static async Task<QosResult> RemoveRoutesAsync(CancellationToken ct = default)
        => await RemovePrefixAsync(RoutePrefix, ct).ConfigureAwait(false);

    private static async Task<QosResult> RemovePrefixAsync(string prefix, CancellationToken ct)
    {
        if (!IsElevated())
        {
            return new QosResult(false, ElevationMessage, string.Empty, null, null, false);
        }

        var before = await QueryAsync(prefix, ct).ConfigureAwait(false);
        if (before.Failure is not null)
        {
            return new QosResult(false, before.Failure, string.Empty, null, null, false);
        }

        var errors = new List<string>();
        var removed = 0;
        foreach (var rule in before.Rules)
        {
            var one = await RemoveOneAsync(rule, ct).ConfigureAwait(false);
            if (one.Ok) { removed++; }
            else { errors.Add($"{rule.Name}@{rule.Store}: {one.Message}"); }
        }

        var after = await QueryAsync(prefix, ct).ConfigureAwait(false);
        var remaining = after.Failure is null ? after.Rules.Count : -1;

        var message = $"已清理 {removed} 条 {prefix}* 策略";
        message += after.Failure is not null
            ? $"；清理后无法复查（{after.Failure}）"
            : remaining == 0 ? "；已无残留" : $"；仍有 {remaining} 条残留";

        if (before.UnreadableStores.Count > 0 || (after.Failure is null && after.UnreadableStores.Count > 0))
        {
            var stores = before.UnreadableStores.Union(after.UnreadableStores, StringComparer.OrdinalIgnoreCase);
            message += $"；{string.Join("、", stores)} 存储无法读取，其中的策略未被检查";
        }

        if (errors.Count > 0)
        {
            message += $"；删除失败：{string.Join("；", errors)}";
        }

        var ok = errors.Count == 0 && remaining == 0;
        var first = before.Rules.FirstOrDefault();
        return new QosResult(ok, message, first?.Name ?? string.Empty, first?.AppPath, first?.Dscp.ToString(), before.Rules.Count > 0);
    }

    // ---- 内部实现 ---------------------------------------------------------------------------

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // 拿不到令牌就按"没有权限"处理：宁可让用户看到提权提示，也不要静默假设已提权。
            return false;
        }
    }

    /// <summary>策略名里的 8 位哈希。输入先规范化（反斜杠 + 小写），路径大小写不同不会产生两条规则。</summary>
    private static string Hash8(string fullPath)
    {
        var normalized = fullPath.Replace('/', '\\').ToLowerInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest)[..8].ToLowerInvariant();
    }

    private static bool SameApp(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) { return false; }
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Describe(QosQuery query)
    {
        var parts = new List<string>();
        parts.Add(query.Rules.Count == 0
            ? $"未发现 {PolicyPrefix}* 策略"
            : $"发现 {query.Rules.Count} 条 {PolicyPrefix}* 策略：" + string.Join("；", query.Rules.Select(r =>
                $"{r.Name} @ {r.Store}（{r.AppPath}，DSCP {r.Dscp}）")));
        parts.Add(query.UnreadableStores.Count == 0
            ? "已读取：Local、ActiveStore"
            : $"{string.Join("、", query.UnreadableStores)} 存储无法读取");
        return string.Join("（", parts[0], string.Join("；", parts.Skip(1))) + "）";
    }

    private static string Join(IReadOnlyList<string> errors)
        => errors.Count == 0 ? "原因未知" : string.Join("；", errors);

    private static Task<QosQuery> QueryAsync(CancellationToken ct) => QueryAsync(PolicyPrefix, ct);

    private static async Task<QosQuery> QueryAsync(string prefix, CancellationToken ct)
    {
        var outcome = await QosPowerShell.RunAsync(
            QosScripts.Query,
            new Dictionary<string, string> { [QosScripts.PrefixVariable] = prefix },
            ct).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            return QosQuery.Failed(FailureText(outcome));
        }

        try
        {
            using var document = JsonDocument.Parse(ExtractJson(outcome.StdOut));
            var root = document.RootElement;
            var rules = new List<QosRule>();
            var errors = new List<string>();
            var unreadable = new List<string>();

            if (root.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rulesElement.EnumerateArray())
                {
                    rules.Add(new QosRule(
                        Text(item, "name"),
                        Text(item, "store"),
                        Text(item, "app"),
                        item.TryGetProperty("dscp", out var dscp) && dscp.TryGetInt32(out var value) ? value : -1));
                }
            }

            if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in errorsElement.EnumerateArray())
                {
                    unreadable.Add(Text(item, "store"));
                    errors.Add($"{Text(item, "store")}: {Text(item, "error")}");
                }
            }

            if (rules.Count == 0 && errors.Count == 2)
            {
                // 两个存储都读不出来：这才是查询失败。只读不出来一个时仍然可以回答另一半。
                return QosQuery.Failed($"无法读取任何 QoS 策略存储：{string.Join("；", errors)}");
            }

            rules.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new QosQuery(rules, unreadable.AsReadOnly(), errors.AsReadOnly(), null);
        }
        catch (Exception ex)
        {
            return QosQuery.Failed($"无法解析 QoS 查询结果（{QosProcessOutcome.ShortError(ex)}）");
        }
    }

    private static async Task<QosCreate> RunCreateAsync(string name, string app, int dscp, CancellationToken ct)
    {
        var outcome = await QosPowerShell.RunAsync(
            QosScripts.Create,
            new Dictionary<string, string>
            {
                [QosScripts.NameVariable] = name,
                [QosScripts.AppVariable] = app,
                [QosScripts.DscpVariable] = dscp.ToString(),
            },
            ct).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            return QosCreate.Failed(FailureText(outcome));
        }

        try
        {
            using var document = JsonDocument.Parse(ExtractJson(outcome.StdOut));
            var root = document.RootElement;
            var store = Text(root, "store");
            var errors = new List<string>();
            if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in errorsElement.EnumerateArray()) { errors.Add(Text(item, "error")); }
            }

            QosRule? readback = null;
            if (root.TryGetProperty("readback", out var readbackElement) && readbackElement.ValueKind == JsonValueKind.Object)
            {
                readback = new QosRule(
                    Text(readbackElement, "name"),
                    store,
                    Text(readbackElement, "app"),
                    readbackElement.TryGetProperty("dscp", out var value) && value.TryGetInt32(out var parsed) ? parsed : -1);
            }

            return new QosCreate(store, readback, errors.AsReadOnly(), null);
        }
        catch (Exception ex)
        {
            return QosCreate.Failed($"无法解析 QoS 创建结果（{QosProcessOutcome.ShortError(ex)}）");
        }
    }

    private static async Task<(bool Ok, string Message)> RemoveOneAsync(QosRule rule, CancellationToken ct)
    {
        var outcome = await QosPowerShell.RunAsync(
            QosScripts.RemoveOne,
            new Dictionary<string, string>
            {
                [QosScripts.NameVariable] = rule.Name,
                [QosScripts.StoreVariable] = rule.Store,
            },
            ct).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            return (false, FailureText(outcome));
        }

        try
        {
            using var document = JsonDocument.Parse(ExtractJson(outcome.StdOut));
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            return ok ? (true, string.Empty) : (false, Text(root, "error"));
        }
        catch (Exception ex)
        {
            return (false, QosProcessOutcome.ShortError(ex));
        }
    }

    /// <summary>stdout 里除 JSON 之外不该有内容；真有多余行时取第一个 '{' 到最后一个 '}'。</summary>
    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{')) { return trimmed; }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start) { return trimmed[start..(end + 1)]; }
        throw new InvalidOperationException("PowerShell 没有返回 JSON");
    }

    private static string FailureText(QosProcessOutcome outcome)
    {
        if (outcome.Failure is not null) { return outcome.Failure; }
        var detail = string.IsNullOrWhiteSpace(outcome.StdErr) ? outcome.StdOut.Trim() : outcome.StdErr.Trim();
        detail = detail.Replace('\r', ' ').Replace('\n', ' ');
        if (detail.Length > 180) { detail = detail[..180] + "…"; }
        return $"PowerShell 调用失败（退出码 {outcome.ExitCode}）：{(detail.Length == 0 ? "无输出" : detail)}";
    }

    private static string Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private sealed record QosRule(string Name, string Store, string AppPath, int Dscp);

    private sealed record QosQuery(
        IReadOnlyList<QosRule> Rules,
        IReadOnlyList<string> UnreadableStores,
        IReadOnlyList<string> Errors,
        string? Failure)
    {
        internal static QosQuery Failed(string failure) => new(
            Array.Empty<QosRule>(), Array.Empty<string>(), Array.Empty<string>(), failure);
    }

    private sealed record QosCreate(string Store, QosRule? Readback, IReadOnlyList<string> Errors, string? Failure)
    {
        internal static QosCreate Failed(string failure) => new(string.Empty, null, Array.Empty<string>(), failure);
    }
}
