namespace BatterBabel.Core.Qos;

using System.Globalization;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

/// <summary>一条要打 DSCP 的路由目标。Tag 用来让策略名可读；Domain 只作说明，不进策略。</summary>
public sealed record RouteQosEntry(string Tag, string Domain, string Ip);

/// <summary>
/// 一条路由策略的单项结果。Ok=false 时 Message 是可以直接展示的中文短句；
/// Ip 一律填规范化后的 IPv4（校验失败时填调用方给的原值），Dscp 填本次涉及的 DSCP。
/// HasDstPrefix / DstPrefix 是读回的目标 IP 条件：上层据此区分「只对目标 IP 生效」与
/// 「没有目标条件 = 匹配全部流量（catch-all）」。DstPrefix 为空而策略存在时 HasDstPrefix=false。
/// </summary>
public sealed record RouteQosItem(
    bool Ok,
    string Message,
    string PolicyName,
    string Ip,
    string? Dscp,
    bool AlreadyPresent,
    bool HasDstPrefix = false,
    string? DstPrefix = null);

/// <summary>
/// 一批路由策略的结果。Applied=本次新建，Skipped=已存在且一致所以没动，Removed=替换/清理掉的旧策略。
/// Items 与传入的 entries 一一对应（批量级失败时为空，见 ApplyAsync 的 64 条上限）。
/// </summary>
public sealed record RouteQosResult(bool Ok, string Message, int Applied, int Skipped, int Removed, IReadOnlyList<RouteQosItem> Items);

/// <summary>
/// 「按目标 IP 加速线路」的 QoS 策略读写，对应发布版 Tauri 的 apply-route 动作
/// （scripts/optimization.ps1 的 <c>New-QosPolicyRule -IpPrefix</c> 分支）：
/// 只给去往目标 /32 的流量打 DSCP 46（EF），不动 hosts / DNS / 代理。
///
/// 与按 exe 建策略的 <see cref="QosPolicy"/> 并列，共用同一个 PowerShell 边界
/// （<see cref="QosPowerShell"/>：-EncodedCommand、参数经环境变量、20 s 超时、无窗口）。
///
/// 行为契约：
///   * 策略名确定且可读：<c>BatterBabel-Route-&lt;tag&gt;-&lt;ip&gt;</c>，tag 先小写化，IP 里的点保留，
///     总长 ≤ <see cref="MaxPolicyNameLength"/>（因此 tag 实际最长 27 个字符）。
///     同一 (tag, ip) 永远得到同一个名字，读回顺序、进程重启都不影响。
///   * 幂等：同名策略的 IP 与 DSCP 都一致时 AlreadyPresent=true，零写入。
///   * 替换：同名但目标或 DSCP 不同 -> 先删再建，避免 New-NetQosPolicy 报 87。
///   * 存储固定 ActiveStore：本机没有 Local 存储（Get-NetQosPolicy -Store Local 报 Win32 53
///     "找不到网络路径"），所以策略只活到本次开机，重启后由开机恢复重施。
///   * 校验：IP 必须是规范 IPv4（拒绝 010.1.1.1 这类前导零）、tag 只允许小写字母/数字/连字符、
///     一次最多 <see cref="MaxEntries"/> 条。不合法条目只让该条 Ok=false，不影响同批其它条目
///     （只有整批超过上限时才在写入前整体拒绝）。
///   * 任何一条失败都不抛异常：失败原因汇总进 RouteQosResult / RouteQosItem.Message。
/// </summary>
public static class RouteQosPolicy
{
    /// <summary>本产品路由策略的名字前缀。清理只按这个前缀，绝不删别人的规则。</summary>
    public const string PolicyPrefix = "BatterBabel-Route-";

    /// <summary>DSCP 46 = EF（加速转发），与发布版脚本的 apply-route 一致。</summary>
    public const int DefaultDscp = 46;

    /// <summary>一次调用最多接受的路由条数。超过时整批拒绝，不做任何写入。</summary>
    public const int MaxEntries = 64;

    /// <summary>策略名总长上限。Windows 没有硬限制，这里取一个便于 UI/日志显示的确定值。</summary>
    public const int MaxPolicyNameLength = 60;

    private const string StoreName = "ActiveStore";
    private const string RebootNote = "存储 ActiveStore：重启后失效，由开机恢复重施";

    /// <summary>
    /// 本机实测（Windows 10.0.26200）的 platform defect：New-NetQosPolicy 接受
    /// -IPDstPrefixMatchCondition 却把它丢掉，读回为空。这样的策略没有任何目标条件，
    /// 等于给全部流量打 DSCP —— 所以创建后读回不成立时一律回滚，绝不保留。
    /// </summary>
    internal const string PlatformIpConditionDefect =
        "平台未保存目标 IP 条件（New-NetQosPolicy -IPDstPrefixMatchCondition 读回为空），该策略会匹配全部流量";

    /// <summary>
    /// 给一批目标 IP 建 /32 的 DSCP 策略。已有的不重复建；同名的旧策略先删再建。
    /// 读写都需要管理员，所以入口先检查提权，未提权时返回 Ok=false + 固定文案，
    /// 由宿主决定要不要走提权分支（与 <see cref="QosPolicy"/> 一致，不自己弹 UAC）。
    /// </summary>
    public static async Task<RouteQosResult> ApplyAsync(
        IReadOnlyList<RouteQosEntry> entries,
        int dscp = DefaultDscp,
        CancellationToken ct = default)
    {
        if (entries is null || entries.Count == 0)
        {
            return Fail("没有给出路由条目");
        }

        if (entries.Count > MaxEntries)
        {
            return Fail($"一次最多 {MaxEntries} 条路由（收到 {entries.Count} 条），未做任何写入");
        }

        if (dscp is < 0 or > 63)
        {
            return Fail($"DSCP 取值必须在 0–63 之间（收到 {dscp}）");
        }

        if (!IsElevated())
        {
            return Fail(QosPolicy.ElevationMessage);
        }

        var before = await QueryAsync(ct).ConfigureAwait(false);
        if (before.Failure is not null)
        {
            return Fail(before.Failure);
        }

        var rules = before.Rules.ToList();
        var items = new List<RouteQosItem>(entries.Count);
        var dscpText = dscp.ToString(CultureInfo.InvariantCulture);
        var applied = 0;
        var skipped = 0;
        var removed = 0;
        var rolledBack = 0;

        foreach (var entry in entries)
        {
            if (!TryCompile(entry, out var ip, out var name, out var error))
            {
                items.Add(new RouteQosItem(false, error, string.Empty, entry?.Ip?.Trim() ?? string.Empty, null, false));
                continue;
            }

            // 1) 同名且目标、DSCP 一致：脚本 apply 的第 3 条契约，零写入。
            var existing = rules.FirstOrDefault(rule => NameEquals(rule.Name, name));
            if (existing is not null && PrefixEquals(existing.IpPrefix, ip) && existing.Dscp == dscp)
            {
                skipped++;
                items.Add(new RouteQosItem(
                    true,
                    $"策略已存在（DSCP {dscp}），未重复创建：{existing.Name}",
                    existing.Name,
                    ip,
                    dscpText,
                    true,
                    HasDstPrefix: true,
                    DstPrefix: existing.IpPrefix));
                continue;
            }

            // 2) 同名但目标/DSCP 不同（残留或换 IP）：先删掉，否则 New-NetQosPolicy 报 87。
            if (existing is not null)
            {
                var deleted = await RemoveOneAsync(existing.Name, ct).ConfigureAwait(false);
                if (!deleted.Ok)
                {
                    items.Add(new RouteQosItem(
                        false,
                        $"同名策略 {existing.Name} 的目标或 DSCP 不同，替换前删除失败：{deleted.Message}",
                        existing.Name,
                        ip,
                        dscpText,
                        false,
                        HasDstPrefix: !string.IsNullOrWhiteSpace(existing.IpPrefix),
                        DstPrefix: existing.IpPrefix));
                    continue;
                }

                removed++;
                rules.Remove(existing);
            }

            // 3) 创建 + 写后读回：创建成功不等于策略真的在存储里，读回的形状才是事实。
            var create = await CreateAsync(name, ip, dscp, ct).ConfigureAwait(false);
            if (!create.Ok || create.Readback is null)
            {
                items.Add(new RouteQosItem(
                    false,
                    create.Message,
                    name,
                    ip,
                    dscpText,
                    false,
                    HasDstPrefix: !string.IsNullOrWhiteSpace(create.Readback?.IpPrefix),
                    DstPrefix: create.Readback?.IpPrefix));
                continue;
            }

            var readback = create.Readback;
            if (!NameEquals(readback.Name, name) || !PrefixEquals(readback.IpPrefix, ip) || readback.Dscp != dscp)
            {
                // fail-closed：没有目标 IP 条件的策略会匹配全部流量，绝不留着。
                var rollback = await RemoveOneAsync(name, ct).ConfigureAwait(false);
                if (rollback.Ok) { rolledBack++; }
                var rollbackNote = rollback.Ok
                    ? "已立即回滚删除"
                    : $"回滚失败，需人工清理：{rollback.Message}";
                var reason = string.IsNullOrWhiteSpace(readback.IpPrefix) ? PlatformIpConditionDefect : "创建后读回不一致";
                items.Add(new RouteQosItem(
                    false,
                    $"{reason}（实际：{readback.Name} / 目标={DescribePrefix(readback.IpPrefix)} / DSCP {readback.Dscp}；{rollbackNote}）",
                    name,
                    ip,
                    dscpText,
                    false,
                    HasDstPrefix: !string.IsNullOrWhiteSpace(readback.IpPrefix),
                    DstPrefix: readback.IpPrefix));
                continue;
            }

            rules.Add(readback);
            applied++;
            items.Add(new RouteQosItem(
                true,
                $"已创建 {name} → {ip}/32 DSCP {dscp}（{StoreName}，重启后失效，由开机恢复重施）",
                name,
                ip,
                dscpText,
                false,
                HasDstPrefix: true,
                DstPrefix: readback.IpPrefix));
        }

        var failed = items.Count(item => !item.Ok);
        var parts = new List<string> { $"已应用 {applied} 条，跳过 {skipped} 条" };
        if (removed > 0) { parts.Add($"替换 {removed} 条旧策略"); }
        if (rolledBack > 0) { parts.Add($"回滚 {rolledBack} 条未通过读回校验的策略"); }
        if (failed > 0) { parts.Add($"{failed} 条失败（见条目消息）"); }
        parts.Add(RebootNote);
        return new RouteQosResult(failed == 0, string.Join("；", parts), applied, skipped, removed, items.AsReadOnly());
    }

    /// <summary>
    /// 删除本机所有 BatterBabel-Route-* 策略（只认这个前缀）。删完再读一次：
    /// 复查读不出来 -> Ok=false；仍有残留 -> Ok=false 并写出条数。空表也是 Ok=true。
    /// </summary>
    public static async Task<RouteQosResult> RemoveAsync(CancellationToken ct = default)
    {
        if (!IsElevated())
        {
            return Fail(QosPolicy.ElevationMessage);
        }

        var before = await QueryAsync(ct).ConfigureAwait(false);
        if (before.Failure is not null)
        {
            return Fail(before.Failure);
        }

        var items = new List<RouteQosItem>(before.Rules.Count);
        var removed = 0;
        foreach (var rule in before.Rules)
        {
            var ip = PrefixIp(rule.IpPrefix);
            var dscpText = rule.Dscp >= 0 ? rule.Dscp.ToString(CultureInfo.InvariantCulture) : null;
            var one = await RemoveOneAsync(rule.Name, ct).ConfigureAwait(false);
            if (!one.Ok)
            {
                items.Add(new RouteQosItem(
                    false,
                    $"删除 {rule.Name} 失败：{one.Message}",
                    rule.Name,
                    ip,
                    dscpText,
                    false,
                    HasDstPrefix: !string.IsNullOrWhiteSpace(rule.IpPrefix),
                    DstPrefix: rule.IpPrefix));
                continue;
            }

            removed++;
            items.Add(new RouteQosItem(
                true,
                $"已删除 {rule.Name}（{DescribePrefix(rule.IpPrefix)}，DSCP {dscpText ?? "未知"}）",
                rule.Name,
                ip,
                dscpText,
                false,
                HasDstPrefix: !string.IsNullOrWhiteSpace(rule.IpPrefix),
                DstPrefix: rule.IpPrefix));
        }

        var after = await QueryAsync(ct).ConfigureAwait(false);
        var remaining = after.Failure is null ? after.Rules.Count : -1;
        var failed = items.Count(item => !item.Ok);
        var message = $"已清理 {removed} 条 {PolicyPrefix}* 策略";
        message += after.Failure is not null
            ? $"；清理后无法复查（{after.Failure}）"
            : remaining == 0 ? "；已无残留" : $"；仍有 {remaining} 条残留";
        message += $"（{RebootNote}）";

        return new RouteQosResult(failed == 0 && remaining == 0, message, 0, 0, removed, items.AsReadOnly());
    }

    /// <summary>
    /// 读出本机所有 BatterBabel-Route-* 策略（策略名 + IP + DSCP）。只读，不需要管理员。
    /// 每条策略一个 Item（AlreadyPresent=true 表示"读到了"）。读不出来就是失败，不是"没有"。
    /// </summary>
    public static async Task<RouteQosResult> InspectAsync(CancellationToken ct = default)
    {
        var query = await QueryAsync(ct).ConfigureAwait(false);
        if (query.Failure is not null)
        {
            return Fail(query.Failure);
        }

        var items = new List<RouteQosItem>(query.Rules.Count);
        foreach (var rule in query.Rules)
        {
            var dscpText = rule.Dscp >= 0 ? rule.Dscp.ToString(CultureInfo.InvariantCulture) : null;
            var hasPrefix = !string.IsNullOrWhiteSpace(rule.IpPrefix);
            items.Add(new RouteQosItem(
                hasPrefix,
                hasPrefix
                    ? $"{rule.Name} → {DescribePrefix(rule.IpPrefix)}，DSCP {dscpText ?? "未知"} @ {StoreName}"
                    : $"{rule.Name} 没有目标 IP 条件（会匹配全部流量），DSCP {dscpText ?? "未知"} @ {StoreName}",
                rule.Name,
                PrefixIp(rule.IpPrefix),
                dscpText,
                true,
                HasDstPrefix: hasPrefix,
                DstPrefix: rule.IpPrefix));
        }

        var catchAll = items.Count(item => !item.HasDstPrefix);
        var message = items.Count == 0
            ? $"未发现 {PolicyPrefix}* 策略（{StoreName}）"
            : $"发现 {items.Count} 条 {PolicyPrefix}* 策略：" +
              string.Join("；", items.Select(item => $"{item.PolicyName} → {DescribePrefix(item.DstPrefix)} DSCP {item.Dscp ?? "未知"}")) +
              (catchAll > 0 ? $"；⚠ {catchAll} 条没有目标 IP 条件（匹配全部流量）" : string.Empty) +
              $"（{RebootNote}）";
        return new RouteQosResult(items.All(item => item.Ok), message, 0, 0, 0, items.AsReadOnly());
    }

    // ---- 命名与校验 -------------------------------------------------------------------------

    /// <summary>
    /// 把一条 entry 编译成 (规范化 IP, 策略名)。校验失败时 error 是可直接展示的中文原因。
    /// tag 先 trim + 小写化再校验，所以 "Cloudflare" 与 "cloudflare" 得到同一条策略。
    /// </summary>
    private static bool TryCompile(RouteQosEntry? entry, out string ip, out string name, out string error)
    {
        ip = string.Empty;
        name = string.Empty;
        error = string.Empty;

        if (entry is null)
        {
            error = "路由条目为空";
            return false;
        }

        var tag = (entry.Tag ?? string.Empty).Trim().ToLowerInvariant();
        if (tag.Length == 0)
        {
            error = "路由条目的 tag 为空";
            return false;
        }

        foreach (var ch in tag)
        {
            if ((ch is < 'a' or > 'z') && (ch is < '0' or > '9') && ch != '-')
            {
                error = $"tag 只允许小写字母、数字和连字符（收到 \"{tag}\"）";
                return false;
            }
        }

        if (!TryNormalizeIp(entry.Ip, out ip, out error))
        {
            return false;
        }

        // 17 (前缀) + tag + 1 (连字符) + 最多 15 (IPv4) ≤ 60 -> tag ≤ 27。
        var maxTagLength = MaxPolicyNameLength - PolicyPrefix.Length - 1 - 15;
        if (tag.Length > maxTagLength)
        {
            error = $"tag 过长（{tag.Length} 个字符），策略名总长需 ≤ {MaxPolicyNameLength}，tag 最多 {maxTagLength} 个字符";
            return false;
        }

        name = PolicyPrefix + tag + "-" + ip;
        return true;
    }

    /// <summary>
    /// 规范 IPv4 校验：四段十进制、每段 0–255、不允许多余字符、不允许前导零（"010.1.1.1" 拒绝）。
    /// 通过时 normalized 是 new IPAddress(...).ToString() 的规范写法。
    /// 刻意不用 IPAddress.TryParse：它对 "010.1.1.1"、"1.2.3" 这类历史写法的接受程度随框架版本变化，
    /// 而策略名必须确定，所以自己判。
    /// </summary>
    private static bool TryNormalizeIp(string? raw, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "缺少目标 IP";
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 4)
        {
            error = $"目标 IP 不是 IPv4 地址：{text}";
            return false;
        }

        var bytes = new byte[4];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length is 0 or > 3)
            {
                error = $"目标 IP 不是 IPv4 地址：{text}";
                return false;
            }

            foreach (var ch in part)
            {
                if (ch is < '0' or > '9')
                {
                    error = $"目标 IP 不是 IPv4 地址：{text}";
                    return false;
                }
            }

            if (part.Length > 1 && part[0] == '0')
            {
                error = $"IPv4 段不允许前导零：{text}";
                return false;
            }

            var value = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
            if (value > 255)
            {
                error = $"IPv4 段超出 0–255：{text}";
                return false;
            }

            bytes[index] = (byte)value;
        }

        normalized = new IPAddress(bytes).ToString();
        return true;
    }

    /// <summary>把读回的 "203.0.113.5/32" 归一化成 "203.0.113.5"；读不懂时原样返回。</summary>
    private static string PrefixIp(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) { return string.Empty; }
        var text = prefix.Trim();
        var slash = text.IndexOf('/');
        var address = slash >= 0 ? text[..slash] : text;
        return TryNormalizeIp(address, out var ip, out _) ? ip : address;
    }

    /// <summary>读回的 IP 前缀是否就是这个 /32 地址。非 /32 一律不算命中。</summary>
    private static bool PrefixEquals(string? prefix, string ip)
    {
        var text = (prefix ?? string.Empty).Trim();
        var slash = text.IndexOf('/');
        if (slash >= 0 && text[(slash + 1)..] != "32") { return false; }
        var address = slash >= 0 ? text[..slash] : text;
        return TryNormalizeIp(address, out var normalized, out _)
               && string.Equals(normalized, ip, StringComparison.Ordinal);
    }

    /// <summary>策略名比较：Windows 读回的名字是小写，未提权时建的名字保留大小写，所以忽略大小写比。</summary>
    private static bool NameEquals(string? left, string? right)
        => string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static string DescribePrefix(string? prefix)
    {
        var text = (prefix ?? string.Empty).Trim();
        if (text.Length == 0) { return "(无 IP 条件)"; }
        return text.EndsWith("/32", StringComparison.Ordinal) ? text : $"{text}（非 /32）";
    }

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

    // ---- PowerShell 读写 --------------------------------------------------------------------

    private static async Task<QosRouteQuery> QueryAsync(CancellationToken ct)
    {
        var outcome = await QosPowerShell.RunAsync(
            RouteScripts.Query,
            new Dictionary<string, string> { [RouteScripts.PrefixVariable] = PolicyPrefix },
            ct).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            return QosRouteQuery.Failed(FailureText(outcome));
        }

        try
        {
            using var document = JsonDocument.Parse(ExtractJson(outcome.StdOut));
            var root = document.RootElement;
            var error = Text(root, "error");
            if (error.Length > 0)
            {
                // 读不出来不是"没有策略"：把错误原样上报，避免把失败说成"已清理干净"。
                return QosRouteQuery.Failed($"无法读取 {PolicyPrefix}* 策略：{error}");
            }

            var rules = new List<QosRouteRule>();
            if (root.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rulesElement.EnumerateArray())
                {
                    rules.Add(new QosRouteRule(
                        Text(item, "name"),
                        Text(item, "ip"),
                        item.TryGetProperty("dscp", out var dscp) && dscp.TryGetInt32(out var value) ? value : -1));
                }
            }

            rules.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new QosRouteQuery(rules.AsReadOnly(), null);
        }
        catch (Exception ex)
        {
            return QosRouteQuery.Failed($"无法解析路由策略查询结果（{QosProcessOutcome.ShortError(ex)}）");
        }
    }

    private static async Task<QosRouteCreate> CreateAsync(string name, string ip, int dscp, CancellationToken ct)
    {
        var outcome = await QosPowerShell.RunAsync(
            RouteScripts.Create,
            new Dictionary<string, string>
            {
                [RouteScripts.NameVariable] = name,
                [RouteScripts.IpVariable] = ip,
                [RouteScripts.DscpVariable] = dscp.ToString(CultureInfo.InvariantCulture),
            },
            ct).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            return QosRouteCreate.Failed(FailureText(outcome));
        }

        try
        {
            using var document = JsonDocument.Parse(ExtractJson(outcome.StdOut));
            var root = document.RootElement;
            var error = Text(root, "error");
            if (error.Length > 0)
            {
                return QosRouteCreate.Failed(error);
            }

            if (!root.TryGetProperty("readback", out var readbackElement) || readbackElement.ValueKind != JsonValueKind.Object)
            {
                return QosRouteCreate.Failed("创建后没有读回策略");
            }

            var rule = new QosRouteRule(
                Text(readbackElement, "name"),
                Text(readbackElement, "ip"),
                readbackElement.TryGetProperty("dscp", out var dscpElement) && dscpElement.TryGetInt32(out var parsed) ? parsed : -1);
            return new QosRouteCreate(rule, string.Empty);
        }
        catch (Exception ex)
        {
            return QosRouteCreate.Failed($"无法解析路由策略创建结果（{QosProcessOutcome.ShortError(ex)}）");
        }
    }

    private static async Task<(bool Ok, string Message)> RemoveOneAsync(string name, CancellationToken ct)
    {
        var outcome = await QosPowerShell.RunAsync(
            RouteScripts.RemoveOne,
            new Dictionary<string, string> { [RouteScripts.NameVariable] = name },
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

    private static RouteQosResult Fail(string message)
        => new(false, message, 0, 0, 0, Array.Empty<RouteQosItem>());

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

    private sealed record QosRouteRule(string Name, string IpPrefix, int Dscp);

    private sealed record QosRouteQuery(IReadOnlyList<QosRouteRule> Rules, string? Failure)
    {
        internal static QosRouteQuery Failed(string failure)
            => new(Array.Empty<QosRouteRule>(), failure);
    }

    private sealed record QosRouteCreate(QosRouteRule? Readback, string Message)
    {
        internal bool Ok => Readback is not null;

        internal static QosRouteCreate Failed(string message) => new(null, message);
    }
}

/// <summary>
/// 四条脚本：查询、创建（原地读回）、删除单条。参数一律从环境变量读，脚本内不出现调用方字面量，
/// 所以策略名/IP 里的引号、空格、$、反引号都不可能变成 PowerShell 代码。
/// 存储固定 ActiveStore：本机没有 Local 存储，策略随重启消失，由开机恢复重施。
/// </summary>
internal static class RouteScripts
{
    private const string Preamble = """
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
""";

    internal const string PrefixVariable = "BB_ROUTE_PREFIX";
    internal const string NameVariable = "BB_ROUTE_NAME";
    internal const string IpVariable = "BB_ROUTE_IP";
    internal const string DscpVariable = "BB_ROUTE_DSCP";

    /// <summary>
    /// 读 ActiveStore 里本前缀的策略。-Name 接受通配符（无匹配返回 0 条不报错），
    /// 所以用 "${prefix}*" 而不是枚举全表。读失败时 error 非空 —— 调用方必须当成失败而不是"没有"。
    /// </summary>
    internal const string Query = Preamble + """
$prefix = $env:BB_ROUTE_PREFIX
$rules = New-Object System.Collections.Generic.List[object]
$errorText = ''
try {
    foreach ($policy in @(Get-NetQosPolicy -PolicyStore 'ActiveStore' -Name "${prefix}*" -ErrorAction Stop)) {
        $rules.Add([pscustomobject]@{
            name = "$($policy.Name)"
            ip   = "$($policy.IPDstPrefixMatchCondition)"
            dscp = [int]$policy.DSCPAction
        })
    }
} catch {
    $errorText = "$($_.FullyQualifiedErrorId): $($_.Exception.Message)"
}
[pscustomobject]@{ rules = $rules.ToArray(); error = $errorText } | ConvertTo-Json -Depth 5 -Compress
""";

    /// <summary>
    /// 建一条 IP 目标策略。参数与发布版脚本的 New-QosPolicyRule -IpPrefix 分支一致：
    /// -IPDstPrefixMatchCondition &lt;ip&gt;/32 / -DSCPAction / -NetworkProfile All / -PolicyStore ActiveStore。
    /// 创建后立刻用名字读回；读不回一条形状一致的策略就算失败。
    /// </summary>
    internal const string Create = Preamble + """
$name = $env:BB_ROUTE_NAME
$ip = $env:BB_ROUTE_IP
$dscp = [int]$env:BB_ROUTE_DSCP
$readback = $null
$errorText = ''
try {
    New-NetQosPolicy -Name $name -IPDstPrefixMatchCondition "$ip/32" -DSCPAction $dscp -NetworkProfile All -PolicyStore 'ActiveStore' -ErrorAction Stop | Out-Null
} catch {
    $errorText = "$($_.FullyQualifiedErrorId): $($_.Exception.Message)"
}
if (-not $errorText) {
    try {
        $found = @(Get-NetQosPolicy -PolicyStore 'ActiveStore' -Name $name -ErrorAction Stop)
        if ($found.Count -eq 1) {
            $readback = [pscustomobject]@{
                name = "$($found[0].Name)"
                ip   = "$($found[0].IPDstPrefixMatchCondition)"
                dscp = [int]$found[0].DSCPAction
            }
        } else {
            $errorText = "readback returned $($found.Count) rules"
        }
    } catch {
        $errorText = "readback: $($_.FullyQualifiedErrorId): $($_.Exception.Message)"
    }
}
[pscustomobject]@{ readback = $readback; error = $errorText } | ConvertTo-Json -Depth 5 -Compress
""";

    /// <summary>删除 ActiveStore 里的单条策略（只删调用方查出来的那个名字，不碰别的）。</summary>
    internal const string RemoveOne = Preamble + """
$name = $env:BB_ROUTE_NAME
try {
    Remove-NetQosPolicy -Name $name -PolicyStore 'ActiveStore' -Confirm:$false -ErrorAction Stop
    [pscustomobject]@{ ok = $true; error = '' } | ConvertTo-Json -Compress
} catch {
    [pscustomobject]@{ ok = $false; error = "$($_.FullyQualifiedErrorId): $($_.Exception.Message)" } | ConvertTo-Json -Compress
}
""";
}
