using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using BatterBabel.App.Discovery;
using BatterBabel.Core.Domain;
using BatterBabel.Core.Hosts;
using BatterBabel.Core.Qos;
using BatterBabel.Core.Recovery;
using BatterBabel.Core.Tuning;
using Microsoft.Web.WebView2.Core;

namespace BatterBabel.App.Web;

/// <summary>job 的状态取值（页面只认 RUNNING/SUCCEEDED/FAILED；IDLE 表示「还没有任何任务」）。</summary>
internal static class JobStatus
{
    public const string Running = "RUNNING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Idle = "IDLE";
}

/// <summary>一次任务的进度状态（Web 壳里只有内存态，不落盘）。</summary>
internal sealed class WebJob
{
    public required string JobId { get; init; }
    public required string Action { get; init; }
    public string GameId { get; init; } = "";
    public int Sequence { get; set; }
    public int Percent { get; set; }
    public string Status { get; set; } = JobStatus.Running;
    public string Message { get; set; } = "正在测速";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }

    public bool IsTerminal => Status is JobStatus.Succeeded or JobStatus.Failed;
}

/// <summary>get_progress 的对外投影：页面只看这五个字段。</summary>
internal readonly record struct JobSnapshot(string JobId, string Status, int Sequence, int Percent, string Message)
{
    /// <summary>没有任何 job 时的答复（页面据此显示「就绪」）。</summary>
    internal static JobSnapshot Idle { get; } = new("", JobStatus.Idle, 0, 0, "就绪");
}

/// <summary>
/// job 协议的状态机：纯内存、不碰 WebView2、不做 JSON，因此 <see cref="BbRpcHost.ProtocolChecks"/>
/// 能直接驱动它做断言。
///
/// 四条不变量（页面据此判断「还在跑 / 结束没有 / 数字有没有倒退」）：
///   1. 每个 job 只允许一次终态跃迁；重复置终态是空操作，sequence 既不回退也不重复加；
///   2. 终态之后不再接受进度（percent / message / sequence 一起冻结）；
///   3. sequence 只增不减，且只在「进度前进」与「终态跃迁」时 +1；
///   4. 同一时刻最多一个正在测的动作（<see cref="InFlight"/>）：start_job 与 run_action 都要过这道闸。
/// </summary>
internal sealed class JobBoard
{
    /// <summary>并发闸的业务话术（页面把它直接显示给用户）。</summary>
    internal const string BusyMessage = "已有测速在进行";

    private readonly Dictionary<string, WebJob> _jobs = new(StringComparer.Ordinal);
    private int _counter;
    private string _currentJobId = "";
    private string _inFlightJobId = "";

    /// <summary>建过多少个 job（断言「拒绝时不建 job」用）。</summary>
    internal int CreatedCount => _counter;

    /// <summary>当前在册 job 数。</summary>
    internal int JobCount => _jobs.Count;

    /// <summary>最近一个 job（get_progress 无参时的目标）。</summary>
    internal WebJob? Current => Find(_currentJobId);

    /// <summary>正在测速的 job；null 表示没有动作在跑。</summary>
    internal WebJob? InFlight => Find(_inFlightJobId);

    internal WebJob? Find(string? jobId) =>
        string.IsNullOrEmpty(jobId) ? null : _jobs.TryGetValue(jobId, out var job) ? job : null;

    /// <summary>
    /// start_job：建一个「待执行」的 job。被并发闸挡住时 <paramref name="refusal"/> 非空且不建 job。
    /// </summary>
    internal WebJob? Start(string action, string gameId, out string refusal)
    {
        refusal = "";
        if (InFlight is not null)
        {
            refusal = BusyMessage;
            return null;
        }

        var current = Current;
        if (current is { IsTerminal: false })
        {
            // 同一动作再来一次 = 页面重复点击/并发请求：拒绝，不让两个 job 抢同一次测速。
            if (string.Equals(current.Action, action, StringComparison.Ordinal))
            {
                refusal = BusyMessage;
                return null;
            }

            // 换动作 = 页面切了新任务：旧 job 不会再有动作，收成终态，
            // 免得一条悬着的 RUNNING 把会话卡住（也让「终态冻结」对每个 job 都成立）。
            Finish(current, JobStatus.Failed, current.Percent, "已被新任务取代（未执行）");
        }

        return Create(action, gameId);
    }

    /// <summary>
    /// run_action 真正开跑：认领 job（终态不复用；缺 jobId 时复用同动作的待执行 job），
    /// 并把并发闸拉上。已被别的动作占着时 <paramref name="refusal"/> 非空。
    /// </summary>
    internal WebJob? BeginAction(string action, string gameId, string? jobId, out string refusal)
    {
        refusal = "";
        if (InFlight is not null)
        {
            refusal = BusyMessage;
            return null;
        }

        var job = Find(jobId);
        if (job is not null && job.IsTerminal)
        {
            // 终态的 job 不能复活（sequence 会倒退）；另建一个，页面拿新 jobId 看新进度。
            job = null;
        }

        if (job is null)
        {
            var current = Current;
            if (current is { IsTerminal: false } && string.Equals(current.Action, action, StringComparison.Ordinal))
            {
                job = current;
            }
            else
            {
                if (current is { IsTerminal: false })
                {
                    Finish(current, JobStatus.Failed, current.Percent, "已被新任务取代（未执行）");
                }

                job = Create(action, gameId);
            }
        }

        job.Status = JobStatus.Running;
        job.Message = "正在测速";
        job.Sequence++;
        _currentJobId = job.JobId;
        _inFlightJobId = job.JobId;
        return job;
    }

    /// <summary>进度回调（live 的唯一进度来源）：终态之后一律忽略，绝不倒退。</summary>
    internal bool Report(WebJob job, int percent, string message)
    {
        if (job.IsTerminal) { return false; }
        job.Percent = Math.Clamp(percent, 0, 100);
        if (!string.IsNullOrEmpty(message)) { job.Message = message; }
        job.Sequence++;
        return true;
    }

    /// <summary>终态跃迁：只发生一次；第二次起是空操作（返回 false），sequence 不再变。</summary>
    internal bool Finish(WebJob job, string status, int percent, string message)
    {
        if (job.IsTerminal) { return false; }
        job.Status = status is JobStatus.Succeeded or JobStatus.Failed ? status : JobStatus.Failed;
        job.Percent = Math.Clamp(percent, 0, 100);
        if (!string.IsNullOrEmpty(message)) { job.Message = message; }
        job.Sequence++;
        job.FinishedAt = DateTimeOffset.Now;
        if (string.Equals(_inFlightJobId, job.JobId, StringComparison.Ordinal)) { _inFlightJobId = ""; }
        return true;
    }

    internal static JobSnapshot SnapshotOf(WebJob job) =>
        new(job.JobId, job.Status, job.Sequence, job.Percent, job.Message);

    /// <summary>get_progress：点名了 jobId 就取它，否则取最近一个；都没有就是 IDLE。</summary>
    internal JobSnapshot Progress(string? jobId)
    {
        var job = Find(jobId) ?? Current;
        return job is null ? JobSnapshot.Idle : SnapshotOf(job);
    }

    private WebJob Create(string action, string gameId)
    {
        var job = new WebJob { JobId = "web-job-" + (++_counter), Action = action, GameId = gameId };
        _jobs[job.JobId] = job;
        _currentJobId = job.JobId;
        return job;
    }
}

/// <summary>
/// WebView2 侧的 RPC 适配层：把页面的 <c>{callId, command, args}</c> 翻译成对 Core 的调用。
///
/// 四条纪律（与 P0 宿主同构，进入正式工程后仍然成立）：
///   1. 只实现产品需要的三个命令 start_job / get_progress / run_action；不扩通用 RPC，不暴露任意执行；
///   2. run_action 只放行只读的 scan / scan-force / cf-optimize；其余（写 hosts、调优、加速…）一律
///      传输层成功、业务层拒绝 —— 页面必须能区分「没做成」和「没发出」；
///   3. 消息来源必须是本地映射的 bb.local 页面，其它来源直接丢弃并记日志；
///   4. 入站消息有硬上限（信封 64 KiB、args 48 KiB、深度 16），超限在业务分派之前拒绝；
///      能识别 callId 的坏请求回一次结构化运输失败，识别不了的只丢弃并做有界诊断，
///      绝不把解析异常全文反射回页面。
///
/// job 协议（S2）的语义全部落在 <see cref="JobBoard"/> 里，本类只负责校验、日志与 JSON 投影。
/// </summary>
internal sealed class BbRpcHost
{
    internal const string HostOrigin = "https://bb.local/";
    internal const string PagePath = "/index.html";

    internal const int MaxEnvelopeBytes = 64 * 1024;
    internal const int MaxArgsBytes = 48 * 1024;
    internal const int MaxCallIdLength = 128;
    internal const int MaxCommandLength = 64;

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 16 };

    private readonly CoreWebView2 _core;
    private readonly Action<string>? _log;
    private readonly bool _fixture;
    private readonly JobBoard _board = new();
    private List<HostsChange> _lastPlan = new();
    private DateTime _lastPlanAt = DateTime.MinValue;

    /// <param name="fixture">
    /// true 时 scan/cf-optimize 走 DemoRoutes（--web-check / --web-shot 的门禁用固定样本）；
    /// false 时走 Windows 真实发现（正常启动）。两种模式的 JSON 形状一致。
    /// </param>
    internal BbRpcHost(CoreWebView2 core, Action<string>? log = null, bool fixture = true)
    {
        _core = core;
        _log = log;
        _fixture = fixture;
        _core.WebMessageReceived += OnWebMessageReceived;
    }

    /// <summary>宿主收到并处理过的合法消息条数（测试断言用）。</summary>
    internal int ReceivedCount { get; private set; }

    /// <summary>真正进入业务分派的次数（超限/畸形请求不计入）。</summary>
    internal int BusinessCalls { get; private set; }

    /// <summary>因超限、畸形、来源或信封不合法而丢弃的消息数。</summary>
    internal int DroppedEnvelopes { get; private set; }

    /// <summary>回了结构化运输失败（ok=false）的次数。</summary>
    internal int StructuredFailures { get; private set; }

    /// <summary>宿主调用过多少次 __bbReady（旧桥缺陷的复现要素：只通知一次）。</summary>
    internal int ReadyCalls { get; private set; }

    /// <summary>建过的 job 数（自检/证据用）。</summary>
    internal int JobsCreated => _board.CreatedCount;

    internal string LastCommand { get; private set; } = "";

    /// <summary>宿主 → 页面的事件推送（页面的 event.listen('bb-progress') 收到）。</summary>
    internal async Task EmitAsync(string eventName, object payload)
    {
        var name = JsonSerializer.Serialize(eventName);
        var body = JsonSerializer.Serialize(JsonSerializer.Serialize(payload));
        await _core.ExecuteScriptAsync($"window.__bbEmit && window.__bbEmit({name}, {body})");
    }

    /// <summary>通知页面「宿主已就绪」：排队中的请求此时按序发出。</summary>
    internal async Task SignalReadyAsync()
    {
        ReadyCalls++;
        await _core.ExecuteScriptAsync("window.__bbReady && window.__bbReady()");
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var source = e.Source ?? "";
            if (!IsTrustedSource(source))
            {
                Drop($"REJECT_SOURCE {source}");
                return;
            }

            var raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(raw))
            {
                Drop("DROP 空消息");
                return;
            }

            if (Encoding.UTF8.GetByteCount(raw) > MaxEnvelopeBytes)
            {
                // 信封本身超限：不解析、不建任务、不回包（callId 不可信），只留一条有界诊断。
                Drop($"DROP 信封超过 {MaxEnvelopeBytes} 字节");
                return;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(raw, ParseOptions);
            }
            catch (JsonException)
            {
                Drop("DROP 信封不是合法 JSON");
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    Drop("DROP 信封根不是对象");
                    return;
                }

                var envelope = ReadEnvelope(root);
                if (!envelope.Success)
                {
                    Reject(envelope.CallId, "INVALID_ENVELOPE", envelope.Reason);
                    return;
                }

                var command = envelope.Command!;
                var callId = envelope.CallId!;
                LastCommand = command;
                ReceivedCount++;
                Log($"recv #{ReceivedCount} command={command} callId={callId}");

                JsonElement args;
                if (!TryReadArgs(envelope.ArgsJson, out args, out var argsReason))
                {
                    Reject(callId, "INVALID_ARGS", argsReason);
                    return;
                }

                BusinessCalls++;
                switch (command)
                {
                    case "start_job":
                    {
                        // 页面启动时用 __probe__ 做协议能力探测；真实的 13 个动作之外一律拒绝，
                        // 否则「探测成功」会变成假的能力证明（G12：工作区页面 + 严格后端会自锁）。
                        var requested = Str(args, "action") ?? "";
                        if (!TryAcceptStartAction(requested, out var unknownMessage))
                        {
                            // 业务拒绝（页面 resolve 出 ok:false），不是运输失败：闭集外的东西不建 job。
                            Log($"REFUSED start_job action={Bounded(requested)}（未知动作，未建 job）");
                            Post(callId, true, new { ok = false, status = JobStatus.Failed, message = unknownMessage });
                            return;
                        }

                        var job = _board.Start(requested, Str(args, "gameId") ?? "", out var refusal);
                        if (job is null)
                        {
                            Log($"REFUSED start_job action={requested}（{refusal}，未建 job）");
                            Post(callId, true, new { ok = false, status = JobStatus.Failed, message = refusal });
                            return;
                        }

                        Log($"start_job -> {job.JobId} action={job.Action} status={job.Status} sequence={job.Sequence}");
                        Post(callId, true, new
                        {
                            jobId = job.JobId,
                            protocolVersion = 1,
                            status = job.Status,
                            sequence = job.Sequence,
                            percent = job.Percent,
                            action = job.Action,
                        });
                        return;
                    }

                    case "get_progress":
                    {
                        var job = _board.Find(Str(args, "jobId")) ?? _board.Current;
                        if (job is not null && _fixture)
                        {
                            // fixture 假进度：只服务 --web-check / --web-shot 门禁（DemoRoutes 固定样本）。
                            // live 模式 percent 只能来自 LiveRoutes 的 report(percent,message) 回调，这里不掺和。
                            _board.Report(job, Math.Min(90, 15 + (job.Sequence + 1) * 5), "正在测速（本地 fixture）");
                        }

                        var snapshot = job is null ? JobSnapshot.Idle : JobBoard.SnapshotOf(job);
                        Post(callId, true, new
                        {
                            ok = true,
                            jobId = snapshot.JobId,
                            sequence = snapshot.Sequence,
                            percent = snapshot.Percent,
                            message = snapshot.Message,
                            status = snapshot.Status,
                        });
                        return;
                    }

                    case "scan":
                    case "scan-force":
                        Post(callId, true, DemoRoutes.ScanResult());
                        return;

                    case "run_action":
                        await RunActionAsync(callId, args);
                        return;

                    default:
                        Log($"REFUSED 未知命令 {command}");
                        Post(callId, false, new { error = "UNKNOWN_COMMAND", message = "未知命令" });
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            // 只记类型，避免把异常全文（可能含原始报文片段）反射到页面或日志。
            Log($"HANDLER_FAILED {ex.GetType().Name}");
        }
    }

    /// <summary>来源必须是映射根下的 index.html，且没有 userinfo/端口变异。</summary>
    private static bool IsTrustedSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) { return false; }
        return uri.Scheme == Uri.UriSchemeHttps
            && uri.Host == "bb.local"
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.Equals(uri.AbsolutePath, PagePath, StringComparison.Ordinal);
    }

    private readonly record struct EnvelopeResult(bool Success, string? CallId, string? Command, string? ArgsJson, string Reason);

    /// <summary>严格读取信封：只允许 callId/command/args 三个字段，类型与长度都要对。</summary>
    private static EnvelopeResult ReadEnvelope(JsonElement root)
    {
        string? callId = null;
        string? command = null;
        string? argsJson = null;
        var callIdCount = 0;
        var commandCount = 0;
        var argsCount = 0;

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "callId":
                    callIdCount++;
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return new EnvelopeResult(false, null, null, null, "callId 必须是字符串");
                    }
                    callId = property.Value.GetString();
                    break;
                case "command":
                    commandCount++;
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return new EnvelopeResult(false, callId, null, null, "command 必须是字符串");
                    }
                    command = property.Value.GetString();
                    break;
                case "args":
                    argsCount++;
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return new EnvelopeResult(false, callId, command, null, "args 必须是 JSON 字符串");
                    }
                    argsJson = property.Value.GetString();
                    break;
                default:
                    // 未知字段直接判坏：闭集之外的东西一律不解释，避免以后悄悄扩展协议。
                    return new EnvelopeResult(false, callId, command, null, "信封包含未知字段");
            }
        }

        if (callIdCount > 1 || commandCount > 1 || argsCount > 1)
        {
            return new EnvelopeResult(false, callId, command, null, "信封字段重复");
        }

        if (string.IsNullOrEmpty(callId) || !IsSafeToken(callId, MaxCallIdLength))
        {
            return new EnvelopeResult(false, null, command, null, "callId 缺失或不是安全标识");
        }

        if (string.IsNullOrEmpty(command) || command.Length > MaxCommandLength)
        {
            return new EnvelopeResult(false, callId, null, null, "command 缺失或过长");
        }

        return new EnvelopeResult(true, callId, command, argsJson ?? "{}", "");
    }

    private static bool IsSafeToken(string value, int maxLength)
    {
        if (value.Length == 0 || value.Length > maxLength) { return false; }
        foreach (var ch in value)
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
            if (!ok) { return false; }
        }
        return true;
    }

    /// <summary>解析 args：限长、限深、必须是对象；失败返回可回给页面的结构化原因。</summary>
    private static bool TryReadArgs(string? argsJson, out JsonElement args, out string reason)
    {
        args = default;
        reason = "";
        var text = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson!;
        if (Encoding.UTF8.GetByteCount(text) > MaxArgsBytes)
        {
            reason = $"args 超过 {MaxArgsBytes} 字节";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, ParseOptions);
        }
        catch (JsonException)
        {
            reason = "args 不是合法 JSON";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                reason = "args 必须是 JSON 对象";
                return false;
            }

            // 分派是异步的，args 不能借用一个已经释放的 JsonDocument。
            args = document.RootElement.Clone();
            return true;
        }
    }

    private async Task RunActionAsync(string callId, JsonElement args)
    {
        var action = Str(args, "action") ?? "";
        var jobIdArg = Str(args, "jobId");

        // 只有页面点名了 jobId 的动作才把结果写回 job（页面当前只给 cf-optimize 带 jobId）；
        // 没点名就不凭空造 job —— 否则一次 cf-apply 会污染最近一次测速的状态。
        var linkedJob = _board.Find(jobIdArg);

        switch (action)
        {
            case "scan":
            case "scan-force":
            {
                // 发布版 scan 走 24h 缓存（scan-force 强制重扫）；缓存命中时 accelerated 仍按真实策略重算。
                var payload = _fixture
                    ? DemoRoutes.ScanResult()
                    : await GameScanCache.ScanAsync(force: action == "scan-force", CancellationToken.None);
                SettleLinked(linkedJob, true, "扫描完成");
                Post(callId, true, payload);
                return;
            }

            case "status":
            {
                // 发布版 status：真实管理员状态 + 真实 Get-NetQosPolicy（不是猜的）。
                var policies = await QosPolicy.ActivePoliciesAsync();
                var elevated = QosPolicy.CurrentProcessElevated();
                // catch-all 识别：BatterBabel-Route-* 存在但读回没有目标 IP 条件 = 会把 DSCP 46 打到全部流量。
                var routeInspect = await BatterBabel.Core.Qos.RouteQosPolicy.InspectAsync();
                var suspicious = routeInspect.Items.Any(item => !item.HasDstPrefix);
                Log($"status elevated={elevated} activePolicies={policies.Names.Count} qosQueryOk={policies.Ok} suspiciousCatchAll={suspicious}");
                SettleLinked(linkedJob, true, "状态已读取");
                Post(callId, true, elevated
                    ? new
                    {
                        ok = true,
                        status = JobStatus.Succeeded,
                        elevated = true,
                        activePolicies = policies.Names,
                        policyMode = "DSCP 46 / Windows QoS",
                        qosQueryOk = policies.Ok,
                        suspiciousCatchAll = suspicious,
                    }
                    : new
                    {
                        ok = true,
                        status = JobStatus.Succeeded,
                        elevated = false,
                        activePolicies = policies.Names,
                        policyMode = "DSCP 46 / Windows QoS",
                        qosQueryOk = policies.Ok,
                        suspiciousCatchAll = suspicious,
                        message = QosPolicy.ElevationMessage,
                    });
                return;
            }

            case "probe":
            {
                // 8 条线路真实解析 + TCP 443 三取中位；ICMP 与下载速度本版没有实现，返回 null。
                using var probeCancel = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var probe = await RouteCatalog.ProbeAsync(probeCancel.Token);
                Log("probe routes=8 live=1");
                SettleLinked(linkedJob, true, "线路探测完成");
                Post(callId, true, probe);
                return;
            }

            case "optimize":
            {
                // 发布版 optimize：给游戏 exe 建 BatterBabel-<gameId> 的 DSCP 46 策略（Local -> ActiveStore）。
                var gameId = Str(args, "gameId") ?? "";
                var game = GameCatalog.Find(gameId);
                if (game is null)
                {
                    const string unknown = "未知游戏";
                    SettleLinked(linkedJob, false, unknown);
                    Post(callId, true, new { ok = false, accelerated = false, message = unknown });
                    return;
                }

                var executable = WindowsGameScanner.FindExecutable(gameId);
                if (executable is null)
                {
                    var notFound = $"{game.Name} was not found. Install it in Steam or EA app, then scan again.";
                    SettleLinked(linkedJob, false, notFound);
                    Post(callId, true, new { ok = false, accelerated = false, message = notFound });
                    return;
                }

                var qos = await QosPolicy.ApplyForGameAsync(gameId, executable);
                if (!qos.Ok)
                {
                    Log($"optimize gameId={gameId} exe={executable} ok=false policy={qos.PolicyName}");
                    SettleLinked(linkedJob, false, qos.Message);
                    Post(callId, true, new
                    {
                        ok = false,
                        accelerated = false,
                        message = "Could not create the QoS rule: " + qos.Message,
                        policy = qos.PolicyName,
                        executablePath = executable,
                    });
                    return;
                }

                RememberQos(executable, qos.Dscp);
                Log($"optimize gameId={gameId} exe={executable} ok=true policy={qos.PolicyName} store={qos.Store} alreadyPresent={qos.AlreadyPresent}");
                SettleLinked(linkedJob, true, $"Boost enabled for {game.Name}.");
                Post(callId, true, new
                {
                    ok = true,
                    status = JobStatus.Succeeded,
                    accelerated = true,
                    message = $"Boost enabled for {game.Name}.",
                    policy = qos.PolicyName,
                    executablePath = executable,
                    store = qos.Store,
                    alreadyPresent = qos.AlreadyPresent,
                    persistent = string.Equals(qos.Store, "Local", StringComparison.OrdinalIgnoreCase),
                });
                return;
            }

            case "restore":
            {
                // 带 gameId：只删 BatterBabel-<gameId>；不带：删所有 BatterBabel-*。都读回确认。
                var gameId = Str(args, "gameId");
                if (!string.IsNullOrWhiteSpace(gameId))
                {
                    var label = GameCatalog.Find(gameId)?.Name ?? gameId;
                    var one = await QosPolicy.RemoveGameAsync(gameId);
                    if (one.Ok)
                    {
                        Remember(CurrentRecoveryState() with { QosEnabled = false, QosExecutable = null });
                    }

                    Log($"restore gameId={gameId} ok={one.Ok} policy={one.PolicyName}");
                    SettleLinked(linkedJob, one.Ok, one.Message);
                    Post(callId, true, one.Ok
                        ? new
                        {
                            ok = true,
                            status = JobStatus.Succeeded,
                            message = $"Boost removed for {label}.",
                            policy = one.PolicyName,
                            accelerated = false,
                        }
                        : new { ok = false, accelerated = false, message = one.Message, policy = one.PolicyName });
                    return;
                }

                var before = await QosPolicy.ActivePoliciesAsync();
                var cleared = await QosPolicy.RemoveAllAsync();
                var after = await QosPolicy.ActivePoliciesAsync();
                var removed = before.Ok && after.Ok ? Math.Max(0, before.Names.Count - after.Names.Count) : 0;
                if (cleared.Ok)
                {
                    Remember(CurrentRecoveryState() with { QosEnabled = false, QosExecutable = null });
                }

                Log($"restore all ok={cleared.Ok} removed={removed}");
                SettleLinked(linkedJob, cleared.Ok, cleared.Message);
                Post(callId, true, cleared.Ok
                    ? new
                    {
                        ok = true,
                        status = JobStatus.Succeeded,
                        message = $"Removed {removed} Batter Babel QoS rule(s). Hosts and DNS were never changed.",
                        removed,
                        accelerated = false,
                    }
                    : new { ok = false, accelerated = false, message = cleared.Message });
                return;
            }

            case "apply-route":
            {
                // 发布版 apply-route：解析线路 host -> ActiveStore 建 BatterBabel-route-<target>（<ip>/32，DSCP 46）。
                var target = Str(args, "target") ?? Str(args, "route") ?? "";
                var route = RouteCatalog.Find(target);
                if (route is null)
                {
                    const string select = "请选择要应用的线路";
                    SettleLinked(linkedJob, false, select);
                    Post(callId, true, new { ok = false, message = select });
                    return;
                }

                using var routeCancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var ip = await RouteCatalog.ResolveAsync(route.Host, routeCancel.Token);
                if (ip is null)
                {
                    var unresolved = $"Could not resolve {route.Host}";
                    SettleLinked(linkedJob, false, unresolved);
                    Post(callId, true, new { ok = false, message = unresolved, target = route.Id });
                    return;
                }

                // 统一走 S12 的 RouteQosPolicy（同样的读回校验与 fail-closed 回滚，不维护第二套命名/回滚逻辑）。
                var routeQos = await BatterBabel.Core.Qos.RouteQosPolicy.ApplyAsync(
                    new[] { new BatterBabel.Core.Qos.RouteQosEntry(route.Id, route.Host, ip) }, 46);
                var item = routeQos.Items.FirstOrDefault();
                var policyName = item?.PolicyName ?? $"BatterBabel-Route-{route.Id}";
                if (!routeQos.Ok)
                {
                    // 平台写不进 IPDstPrefix 时：策略已被 RouteQosPolicy 回滚删除，只报告，不留 catch-all。
                    var blocked = item is { HasDstPrefix: false };
                    var message = blocked
                        ? "本机 Windows 无法保留目标 IP 条件，apply-route 未生效（策略已删除）"
                        : item?.Message ?? routeQos.Message;
                    Log($"apply-route target={route.Id} ip={ip} ok=false blocked={blocked}");
                    SettleLinked(linkedJob, false, message);
                    Post(callId, true, new
                    {
                        ok = false,
                        status = JobStatus.Failed,
                        message,
                        target = route.Id,
                        policy = policyName,
                        ip,
                        featureStatus = blocked ? "BLOCKED_ON_PLATFORM" : null,
                    });
                    return;
                }

                Log($"apply-route target={route.Id} ip={ip} policy={policyName}");
                SettleLinked(linkedJob, true, $"QoS applied to {route.Name} ({ip}/32).");
                Post(callId, true, new
                {
                    ok = true,
                    status = JobStatus.Succeeded,
                    message = $"QoS applied to {route.Name} ({ip}/32). DSCP 46 is marked for traffic to this address only; hosts, DNS and proxies are never modified.",
                    policy = policyName,
                    ip,
                    target = route.Id,
                    store = "ActiveStore",
                });
                return;
            }

            case "cf-optimize":
            {
                var gameId = Str(args, "gameId") ?? "";
                var job = _board.BeginAction("cf-optimize", gameId, jobIdArg, out var refusal);
                if (job is null)
                {
                    Log($"REFUSED run_action action=cf-optimize（{refusal}）jobId={jobIdArg ?? ""}");
                    Post(callId, true, new { ok = false, status = JobStatus.Failed, message = refusal });
                    return;
                }

                if (_fixture)
                {
                    _board.Finish(job, JobStatus.Succeeded, 100, "测速完成（本地 fixture）");

                    // recheck 模式（页面传 target=recheck + domains）也走同一条只读链路：
                    // 这里只按当前游戏画像返回，不解析 domains 覆盖，避免出现第二套参数语义。
                    Log($"cf-optimize job={job.JobId} sequence={job.Sequence} gameId={gameId} target={Str(args, "target")} fixture=true");
                    Post(callId, true, DemoRoutes.OptimizeResult(job.JobId, job.Sequence, gameId));
                    return;
                }

                // 真实测速：探测在后台线程池里跑，进度只由 report 回调写回 job，
                // 页面每 300 ms 轮询 get_progress。
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(240));
                try
                {
                    var sequence = job.Sequence;
                    var payload = await LiveRoutes.OptimizeAsync(
                        gameId,
                        job.JobId,
                        sequence,
                        new BatterBabel.Core.Probing.ProbeBudget(),
                        (percent, message) => _board.Report(job, percent, message),
                        cancel.Token);

                    if (IsBusinessFailure(payload, out var failureMessage))
                    {
                        // LiveRoutes 自己在业务上失败（未知游戏/没有线路）：job 必须落 FAILED，
                        // 不能因为「调用返回了」就记 SUCCEEDED。
                        _board.Finish(job, JobStatus.Failed, job.Percent, failureMessage);
                        Log($"cf-optimize job={job.JobId} gameId={gameId} live=1 status=FAILED reason={failureMessage} sequence={job.Sequence}");
                        Post(callId, true, payload);
                        return;
                    }

                    _lastPlan = ReadPlan(payload);
                    _lastPlanAt = DateTime.UtcNow;
                    _board.Finish(job, JobStatus.Succeeded, 100, "测速完成");
                    Log($"cf-optimize job={job.JobId} gameId={gameId} live=1 sequence={job.Sequence} plan={_lastPlan.Count}");
                    Post(callId, true, payload);
                }
                catch (OperationCanceledException)
                {
                    _board.Finish(job, JobStatus.Failed, job.Percent, "测速超时（240 秒）");
                    Log($"cf-optimize job={job.JobId} gameId={gameId} live=1 status=FAILED timeout=240s sequence={job.Sequence}");
                    Post(callId, true, new { ok = false, status = JobStatus.Failed, jobId = job.JobId, message = job.Message });
                }
                catch (Exception ex)
                {
                    _board.Finish(job, JobStatus.Failed, job.Percent, "测速失败：" + ex.GetType().Name);
                    Log($"cf-optimize FAILED {ex.GetType().Name}: {ex.Message}");
                    Post(callId, true, new { ok = false, status = JobStatus.Failed, jobId = job.JobId, message = job.Message });
                }

                return;
            }

            case "cf-apply":
            {
                // 预览/自检宿主（fixture）绝不允许碰真实 hosts：页面即使带着合法 domains 也只能得到拒绝。
                if (_fixture)
                {
                    const string readOnly = "Web 壳只读：本版未实现系统写操作";
                    Log("REFUSED cf-apply fixture=1");
                    SettleLinked(linkedJob, false, readOnly);
                    Post(callId, true, new { ok = false, status = JobStatus.Failed, message = readOnly });
                    return;
                }

                // 页面传来的 domains 与本次测速计划必须对得上：只允许写「刚测出来的结果」。
                // 发布版 0.10.3 也校验 domains 的格式；这里再收紧一层，避免旧结果或伪造串直接落到 hosts。
                var plan = _lastPlan.ToArray();
                var planFresh = plan.Length > 0 && DateTime.UtcNow - _lastPlanAt <= TimeSpan.FromMinutes(15);
                const string stalePlan = "优选结果已超过 15 分钟或不存在，请重新点「开始优选」后再写入";
                var domainsArg = Str(args, "domains");
                if (!string.IsNullOrWhiteSpace(domainsArg))
                {
                    if (!planFresh)
                    {
                        Log($"cf-apply domains rejected=stale-plan entries={plan.Length}");
                        SettleLinked(linkedJob, false, stalePlan);
                        Post(callId, true, new { ok = false, status = JobStatus.Failed, message = stalePlan });
                        return;
                    }

                    var parsed = HostsMapping.Parse(domainsArg);
                    var allowed = plan.ToDictionary(entry => entry.Domain, entry => entry.Ip, StringComparer.OrdinalIgnoreCase);
                    var unknown = parsed.Entries
                        .Where(entry => !allowed.TryGetValue(entry.Domain, out var ip) || !string.Equals(ip, entry.Ip, StringComparison.Ordinal))
                        .Select(entry => entry.Domain)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (unknown.Length > 0)
                    {
                        var mismatch = "提交的映射与本次优选结果不一致：" + string.Join("、", unknown.Take(5));
                        Log($"cf-apply domains rejected=mismatch count={unknown.Length}");
                        SettleLinked(linkedJob, false, mismatch);
                        Post(callId, true, new { ok = false, status = JobStatus.Failed, message = mismatch, rejected = parsed.Rejected });
                        return;
                    }

                    if (parsed.Entries.Count == 0)
                    {
                        var rejectedOnly = $"No valid mappings to write ({parsed.Rejected} rejected).";
                        Log($"cf-apply domains rejected={parsed.Rejected} entries=0");
                        SettleLinked(linkedJob, false, rejectedOnly);
                        Post(callId, true, new { ok = false, status = JobStatus.Failed, message = rejectedOnly, rejected = parsed.Rejected });
                        return;
                    }

                    var applied = HostsRunner.Apply(parsed.Entries);
                    if (applied.Ok) { RememberHosts(parsed.Entries, pending: true); }
                    var sure = $"Wrote {applied.Entries} hosts mapping(s) (backup: {applied.Backup}).";
                    Log($"cf-apply domains ok={applied.Ok} entries={applied.Entries} rejected={parsed.Rejected} backup={applied.Backup}");
                    SettleLinked(linkedJob, applied.Ok, applied.Ok ? sure : applied.Message);
                    Post(callId, true, applied.Ok
                        ? new
                        {
                            ok = true,
                            status = JobStatus.Succeeded,
                            message = sure,
                            backup = applied.Backup,
                            rejected = parsed.Rejected,
                            entries = applied.Entries,
                            hostsPath = applied.Path,
                        }
                        : new { ok = false, status = JobStatus.Failed, message = applied.Message, rejected = parsed.Rejected, hostsPath = applied.Path });
                    return;
                }

                // 真实写入：只有拿到本次测速计划才允许；文件层事务在 HostsFileWriter 里。
                if (plan.Length == 0)
                {
                    const string message = "还没有优选结果：请先点「开始优选」";
                    SettleLinked(linkedJob, false, message);
                    Post(callId, true, new { ok = false, status = JobStatus.Failed, message });
                    return;
                }
                // cf-optimize 的 validUntil 是 15 分钟：过期的结果不再写入，避免用旧 IP 覆盖 hosts。
                if (DateTime.UtcNow - _lastPlanAt > TimeSpan.FromMinutes(15))
                {
                    const string message = "优选结果已超过 15 分钟，请重新点「开始优选」后再写入";
                    SettleLinked(linkedJob, false, message);
                    Post(callId, true, new { ok = false, status = JobStatus.Failed, message });
                    return;
                }

                var hostsResult = HostsRunner.Apply(plan);
                if (hostsResult.Ok) { RememberHosts(plan, pending: true); }
                Log($"cf-apply ok={hostsResult.Ok} entries={hostsResult.Entries} backup={hostsResult.Backup}");
                SettleLinked(linkedJob, hostsResult.Ok, hostsResult.Message);
                Post(callId, true, hostsResult.Ok
                    ? new { ok = true, status = JobStatus.Succeeded, message = hostsResult.Message, hostsPath = hostsResult.Path, entries = hostsResult.Entries }
                    : new { ok = false, status = JobStatus.Failed, message = hostsResult.Message, hostsPath = hostsResult.Path });
                return;
            }

            case "cf-restore":
            {
                // 预览/自检宿主不碰系统 hosts：还原只在 --web-live（fixture=false）里真跑。
                if (_fixture)
                {
                    const string readOnly = "Web 壳只读：本版未执行 hosts 还原";
                    Log("REFUSED cf-restore fixture=1");
                    SettleLinked(linkedJob, false, readOnly);
                    Post(callId, true, new { ok = false, status = JobStatus.Failed, message = readOnly, hostsPath = LiveRoutes.HostsPath });
                    return;
                }

                // 只删本程序的标记块，不用备份整文件回滚（否则会把用户/第三方的其它改动一起回滚）。
                var restore = HostsRunner.Strip();
                Log($"cf-restore ok={restore.Ok} removed={restore.Removed} backup={restore.Backup}");
                if (restore.Ok) { RememberHosts(Array.Empty<HostsChange>(), pending: false); }
                SettleLinked(linkedJob, restore.Ok, restore.Message);
                Post(callId, true, restore.Ok
                    ? new { ok = true, status = JobStatus.Succeeded, message = restore.Message, removed = restore.Removed, backup = restore.Backup, hostsPath = LiveRoutes.HostsPath }
                    : new { ok = false, status = JobStatus.Failed, message = restore.Message, hostsPath = LiveRoutes.HostsPath });
                return;
            }

            case "boost":
            {
                // 发布版 boost 只做一件事：把同名进程的调度优先级设成 AboveNormal。
                // QoS 是 optimize 的职责，boost 不再顺带建策略（发布版没有 merge ok）。
                var gameId = Str(args, "gameId") ?? "";
                var game = GameCatalog.Find(gameId);
                if (game is null)
                {
                    SettleLinked(linkedJob, false, "未知游戏：" + Bounded(gameId));
                    Post(callId, true, new { ok = false, message = "未知游戏：" + Bounded(gameId) });
                    return;
                }

                var processName = Path.GetFileNameWithoutExtension(game.Executable);
                var boost = BoostAction.Raise(game.Name, processName);
                if (!boost.Ok)
                {
                    Log($"boost gameId={gameId} process={processName} raised={boost.Raised}");
                    SettleLinked(linkedJob, false, boost.Message);
                    Post(callId, true, new { ok = false, message = boost.Message, raised = boost.Raised });
                    return;
                }

                Log($"boost gameId={gameId} process={processName} raised={boost.Raised}");
                SettleLinked(linkedJob, true, boost.Message);
                Post(callId, true, new
                {
                    ok = true,
                    status = JobStatus.Succeeded,
                    message = boost.Message,
                    raised = boost.Raised,
                    priorityClass = BoostAction.AboveNormalText,
                });
                return;
            }

            case "tune-system":
            {
                var tuning = await Task.Run(TuneRunner.Apply);
                if (tuning.Ok) { RememberTuning(tuning.SnapshotPath); }
                Log($"tune-system ok={tuning.Ok} status={tuning.Status} items={tuning.Items?.Count ?? 0} snapshot={tuning.SnapshotPath}");
                SettleLinked(linkedJob, tuning.Ok, tuning.Message);
                Post(callId, true, new
                {
                    ok = tuning.Ok,
                    status = tuning.Status,
                    message = tuning.Message,
                    snapshotPath = tuning.SnapshotPath,
                    items = tuning.Items?.Select(item => new
                    {
                        id = item.Id,
                        title = item.Title,
                        ok = item.Ok,
                        detail = item.Detail,
                        found = item.Found,
                        files = item.Files,
                    }),
                    log = tuning.Log,
                    changes = tuning.Changes?.Select(c => new { name = c.Name, before = c.Before, after = c.After, changed = c.Changed }),
                });
                return;
            }

            case "restore-tune":
            {
                var tuning = await Task.Run(TuneRunner.Restore);
                Log($"restore-tune ok={tuning.Ok} status={tuning.Status} items={tuning.Items?.Count ?? 0} snapshot={tuning.SnapshotPath}");
                SettleLinked(linkedJob, tuning.Ok, tuning.Message);
                Post(callId, true, new
                {
                    ok = tuning.Ok,
                    status = tuning.Status,
                    message = tuning.Message,
                    snapshotPath = tuning.SnapshotPath,
                    items = tuning.Items?.Select(item => new
                    {
                        id = item.Id,
                        title = item.Title,
                        ok = item.Ok,
                        detail = item.Detail,
                    }),
                    log = tuning.Log,
                });
                return;
            }

            default:
                // 传输层成功、业务层拒绝：页面据此显示「宿主拒绝」，而不是把通道错误当成业务错误。
                Log($"REFUSED run_action action={action}（本版只读，没有系统写能力）");
                SettleLinked(linkedJob, false, "Web 壳只读：本版未实现系统写操作");
                Post(callId, true, new { ok = false, status = JobStatus.Failed, message = "Web 壳只读：本版未实现系统写操作" });
                return;
        }
    }

    /// <summary>把一次短动作的结果写回它点名的 job（没点名就什么都不做）。</summary>
    private void SettleLinked(WebJob? job, bool success, string message)
    {
        if (job is null) { return; }
        _board.Finish(
            job,
            success ? JobStatus.Succeeded : JobStatus.Failed,
            success ? 100 : job.Percent, // 失败保留 percent，不抹零也不假造 100
            message);
    }

    /// <summary>
    /// start_job 的入口校验（未知动作在建 job 之前就被挡下）。抽成静态方法是为了让
    /// <see cref="ProtocolChecks"/> 能直接断言「未知动作不建 job」这条规则。
    /// </summary>
    internal static bool TryAcceptStartAction(string requested, out string message)
    {
        message = "";
        if (IsKnownAction(requested)) { return true; }
        message = "未知动作：" + Bounded(requested);
        return false;
    }

    /// <summary>
    /// start_job 的 action 闭集：发布版 13 个动作 + 页面启动自检用的 __probe__。
    /// 换壳后如果这里放宽成「任意字符串」，页面就会把一次假的探测当成真的能力。
    /// </summary>
    internal static bool IsKnownAction(string action) => action switch
    {
        "scan" or "scan-force" or "status" or "probe" or "cf-optimize" or "optimize"
            or "restore" or "boost" or "apply-route" or "cf-apply" or "cf-restore"
            or "tune-system" or "restore-tune" or "__probe__" => true,
        _ => false,
    };

    /// <summary>页面传来的字符串进日志/答复前先截断并压掉换行（避免伪造日志行）。</summary>
    private static string Bounded(string value)
    {
        var oneLine = value.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length <= MaxCommandLength ? oneLine : oneLine[..MaxCommandLength] + "…";
    }

    /// <summary>cf-optimize 的返回体是不是业务失败（LiveRoutes 用 ok:false 表示）。</summary>
    private static bool IsBusinessFailure(object payload, out string message)
    {
        message = "";
        var json = JsonSerializer.SerializeToElement(payload);
        if (json.ValueKind != JsonValueKind.Object) { return false; }
        if (!json.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False) { return false; }
        if (json.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
        {
            message = text.GetString() ?? "优选没有完成";
        }
        if (message.Length == 0) { message = "优选没有完成"; }
        return true;
    }

    /// <summary>从 cf-optimize 的返回体里取出「域名 → IP」计划（页面不消费这个字段）。</summary>
    /// <summary>
    /// 把「重启后需要恢复什么」写进 desired.json（S8），并顺手注册登录恢复任务。
    /// 非管理员时不注册任务（会失败）但先把期望状态记下来，避免用户提权后丢失目标。
    /// </summary>
    private void Remember(RecoveryState state)
    {
        var next = state with { UpdatedAt = DateTimeOffset.Now };
        try
        {
            BootRecovery.WriteDesired(AppExecutablePath, next);
            if (!BootRecovery.IsAdministrator())
            {
                Log("recovery desired saved（非管理员：未注册登录恢复任务）");
                return;
            }

            var task = BootRecovery.EnsureTask(AppExecutablePath);
            Log($"recovery task ok={task.Ok} {task.Message}");
        }
        catch (Exception ex)
        {
            Log($"recovery desired FAILED {ex.GetType().Name}");
        }
    }

    private void RememberHosts(IReadOnlyList<HostsChange> plan, bool pending) =>
        Remember(CurrentRecoveryState() with
        {
            HostsPending = pending,
            HostsEntries = pending ? plan.Count : 0,
            Hosts = pending ? plan.Select(c => new HostsDesiredEntry(c.Domain, c.Ip)).ToArray() : null,
        });

    private void RememberQos(string executable, string? dscp) =>
        Remember(CurrentRecoveryState() with
        {
            QosEnabled = true,
            QosExecutable = executable,
            QosDscp = int.TryParse(dscp, out var value) ? value : QosPolicy.DefaultDscp,
        });

    private void RememberTuning(string? snapshotPath) =>
        Remember(CurrentRecoveryState() with { TuningSnapshot = snapshotPath });

    private static RecoveryState CurrentRecoveryState() =>
        BootRecovery.ReadDesired()
        ?? new RecoveryState(false, 0, false, null, QosPolicy.DefaultDscp, null, DateTimeOffset.Now);

    private static string AppExecutablePath => Environment.ProcessPath ?? "BatterBabel.exe";

    private static List<HostsChange> ReadPlan(object payload)
    {
        var plan = new List<HostsChange>();
        var json = JsonSerializer.SerializeToElement(payload);
        if (!json.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) { return plan; }
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("domain", out var domain) || !entry.TryGetProperty("ip", out var ip)) { continue; }
            var domainText = domain.GetString();
            var ipText = ip.GetString();
            if (domainText is null || ipText is null) { continue; }
            plan.Add(new HostsChange(domainText, ipText));
        }

        return plan;
    }

    private void Post(string callId, bool ok, object? payload)
    {
        var body = JsonSerializer.Serialize(new { callId, ok, payload });
        _core.PostWebMessageAsJson(body);
        Log($"send callId={callId} ok={ok}");
    }

    private void Reject(string? callId, string error, string message)
    {
        StructuredFailures++;
        if (string.IsNullOrEmpty(callId) || !IsSafeToken(callId, MaxCallIdLength))
        {
            Drop($"DROP 坏信封（{error}：{message}），无可用 callId");
            return;
        }

        // 结构化运输失败：页面拿到的是一次明确的「没成立」，不必等超时。
        Post(callId, false, new { error, message });
    }

    private void Drop(string message)
    {
        DroppedEnvelopes++;
        Log(message);
    }

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Debug.WriteLine(line);
        _log?.Invoke(line);
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ---------------------------------------------------------------- job 协议自检

    /// <summary>
    /// job 协议自检：纯内存驱动 <see cref="JobBoard"/>，不依赖 WebView2、不依赖页面，
    /// 任何返回 false 的一条都表示协议不成立。主代理可以直接在启动自检里调用它。
    /// </summary>
    internal static List<(string Name, bool Pass, string Detail)> ProtocolChecks()
    {
        var checks = new List<(string Name, bool Pass, string Detail)>();
        void Check(string name, bool pass, string detail) => checks.Add((name, pass, detail));

        // 1) 动作闭集：13 个发布版动作 + __probe__，大小写/空格/空串一律不认。
        var released = new[]
        {
            "scan", "scan-force", "status", "probe", "cf-optimize", "optimize", "restore",
            "boost", "apply-route", "cf-apply", "cf-restore", "tune-system", "restore-tune",
        };
        Check(
            "action-closed-set",
            released.Length == 13 && released.All(IsKnownAction) && IsKnownAction("__probe__")
                && !IsKnownAction("") && !IsKnownAction("rm-rf") && !IsKnownAction("CF-OPTIMIZE")
                && !IsKnownAction("cf-optimize "),
            $"released={released.Length}, probe=__probe__, rejected=(\"\",\"rm-rf\",\"CF-OPTIMIZE\",\"cf-optimize \")");

        // 2) 未知动作：宿主在建 job 之前就拒绝，且话术是「未知动作：…」。
        var fresh = new JobBoard();
        var acceptedUnknown = TryAcceptStartAction("rm-rf", out var unknownMessage);
        Check(
            "unknown-action-no-job",
            !acceptedUnknown && unknownMessage == "未知动作：rm-rf" && fresh.JobCount == 0 && fresh.CreatedCount == 0,
            $"accepted={acceptedUnknown}, message={unknownMessage}, jobs={fresh.JobCount}, created={fresh.CreatedCount}");

        var board = new JobBoard();

        // 3) 没有 job 时 get_progress 必须回 IDLE 固定形状。
        var idle = board.Progress(null);
        Check(
            "idle-snapshot",
            idle.JobId.Length == 0 && idle.Status == JobStatus.Idle && idle.Sequence == 0
                && idle.Percent == 0 && idle.Message == "就绪",
            $"{idle.JobId}|{idle.Status}|{idle.Sequence}|{idle.Percent}|{idle.Message}");

        // 4) start_job 建 job：字段齐全，且不伪造进度（percent=0、sequence=0、未终态）。
        var first = board.Start("cf-optimize", "limbus", out var startRefusal);
        Check(
            "start-creates-job",
            first is not null && first.JobId.StartsWith("web-job-", StringComparison.Ordinal)
                && first.Action == "cf-optimize" && first.GameId == "limbus"
                && first.Status == JobStatus.Running && first.Sequence == 0 && first.Percent == 0
                && first.FinishedAt is null && startRefusal.Length == 0,
            first is null
                ? "job=null"
                : $"jobId={first.JobId}, action={first.Action}, status={first.Status}, seq={first.Sequence}, pct={first.Percent}");

        // 5) 同动作重复 start_job：业务拒绝、话术固定、不建第二个 job。
        var createdBefore = board.CreatedCount;
        var duplicate = board.Start("cf-optimize", "limbus", out var busyRefusal);
        Check(
            "start-refuses-duplicate",
            duplicate is null && busyRefusal == JobBoard.BusyMessage
                && board.CreatedCount == createdBefore && board.JobCount == createdBefore,
            $"refusal={busyRefusal}, created={board.CreatedCount}, job={(duplicate is null ? "(null)" : duplicate.JobId)}");

        // 6) 换动作的 start_job：旧 job 收成终态（不留悬着的 RUNNING），新 job 上马。
        var second = board.Start("scan", "limbus", out _);
        Check(
            "start-supersedes-other-action",
            second is not null && first is not null && first.IsTerminal && first.Status == JobStatus.Failed
                && first.FinishedAt is not null && board.CreatedCount == createdBefore + 1,
            $"old={first?.Status ?? "(null)"}, new={second?.JobId ?? "(null)"}, created={board.CreatedCount}");

        // 7) run_action 认领 start_job 那个 job：开跑时 percent 仍是 0（live 不伪造进度）。
        var claimed = board.BeginAction("cf-optimize", "limbus", second?.JobId, out var claimRefusal);
        if (claimed is null)
        {
            Check("begin-action-claims-job", false, $"BeginAction 返回 null（refusal={claimRefusal}）");
            return checks;
        }

        Check(
            "begin-action-claims-job",
            claimRefusal.Length == 0 && claimed.JobId == second?.JobId
                && claimed.Percent == 0 && claimed.Sequence == 1 && board.InFlight is not null,
            $"jobId={claimed.JobId}, pct={claimed.Percent}, seq={claimed.Sequence}");

        var timeline = new List<int> { claimed.Sequence };

        // 8) 并发闸：已经在跑时第二次 run_action 必须被挡，且不建新 job。
        var blocked = board.BeginAction("cf-optimize", "limbus", claimed.JobId, out var blockedRefusal);
        Check(
            "begin-action-refuses-concurrent",
            blocked is null && blockedRefusal == JobBoard.BusyMessage && board.CreatedCount == createdBefore + 1,
            $"refusal={blockedRefusal}, created={board.CreatedCount}");

        // 9) 进度只由 report 回调推进（percent/message/sequence 一起动）。
        var reported = board.Report(claimed, 42, "解析 cf.example");
        timeline.Add(claimed.Sequence);
        Check(
            "report-moves-progress",
            reported && claimed.Percent == 42 && claimed.Message == "解析 cf.example" && claimed.Sequence == 2,
            $"reported={reported}, pct={claimed.Percent}, msg={claimed.Message}, seq={claimed.Sequence}");

        // 10) 成功终态：SUCCEEDED/100，sequence 只 +1，FinishedAt 落定，并发闸释放。
        var succeeded = board.Finish(claimed, JobStatus.Succeeded, 100, "测速完成");
        timeline.Add(claimed.Sequence);
        Check(
            "finish-success-once",
            succeeded && claimed.Status == JobStatus.Succeeded && claimed.Percent == 100
                && claimed.FinishedAt is not null && board.InFlight is null && claimed.Sequence == 3,
            $"status={claimed.Status}, pct={claimed.Percent}, seq={claimed.Sequence}, inflight={(board.InFlight is null ? "none" : "set")}");

        // 11) 幂等：重复置终态不改状态、不回退、不重复加 sequence。
        var replay = board.Finish(claimed, JobStatus.Failed, 7, "重放");
        timeline.Add(claimed.Sequence);
        Check(
            "finish-idempotent",
            !replay && claimed.Status == JobStatus.Succeeded && claimed.Percent == 100
                && claimed.Message == "测速完成" && claimed.Sequence == 3,
            $"replay={replay}, status={claimed.Status}, pct={claimed.Percent}, seq={claimed.Sequence}");

        // 12) 终态之后不再接受进度（sequence/percent/message 冻结）。
        var lateReport = board.Report(claimed, 88, "迟到的进度");
        timeline.Add(claimed.Sequence);
        Check(
            "terminal-freezes-progress",
            !lateReport && claimed.Percent == 100 && claimed.Message == "测速完成" && claimed.Sequence == 3,
            $"late={lateReport}, pct={claimed.Percent}, msg={claimed.Message}, seq={claimed.Sequence}");

        // 13) get_progress 无参回最近一个，点名 jobId 回同一个（sequence/status 一致）。
        var latest = board.Progress(null);
        var byId = board.Progress(latest.JobId);
        Check(
            "progress-reads-same-job",
            latest.JobId == claimed.JobId && byId.JobId == claimed.JobId
                && byId.Sequence == latest.Sequence && byId.Status == JobStatus.Succeeded && byId.Percent == 100,
            $"latest={latest.JobId}/{latest.Status}/{latest.Sequence}, byId={byId.JobId}/{byId.Status}/{byId.Sequence}");

        // 14) sequence 时间线：1(开跑) → 2(进度) → 3(终态) → 3(重放) → 3(迟到进度)，只增不减。
        var monotonic = timeline.Zip(timeline.Skip(1), (a, b) => b >= a).All(ok => ok);
        Check(
            "sequence-timeline",
            monotonic && timeline.SequenceEqual(new[] { 1, 2, 3, 3, 3 }),
            $"{string.Join("→", timeline)} (monotonic={monotonic})");

        // 15) 失败终态保留 percent（不抹零、也不假造 100），且同样只跃迁一次。
        var third = board.Start("cf-apply", "limbus", out _);
        var applyJob = board.BeginAction("cf-apply", "limbus", third?.JobId, out _);
        if (applyJob is null)
        {
            Check("finish-failure-keeps-percent", false, "BeginAction(cf-apply) 返回 null");
            return checks;
        }

        board.Report(applyJob, 55, "写入中");
        var failedOnce = board.Finish(applyJob, JobStatus.Failed, applyJob.Percent, "写入被拒绝");
        var failedReplay = board.Finish(applyJob, JobStatus.Succeeded, 100, "重放");
        Check(
            "finish-failure-keeps-percent",
            failedOnce && !failedReplay && applyJob.Status == JobStatus.Failed
                && applyJob.Percent == 55 && applyJob.FinishedAt is not null && board.InFlight is null,
            $"once={failedOnce}, replay={failedReplay}, status={applyJob.Status}, pct={applyJob.Percent}");

        return checks;
    }
}
