namespace BatterBabel.Core.Tuning;

/// <summary>
/// 一项系统参数的改动记录（保留给旧调用方：App 的 <c>TuningLine</c> 与 BbRpcHost 的 changes[]）。
/// 新代码用 <see cref="TuneItem"/>：那一份是页面契约，这一份只是兼容外壳。
/// </summary>
public sealed record TuningChange(string Name, string Before, string After, bool Changed);

/// <summary>
/// 页面要的一条结果：id 与发布版 v0.10.3 逐字一致（<c>net-set-&lt;key&gt;</c> / <c>net-restore-&lt;key&gt;</c> /
/// <c>bg-programs</c> / <c>game-config</c>，还原侧 tcp-nagle / tcp-global / adapter / adapter-power /
/// throttle / wifi-power / game-configs），title/detail 是给人看的中文。
/// </summary>
public sealed record TuneItem(
    string Id,
    string Title,
    bool Ok,
    string Detail,
    IReadOnlyList<string>? Found = null,
    IReadOnlyList<string>? Files = null)
{
    public IReadOnlyList<string> FoundList => Found ?? Array.Empty<string>();
    public IReadOnlyList<string> FileList => Files ?? Array.Empty<string>();
}

/// <summary>
/// 页面认识的状态码（src/public/app.js:1210-1285 只认这几种）。
/// 发布版没有 status 字段，这是 WPF 侧为了区分"全应用/部分应用/无快照"加的，
/// 取值刻意落在页面已有分支上。
/// </summary>
public static class TuneStatus
{
    /// <summary>调优：所有 net-* 与 game-config 项都成功。</summary>
    public const string Applied = "APPLIED";

    /// <summary>调优：至少一项没落地，快照保留，可重试。</summary>
    public const string Partial = "PARTIAL";

    /// <summary>还原：7 个还原项都成功，快照已消费。</summary>
    public const string Restored = "RESTORED";

    /// <summary>还原：没有快照（从未调优，或上一次还原已消费掉快照）。</summary>
    public const string NoSnapshot = "NO_SNAPSHOT";

    /// <summary>提权被拒 / 快照无法持久化 → 未做任何系统修改。</summary>
    public const string Blocked = "BLOCKED";

    /// <summary>参数不合法等不可重试的失败。</summary>
    public const string Failed = "FAILED";
}

/// <summary>
/// 一次调优/还原的返回体。前 5 个成员是旧 API（App 与 BbRpcHost 已经在读，不能删），
/// 后面 3 个是本轮新增：Items/Log 是页面契约，Status 是页面判成功用的码值。
/// <see cref="RebootRequired"/> 永远为 false——发布版全仓没有这个字段（见 TUNE-PARITY §4），
/// 保留只为不打断现有调用方。
/// </summary>
public sealed record TuningResult(
    bool Ok,
    string Message,
    string? SnapshotPath,
    IReadOnlyList<TuningChange> Changes,
    bool RebootRequired,
    IReadOnlyList<TuneItem>? Items = null,
    IReadOnlyList<string>? Log = null,
    string Status = TuneStatus.Failed)
{
    public IReadOnlyList<TuneItem> ItemList => Items ?? Array.Empty<TuneItem>();
    public IReadOnlyList<string> LogLines => Log ?? Array.Empty<string>();
}

/// <summary>
/// 调优上下文。发布版从 catalog 里找游戏、按 ExecutableParentDepth 推安装根目录；
/// Core 不引用 App 的扫描器，所以安装根目录（或精确的配置文件目标）由调用方给。
/// 不给就是"只调系统参数、不写游戏配置块"，与发布版对未安装游戏的处理一致。
/// </summary>
public sealed record TuneRequest(
    string? GameId = null,
    string? InstallRoot = null,
    IReadOnlyList<TuneConfigTarget>? ConfigTargets = null,
    bool ScanBackground = true)
{
    public static TuneRequest Default { get; } = new();

    public IReadOnlyList<TuneConfigTarget> TargetList => ConfigTargets ?? Array.Empty<TuneConfigTarget>();
}

/// <summary>一个要写托管块的配置文件：路径由调用方定，行内容来自档位配方。</summary>
public sealed record TuneConfigTarget(string Path, IReadOnlyList<string> Lines);
