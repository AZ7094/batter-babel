namespace BatterBabel.Core.Domain;

/// <summary>
/// 线路标签。与 scripts/optimization.ps1 的 OptGroup.Tag 以及 src/public/app.css 的
/// .tag-* 配色一一对应，C# 侧不新增标签。
/// </summary>
public enum RouteTag
{
    Cf,
    Amazon,
    Steam,
    Ea,
    Klei,
    Ubisoft,
    Epic,
    Blizzard,
}

/// <summary>
/// 探测模式，直接对应 optimization.ps1 里 OptGroup.Mode：
///   Cloudflare = 对精选官方 Cloudflare 地址测下载速度（模式 'cloudflare'）；
///   Probe      = 系统 DNS + 公共 DoH 解析候选地址后再逐个测（模式 'probe'）。
/// </summary>
public enum ProbeMode
{
    Cloudflare,
    Probe,
}

/// <summary>
/// 单个候选地址的判定，对应 cf-probe.ps1 的 verdict：
///   Usable   = 正常响应且测到有效延迟，可以参与排名、可以写入 hosts；
///   Keep     = 可达但返回反机器人页（403/405），永不写入 hosts，一次采样即终结；
///   Unusable = 传输失败或规则不匹配，永不写入 hosts，一次采样即终结。
/// </summary>
public enum ProbeVerdict
{
    Usable,
    Keep,
    Unusable,
}

/// <summary>
/// 「为什么这个候选不能参与排名」，沿用 cf-probe.ps1 的 rankReason 字符串语义。
/// 它会被当作面向用户的回退原因显示，所以不要退化成笼统的 None。
/// </summary>
public enum RankReason
{
    None,
    NotUsable,
    NoValidLatencySamples,
    InsufficientSamples,
}

/// <summary>一个可优化的线路分组（界面上就是一张卡片）。</summary>
public sealed record OptGroup(string Label, RouteTag Tag, ProbeMode Mode, IReadOnlyList<string> Domains);

/// <summary>一个游戏的优选画像。字段与 optimization.ps1 的 $Catalog 条目对齐。</summary>
public sealed record GameProfile(
    string Id,
    string Name,
    string AppId,
    string Executable,
    string RelativePath,
    string Vendor,
    IReadOnlyList<OptGroup> OptGroups,
    bool Standalone = false,
    bool HelpsGameplay = false);

/// <summary>一个候选地址的完整探测结果，可直接用于界面展示。</summary>
public sealed record RouteCandidate(
    string Address,
    ProbeVerdict Verdict,
    double? MedianMs,
    int Samples,
    int StatusCode,
    double? DownloadMiBps,
    RankReason RankReason,
    bool Rankable,
    string? Reason = null);

/// <summary>单个域名的探测汇总：候选按 cf-probe 的排序键排好序，Best 为可写入的赢家。</summary>
public sealed record DomainProbeResult(
    string Domain,
    IReadOnlyList<RouteCandidate> Candidates,
    RouteCandidate? Best)
{
    /// <summary>没有赢家时给界面/日志用的解释，例如 "no usable address (InsufficientSamples)"。</summary>
    public string BestUnavailableReason =>
        Best is not null
            ? "None"
            : Candidates.FirstOrDefault()?.Reason ?? "NoCandidate";
}

/// <summary>一个线路分组（卡片）的探测汇总。</summary>
public sealed record RouteGroupResult(OptGroup Group, IReadOnlyList<DomainProbeResult> Domains);
