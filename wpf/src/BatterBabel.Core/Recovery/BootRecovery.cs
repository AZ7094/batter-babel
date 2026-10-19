namespace BatterBabel.Core.Recovery;

using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;

/// <summary>desired.json 里的一条 hosts 期望映射（domain → ip）。</summary>
public sealed record HostsDesiredEntry(string Domain, string Ip);

/// <summary>
/// 期望状态的内存形态。字段与 desired.json 的 schema 一一对应：
/// <list type="bullet">
///   <item><description><see cref="HostsPending"/> + <see cref="HostsEntries"/> + <see cref="Hosts"/>：登录后要恢复的 hosts 项；</description></item>
///   <item><description><see cref="QosEnabled"/> / <see cref="QosExecutable"/> / <see cref="QosDscp"/>：是否重建游戏 QoS；</description></item>
///   <item><description><see cref="TuningSnapshot"/>：需要回滚的调优快照文件（null 表示不回滚）。</description></item>
/// </list>
/// <para>
/// <see cref="HostsEntries"/> 保留为独立的计数（与 spec 的签名一致）；实际写盘时以
/// <see cref="Hosts"/> 列表为准，缺少列表却有计数会被视为调用方错误，避免静默丢掉具体映射。
/// </para>
/// </summary>
public sealed record RecoveryState(
    bool HostsPending,
    int HostsEntries,
    bool QosEnabled,
    string? QosExecutable,
    int QosDscp,
    string? TuningSnapshot,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<HostsDesiredEntry>? Hosts = null);

/// <summary>计划任务操作的统一结果。<see cref="TaskXml"/> 只在查询成功时非空。</summary>
public sealed record TaskResult(bool Ok, string Message, string TaskName, string? TaskXml);

/// <summary>一次性恢复摘要：只描述“要做什么”，不代表已经做了。</summary>
public sealed record RecoveryPlan(
    bool HostsPending,
    IReadOnlyList<HostsDesiredEntry> Hosts,
    bool RebuildQos,
    string? QosExecutable,
    int QosDscp,
    bool RollbackTuning,
    string? TuningSnapshot,
    DateTimeOffset? UpdatedAt)
{
    public int HostsEntries => Hosts.Count;

    public bool HasWork => (HostsPending && Hosts.Count > 0) || RebuildQos || RollbackTuning;

    /// <summary>给日志/CLI 用的中文摘要；不包含任何执行结果。</summary>
    public string Summary
    {
        get
        {
            if (!HasWork)
            {
                return "无需恢复（desired 中没有待处理项）";
            }

            var parts = new List<string>();
            if (HostsPending && Hosts.Count > 0)
            {
                parts.Add($"hosts {Hosts.Count} 项");
            }

            if (RebuildQos)
            {
                parts.Add($"重建 QoS（{QosExecutable}，DSCP {QosDscp}）");
            }

            if (RollbackTuning)
            {
                parts.Add($"回滚调优快照 {TuningSnapshot}");
            }

            return "待恢复：" + string.Join("；", parts);
        }
    }
}

/// <summary>单个恢复步骤的结果，由调用方（主线程）提供的委托返回。</summary>
public sealed record RecoveryStepResult(bool Ok, string Message);

/// <summary>
/// 实际执行能力由调用方注入。本切片只做“读状态 → 出计划”，不直接改 hosts、QoS 或调优；
/// 这样 S8 不依赖 S5/S7/S9 的任何类型，也不会在自检里误碰系统。
/// </summary>
public sealed record RecoveryHooks(
    Func<IReadOnlyList<HostsDesiredEntry>, RecoveryStepResult>? ApplyHosts = null,
    Func<string, int, RecoveryStepResult>? ApplyQos = null,
    Func<string, RecoveryStepResult>? RestoreTuning = null);

/// <summary>一步实际执行的留痕。</summary>
public sealed record RecoveryStep(string Name, bool Ok, string Message);

/// <summary>整次恢复的结果；<see cref="Ok"/> 要求所有需要执行的步骤都成功。</summary>
public sealed record RecoveryOutcome(
    bool Ok,
    string Message,
    RecoveryPlan Plan,
    IReadOnlyList<RecoveryStep> Steps);

/// <summary>
/// 登录恢复的边界（S8）：
/// <list type="number">
///   <item><description>注册/删除/查询计划任务 <c>BatterBabel-Restore</c>（登录时以最高权限运行 <c>&lt;exe&gt; --boot-restore</c>）；</description></item>
///   <item><description>原子写/读 <c>%LOCALAPPDATA%\BatterBabel\recovery\desired.json</c>，坏数据落 <c>desired.error.log</c>（最多 10 行）；</description></item>
///   <item><description>把 desired 摘要成 <see cref="RecoveryPlan"/>，执行交给调用方委托。</description></item>
/// </list>
/// 本类型不写 hosts、不调 netsh、不读 S7/S9 的模型，也不弹 UAC。
/// </summary>
public static class BootRecovery
{
    public const string TaskName = "BatterBabel-Restore";

    private const int MaxErrorLogLines = 10;
    private static readonly object ErrorLogLock = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>%LOCALAPPDATA%\BatterBabel\recovery\desired.json（不落程序目录）。</summary>
    public static string DefaultDesiredPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatterBabel", "recovery", "desired.json");

    /// <summary>与 desired.json 同目录的诊断日志（最多 10 行）。</summary>
    public static string ErrorLogPath { get; } = Path.Combine(
        Path.GetDirectoryName(DefaultDesiredPath)!,
        "desired.error.log");

    /// <summary>当前进程是否管理员（只查询令牌，不弹 UAC）。</summary>
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 注册登录计划任务。需要管理员权限，否则返回 Ok=false 且不弹 UAC。
    /// </summary>
    /// <param name="appExecutablePath">登录后要运行的 BatterBabel 可执行文件。</param>
    /// <param name="taskName">任务名；自检会传一次性名字。</param>
    /// <param name="arguments">附加参数；生产入口固定为 <c>--boot-restore</c>，自检用 <c>/c exit 0</c> 或 <c>--boot-restore</c> 指向 cmd.exe。</param>
    public static TaskResult EnsureTask(
        string appExecutablePath,
        string taskName = TaskName,
        string arguments = "--boot-restore")
    {
        if (!OperatingSystem.IsWindows())
        {
            return new TaskResult(false, "仅 Windows 支持计划任务恢复", taskName, null);
        }

        if (!IsAdministrator())
        {
            return new TaskResult(false, "需要管理员权限（创建计划任务）", taskName, null);
        }

        if (!IsValidTaskName(taskName))
        {
            return new TaskResult(false, "计划任务名无效：" + taskName, taskName, null);
        }

        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new TaskResult(false, "计划任务参数不能为空", taskName, null);
        }

        string fullExe;
        try
        {
            fullExe = Path.GetFullPath(appExecutablePath);
        }
        catch (Exception ex)
        {
            return new TaskResult(false, "可执行文件路径无效：" + ex.Message, taskName, null);
        }

        if (!File.Exists(fullExe))
        {
            return new TaskResult(false, "找不到可执行文件：" + fullExe, taskName, null);
        }

        // schtasks 把 /TR 当成一条完整命令行：带空格的 exe 必须自己加引号。
        var commandLine = $"\"{fullExe}\" {arguments.Trim()}";
        var create = RunProcess(SchtasksPath, new[]
        {
            "/Create", "/F", "/RL", "HIGHEST", "/SC", "ONLOGON",
            "/TN", taskName, "/TR", commandLine,
        });

        if (create.Failure is not null)
        {
            return new TaskResult(false, "创建计划任务失败：" + create.Failure, taskName, null);
        }

        if (create.ExitCode != 0)
        {
            return new TaskResult(
                false,
                $"创建计划任务失败（schtasks 退出码 {create.ExitCode}）：{Shorten(FirstNonEmpty(create.StandardError, create.StandardOutput))}",
                taskName,
                null);
        }

        var query = QueryTask(taskName);
        if (!query.Exists || string.IsNullOrWhiteSpace(query.Xml))
        {
            // 创建命令成功但读不回：可能是权限/任务计划服务异常。不要留下无法验证的任务。
            var rollback = DeleteTaskRaw(taskName);
            return new TaskResult(
                false,
                "创建命令返回成功，但读不到任务：" + Shorten(query.Error) +
                (rollback.Ok ? "（已回滚删除）" : "（回滚删除也失败：" + rollback.Message + "）"),
                taskName,
                null);
        }

        var check = InspectXml(query.Xml, fullExe, arguments);
        if (!check.Ok)
        {
            // 刚创建的任务不可用就不要留在机器上；回滚失败也如实报告。
            var rollback = DeleteTaskRaw(taskName);
            return new TaskResult(
                false,
                "任务读回校验失败：" + check.Message + (rollback.Ok ? "（已回滚删除）" : "（回滚删除也失败：" + rollback.Message + "）"),
                taskName,
                query.Xml);
        }

        return new TaskResult(
            true,
            $"已注册计划任务 {taskName}：登录时以最高权限运行 \"{fullExe}\" {arguments.Trim()}（RunLevel=HighestAvailable，登录触发）",
            taskName,
            query.Xml);
    }

    /// <summary>删除计划任务；任务不存在也算成功（幂等）。</summary>
    public static TaskResult RemoveTask(string taskName = TaskName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new TaskResult(false, "仅 Windows 支持计划任务恢复", taskName, null);
        }

        if (!IsValidTaskName(taskName))
        {
            return new TaskResult(false, "计划任务名无效：" + taskName, taskName, null);
        }

        var query = QueryTask(taskName);
        if (!query.Exists)
        {
            return new TaskResult(true, $"任务不存在，无需删除：{taskName}", taskName, null);
        }

        if (!IsAdministrator())
        {
            return new TaskResult(false, "需要管理员权限（删除计划任务）", taskName, query.Xml);
        }

        var delete = DeleteTaskRaw(taskName);
        if (!delete.Ok)
        {
            return new TaskResult(false, "删除计划任务失败：" + delete.Message, taskName, query.Xml);
        }

        var after = QueryTask(taskName);
        if (after.Exists)
        {
            return new TaskResult(false, "删除命令返回成功，但任务仍存在：" + taskName, taskName, after.Xml);
        }

        return new TaskResult(true, $"已删除计划任务：{taskName}", taskName, query.Xml);
    }

    /// <summary>只读查询任务；不存在时 Ok=false 且 Message 说明。</summary>
    public static TaskResult InspectTask(string taskName = TaskName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new TaskResult(false, "仅 Windows 支持计划任务恢复", taskName, null);
        }

        if (!IsValidTaskName(taskName))
        {
            return new TaskResult(false, "计划任务名无效：" + taskName, taskName, null);
        }

        var query = QueryTask(taskName);
        if (!query.Exists || string.IsNullOrWhiteSpace(query.Xml))
        {
            return new TaskResult(false, $"任务不存在或无法查询：{taskName}（{Shorten(query.Error)}）", taskName, null);
        }

        var parsed = TryParseTaskXml(query.Xml);
        var summary = parsed is null
            ? "任务存在，但 XML 解析失败"
            : $"任务存在：RunLevel={parsed.Value.RunLevel ?? "?"}；命令={parsed.Value.Command ?? "?"}；参数={parsed.Value.Arguments ?? ""}；登录触发";

        return new TaskResult(true, summary, taskName, query.Xml);
    }

    /// <summary>
    /// 原子写 desired.json：同目录临时文件 + 落盘 + File.Replace/Move 覆盖 + 读回校验。
    /// 失败抛 <see cref="InvalidOperationException"/>（调用方可以把它当“本次状态未持久化”处理）。
    /// <paramref name="appExe"/> 只用于诊断日志与调用方语义，不写入 schema（schema 固定四段）。
    /// </summary>
    public static void WriteDesired(string appExe, RecoveryState state, string? desiredPath = null)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        if (string.IsNullOrWhiteSpace(appExe))
        {
            throw new ArgumentException("appExe 不能为空", nameof(appExe));
        }

        var path = Path.GetFullPath(desiredPath ?? DefaultDesiredPath);
        var normalized = NormalizeForWrite(state);
        var json = SerializeDesired(normalized);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            LogError($"写入 desired 失败（{Path.GetFileName(path)}，来源 {Path.GetFileName(appExe)}）：{ex.GetType().Name}: {ex.Message}", path);
            throw new InvalidOperationException("写入 desired.json 失败：" + ex.Message, ex);
        }

        var readBack = ReadDesired(path);
        if (readBack is null)
        {
            LogError("写入 desired 后读回为 null（可能被并发改写或内容不可解析）", path);
            throw new InvalidOperationException("写入 desired.json 后读回校验失败（ReadDesired 返回 null）");
        }

        var expected = normalized.State;
        var mismatch = Compare(expected, readBack);
        if (mismatch is not null)
        {
            LogError("写入 desired 后读回不一致：" + mismatch, path);
            throw new InvalidOperationException("写入 desired.json 后读回不一致：" + mismatch);
        }
    }

    /// <summary>
    /// 读 desired.json；文件不存在返回 null（首次登录属正常），坏 JSON/缺字段/类型不符返回 null 并写诊断日志。
    /// </summary>
    public static RecoveryState? ReadDesired(string? desiredPath = null)
    {
        var path = desiredPath is null ? DefaultDesiredPath : Path.GetFullPath(desiredPath);
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogError($"读取 desired 失败（{path}）：{ex.GetType().Name}: {ex.Message}", path);
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });

            return ParseDesired(document.RootElement, path);
        }
        catch (JsonException ex)
        {
            LogError($"desired JSON 解析失败（{path}）：{ex.Message}", path);
            return null;
        }
        catch (FormatException ex)
        {
            LogError($"desired JSON 字段格式错误（{path}）：{ex.Message}", path);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            LogError($"desired JSON 字段不符合 schema（{path}）：{ex.Message}", path);
            return null;
        }
        catch (Exception ex)
        {
            LogError($"desired 读取异常（{path}）：{ex.GetType().Name}: {ex.Message}", path);
            return null;
        }
    }

    /// <summary>把内存状态摘要成计划；null 表示没有可读的 desired。</summary>
    public static RecoveryPlan? BuildPlan(RecoveryState? state)
    {
        if (state is null)
        {
            return null;
        }

        var hosts = state.Hosts ?? Array.Empty<HostsDesiredEntry>();
        return new RecoveryPlan(
            HostsPending: state.HostsPending,
            Hosts: hosts,
            RebuildQos: state.QosEnabled && !string.IsNullOrWhiteSpace(state.QosExecutable),
            QosExecutable: state.QosEnabled ? state.QosExecutable : null,
            QosDscp: state.QosEnabled ? state.QosDscp : 0,
            RollbackTuning: !string.IsNullOrWhiteSpace(state.TuningSnapshot),
            TuningSnapshot: string.IsNullOrWhiteSpace(state.TuningSnapshot) ? null : state.TuningSnapshot,
            UpdatedAt: state.UpdatedAt);
    }

    /// <summary>读 desired.json 并生成计划；null 表示文件缺失或不可解析（原因见 desired.error.log）。</summary>
    public static RecoveryPlan? ReadPlan(string? desiredPath = null) => BuildPlan(ReadDesired(desiredPath));

    /// <summary>
    /// 按计划调用调用方提供的委托执行恢复。本方法只编排，不直接改系统。
    /// 某个步骤失败不会阻止后续步骤（尽量恢复能恢复的），最终 Ok 要求全部成功。
    /// </summary>
    public static RecoveryOutcome Execute(RecoveryPlan plan, RecoveryHooks hooks)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (hooks is null)
        {
            throw new ArgumentNullException(nameof(hooks));
        }

        var steps = new List<RecoveryStep>();

        if (!plan.HasWork)
        {
            return new RecoveryOutcome(true, "无需恢复（desired 中没有待处理项）", plan, steps);
        }

        if (plan.HostsPending && plan.Hosts.Count > 0)
        {
            steps.Add(RunStep("hosts", () => hooks.ApplyHosts is null
                ? new RecoveryStepResult(false, "未提供 hosts 恢复委托（本切片不直接改 hosts）")
                : hooks.ApplyHosts(plan.Hosts)));
        }

        if (plan.RebuildQos)
        {
            if (string.IsNullOrWhiteSpace(plan.QosExecutable))
            {
                steps.Add(new RecoveryStep("qos", false, "QoS 计划缺少 executable"));
            }
            else
            {
                steps.Add(RunStep("qos", () => hooks.ApplyQos is null
                    ? new RecoveryStepResult(false, "未提供 QoS 恢复委托（本切片不直接改网络）")
                    : hooks.ApplyQos(plan.QosExecutable, plan.QosDscp)));
            }
        }

        if (plan.RollbackTuning && !string.IsNullOrWhiteSpace(plan.TuningSnapshot))
        {
            steps.Add(RunStep("tuning", () => hooks.RestoreTuning is null
                ? new RecoveryStepResult(false, "未提供调优回滚委托（本切片不直接改系统）")
                : hooks.RestoreTuning(plan.TuningSnapshot)));
        }

        var ok = steps.All(step => step.Ok);
        var message = string.Join("；", steps.Select(step => $"{step.Name}:{(step.Ok ? "成功" : "失败")} {step.Message}"));
        return new RecoveryOutcome(ok, message, plan, steps);
    }

    // ---------- desired.json ----------

    private static RecoveryState ParseDesired(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("根必须是 JSON 对象");
        }

        if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaNumber))
        {
            throw new InvalidOperationException("缺少整数 schema");
        }

        if (schemaNumber != 1)
        {
            throw new InvalidOperationException("不支持的 schema 版本：" + schemaNumber);
        }

        DateTimeOffset? updatedAt = null;
        if (root.TryGetProperty("updatedAt", out var updatedElement) && updatedElement.ValueKind != JsonValueKind.Null)
        {
            if (updatedElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("updatedAt 必须是 ISO 字符串");
            }

            if (!DateTimeOffset.TryParse(updatedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedUpdated))
            {
                throw new FormatException("updatedAt 不是可解析的 ISO 时间：" + updatedElement.GetString());
            }

            updatedAt = parsedUpdated;
        }

        var hostsPending = false;
        var hostsEntries = 0;
        IReadOnlyList<HostsDesiredEntry>? hosts = null;
        if (root.TryGetProperty("hosts", out var hostsElement) && hostsElement.ValueKind != JsonValueKind.Null)
        {
            if (hostsElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("hosts 必须是对象或 null");
            }

            if (!hostsElement.TryGetProperty("pending", out var pending) || pending.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidOperationException("hosts.pending 必须是布尔值");
            }

            if (!hostsElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("hosts.entries 必须是数组");
            }

            var list = new List<HostsDesiredEntry>();
            var index = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"hosts.entries[{index}] 必须是对象");
                }

                var domain = RequiredString(entry, "domain", $"hosts.entries[{index}]");
                var ip = RequiredString(entry, "ip", $"hosts.entries[{index}]");
                list.Add(new HostsDesiredEntry(domain, ip));
                index++;
            }

            hostsPending = pending.GetBoolean();
            hostsEntries = list.Count;
            hosts = list;
        }

        var qosEnabled = false;
        string? qosExecutable = null;
        var qosDscp = 0;
        if (root.TryGetProperty("qos", out var qosElement) && qosElement.ValueKind != JsonValueKind.Null)
        {
            if (qosElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("qos 必须是对象或 null");
            }

            if (!qosElement.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidOperationException("qos.enabled 必须是布尔值");
            }

            qosEnabled = enabled.GetBoolean();
            if (!qosElement.TryGetProperty("dscp", out var dscp) || dscp.ValueKind != JsonValueKind.Number || !dscp.TryGetInt32(out qosDscp))
            {
                throw new InvalidOperationException("qos.dscp 必须是整数");
            }

            if (qosDscp is < 0 or > 63)
            {
                throw new InvalidOperationException("qos.dscp 超出 0..63：" + qosDscp);
            }

            if (qosElement.TryGetProperty("executable", out var executable) && executable.ValueKind != JsonValueKind.Null)
            {
                if (executable.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException("qos.executable 必须是字符串或 null");
                }

                qosExecutable = executable.GetString();
            }

            if (qosEnabled && string.IsNullOrWhiteSpace(qosExecutable))
            {
                throw new InvalidOperationException("qos.enabled=true 时 qos.executable 不能为空");
            }
        }

        string? tuningSnapshot = null;
        if (root.TryGetProperty("tuning", out var tuningElement) && tuningElement.ValueKind != JsonValueKind.Null)
        {
            if (tuningElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("tuning 必须是对象或 null");
            }

            tuningSnapshot = RequiredString(tuningElement, "restoreSnapshot", "tuning");
        }

        return new RecoveryState(
            hostsPending,
            hostsEntries,
            qosEnabled,
            qosExecutable,
            qosDscp,
            tuningSnapshot,
            updatedAt,
            hosts);
    }

    private static string RequiredString(JsonElement parent, string property, string location)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"{location}.{property} 必须是非空字符串");
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{location}.{property} 不能为空");
        }

        return value;
    }

    private static NormalizedDesired NormalizeForWrite(RecoveryState state)
    {
        var hosts = state.Hosts ?? Array.Empty<HostsDesiredEntry>();
        if (state.HostsPending && state.Hosts is null && state.HostsEntries > 0)
        {
            throw new InvalidOperationException(
                $"RecoveryState 声明了 {state.HostsEntries} 项 hosts，但没有提供 entries；desired.json 需要具体映射，拒绝静默丢数据");
        }

        foreach (var entry in hosts)
        {
            if (string.IsNullOrWhiteSpace(entry.Domain) || string.IsNullOrWhiteSpace(entry.Ip))
            {
                throw new InvalidOperationException("hosts entries 存在空 domain/ip");
            }
        }

        if (state.HostsPending && state.HostsEntries > 0 && hosts.Count > 0 && state.HostsEntries != hosts.Count)
        {
            LogError($"RecoveryState.HostsEntries={state.HostsEntries} 与 entries 数量 {hosts.Count} 不一致，写入时以 entries 为准");
        }

        var updatedAt = state.UpdatedAt ?? DateTimeOffset.UtcNow;
        var qosEnabled = state.QosEnabled;
        string? qosExe = qosEnabled ? state.QosExecutable : null;
        if (qosEnabled && string.IsNullOrWhiteSpace(qosExe))
        {
            throw new InvalidOperationException("QosEnabled=true 时必须提供 QosExecutable");
        }

        if (qosEnabled && state.QosDscp is < 0 or > 63)
        {
            throw new InvalidOperationException("QosDscp 超出 0..63：" + state.QosDscp);
        }

        var snapshot = string.IsNullOrWhiteSpace(state.TuningSnapshot) ? null : state.TuningSnapshot;
        var normalized = new RecoveryState(
            HostsPending: state.HostsPending,
            HostsEntries: hosts.Count,
            QosEnabled: qosEnabled,
            QosExecutable: qosExe,
            QosDscp: qosEnabled ? state.QosDscp : 0,
            TuningSnapshot: snapshot,
            UpdatedAt: updatedAt,
            Hosts: hosts.ToArray());

        return new NormalizedDesired(normalized, updatedAt);
    }

    private static string SerializeDesired(NormalizedDesired normalized)
    {
        var state = normalized.State;
        var payload = new DesiredDocument
        {
            Schema = 1,
            UpdatedAt = normalized.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            Hosts = state.HostsPending
                ? new DesiredHosts
                {
                    Pending = true,
                    Entries = (state.Hosts ?? Array.Empty<HostsDesiredEntry>())
                        .Select(entry => new DesiredHostsEntry { Domain = entry.Domain, Ip = entry.Ip })
                        .ToList(),
                }
                : null,
            Qos = state.QosEnabled
                ? new DesiredQos { Enabled = true, Executable = state.QosExecutable, Dscp = state.QosDscp }
                : null,
            Tuning = state.TuningSnapshot is null
                ? null
                : new DesiredTuning { RestoreSnapshot = state.TuningSnapshot },
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // desired.json 是本地诊断文件，不是 HTML：不转义中文/加号，便于人工查看。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static string? Compare(RecoveryState expected, RecoveryState actual)
    {
        if (expected.HostsPending != actual.HostsPending)
        {
            return $"HostsPending 期望 {expected.HostsPending} 实际 {actual.HostsPending}";
        }

        var expectedEntries = expected.Hosts ?? Array.Empty<HostsDesiredEntry>();
        var actualEntries = actual.Hosts ?? Array.Empty<HostsDesiredEntry>();
        if (expectedEntries.Count != actualEntries.Count)
        {
            return $"hosts 条目数期望 {expectedEntries.Count} 实际 {actualEntries.Count}";
        }

        for (var i = 0; i < expectedEntries.Count; i++)
        {
            if (!string.Equals(expectedEntries[i].Domain, actualEntries[i].Domain, StringComparison.Ordinal) ||
                !string.Equals(expectedEntries[i].Ip, actualEntries[i].Ip, StringComparison.Ordinal))
            {
                return $"hosts 第 {i + 1} 条不一致";
            }
        }

        if (expected.QosEnabled != actual.QosEnabled)
        {
            return $"QosEnabled 期望 {expected.QosEnabled} 实际 {actual.QosEnabled}";
        }

        if (!string.Equals(expected.QosExecutable, actual.QosExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return $"QosExecutable 期望 {expected.QosExecutable} 实际 {actual.QosExecutable}";
        }

        if (expected.QosDscp != actual.QosDscp)
        {
            return $"QosDscp 期望 {expected.QosDscp} 实际 {actual.QosDscp}";
        }

        if (!string.Equals(expected.TuningSnapshot, actual.TuningSnapshot, StringComparison.OrdinalIgnoreCase))
        {
            return "TuningSnapshot 不一致";
        }

        if (expected.UpdatedAt is { } expectedUpdated && actual.UpdatedAt is { } actualUpdated &&
            expectedUpdated.ToUnixTimeMilliseconds() != actualUpdated.ToUnixTimeMilliseconds())
        {
            return "UpdatedAt 不一致";
        }

        return null;
    }

    // ---------- 计划任务 ----------

    private static string SchtasksPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "schtasks.exe");

    private static bool IsValidTaskName(string taskName) =>
        !string.IsNullOrWhiteSpace(taskName) &&
        taskName.Length <= 200 &&
        taskName.IndexOfAny(new[] { '\r', '\n', '"' }) < 0;

    private static TaskQuery QueryTask(string taskName)
    {
        var result = RunProcess(SchtasksPath, new[] { "/Query", "/TN", taskName, "/XML" });
        if (result.Failure is not null)
        {
            return new TaskQuery(false, null, result.Failure);
        }

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new TaskQuery(true, result.StandardOutput, "");
        }

        return new TaskQuery(false, null, Shorten(FirstNonEmpty(result.StandardError, result.StandardOutput)));
    }

    private static TaskResult DeleteTaskRaw(string taskName)
    {
        var result = RunProcess(SchtasksPath, new[] { "/Delete", "/F", "/TN", taskName });
        if (result.Failure is not null)
        {
            return new TaskResult(false, result.Failure, taskName, null);
        }

        if (result.ExitCode == 0)
        {
            return new TaskResult(true, "已删除", taskName, null);
        }

        return new TaskResult(
            false,
            $"schtasks 退出码 {result.ExitCode}：{Shorten(FirstNonEmpty(result.StandardError, result.StandardOutput))}",
            taskName,
            null);
    }

    private static (bool Ok, string Message) InspectXml(string xml, string expectedExe, string expectedArguments)
    {
        var parsed = TryParseTaskXml(xml);
        if (parsed is null)
        {
            return (false, "任务 XML 无法解析");
        }

        var value = parsed.Value;
        if (!string.Equals(value.RunLevel, "HighestAvailable", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"RunLevel 不是 HighestAvailable（实际 {value.RunLevel ?? "空"}）");
        }

        if (!CommandMatches(value.Command, expectedExe))
        {
            return (false, $"任务命令不是目标 exe（实际 {value.Command ?? "空"}）");
        }

        if (string.IsNullOrWhiteSpace(value.Arguments) ||
            value.Arguments.IndexOf(expectedArguments.Trim(), StringComparison.Ordinal) < 0)
        {
            return (false, $"任务参数缺少“{expectedArguments.Trim()}”（实际 {value.Arguments ?? "空"}）");
        }

        return (true, "RunLevel/命令/参数均匹配");
    }

    private static (string? RunLevel, string? Command, string? Arguments)? TryParseTaskXml(string xml)
    {
        try
        {
            // schtasks 重定向输出是 UTF-8 字节，但仍声明 encoding="UTF-16"；字符串解析前先改声明。
            var cleaned = xml
                .Replace("encoding=\"UTF-16\"", "encoding=\"utf-8\"", StringComparison.OrdinalIgnoreCase)
                .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.OrdinalIgnoreCase);
            var document = XDocument.Parse(cleaned);
            string? Find(string localName) => document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == localName)?.Value;

            return (Find("RunLevel"), Find("Command"), Find("Arguments"));
        }
        catch
        {
            return null;
        }
    }

    private static bool CommandMatches(string? command, string expectedExe)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var trimmed = command.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        try
        {
            return string.Equals(Path.GetFullPath(trimmed), expectedExe, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return trimmed.Contains(expectedExe, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------- 子进程 / 编码 ----------

    private static ProcessRun RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProcessRun(-1, "", "", "进程未能启动：" + fileName);
            }

            var stdoutTask = Task.Run(() => ReadAllBytes(process.StandardOutput.BaseStream));
            var stderrTask = Task.Run(() => ReadAllBytes(process.StandardError.BaseStream));

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new ProcessRun(-1, "", "", "进程超时（30 s）：" + Path.GetFileName(fileName));
            }

            Task.WaitAll(stdoutTask, stderrTask);
            return new ProcessRun(
                process.ExitCode,
                Decode(stdoutTask.Result),
                Decode(stderrTask.Result),
                null);
        }
        catch (Exception ex)
        {
            return new ProcessRun(-1, "", "", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return "";
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        try
        {
            // 严格 UTF-8：schtasks 在中文系统上按控制台 OEM 代码页（如 936）输出错误文本，
            // 直接按 UTF-8 解码会得到 U+FFFD，消息不可诊断。
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var oemCodePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                if (oemCodePage is > 0 and not 65001 and not 1200 and not 1201)
                {
                    return Encoding.GetEncoding(oemCodePage).GetString(bytes);
                }
            }
            catch
            {
                // 代码页不可用时退回 UTF-8 替换字符，至少保留可读的 ASCII 部分。
            }

            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string Shorten(string value)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "…";
    }

    private static RecoveryStep RunStep(string name, Func<RecoveryStepResult> action)
    {
        try
        {
            var result = action();
            return new RecoveryStep(name, result.Ok, result.Message);
        }
        catch (Exception ex)
        {
            return new RecoveryStep(name, false, "执行异常：" + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主结果。
        }
    }

    // ---------- 诊断日志 ----------

    private static void LogError(string message, string? desiredPath = null)
    {
        try
        {
            var logPath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(desiredPath ?? DefaultDesiredPath))!,
                "desired.error.log");
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (ErrorLogLock)
            {
                var lines = File.Exists(logPath)
                    ? File.ReadAllLines(logPath, Encoding.UTF8).ToList()
                    : new List<string>();
                lines.Add(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + " " + Shorten(message));
                if (lines.Count > MaxErrorLogLines)
                {
                    lines = lines.Skip(lines.Count - MaxErrorLogLines).ToList();
                }

                File.WriteAllLines(logPath, lines, Utf8NoBom);
            }
        }
        catch
        {
            // 日志写失败不能影响恢复流程。
        }
    }

    // ---------- 内部类型 ----------

    private sealed record TaskQuery(bool Exists, string? Xml, string Error);

    private sealed record ProcessRun(int ExitCode, string StandardOutput, string StandardError, string? Failure);

    private sealed record NormalizedDesired(RecoveryState State, DateTimeOffset UpdatedAt);

    private sealed class DesiredDocument
    {
        public int Schema { get; set; }

        public string UpdatedAt { get; set; } = "";

        public DesiredHosts? Hosts { get; set; }

        public DesiredQos? Qos { get; set; }

        public DesiredTuning? Tuning { get; set; }
    }

    private sealed class DesiredHosts
    {
        public bool Pending { get; set; }

        public List<DesiredHostsEntry> Entries { get; set; } = new();
    }

    private sealed class DesiredHostsEntry
    {
        public string Domain { get; set; } = "";

        public string Ip { get; set; } = "";
    }

    private sealed class DesiredQos
    {
        public bool Enabled { get; set; }

        public string? Executable { get; set; }

        public int Dscp { get; set; }
    }

    private sealed class DesiredTuning
    {
        public string RestoreSnapshot { get; set; } = "";
    }
}
