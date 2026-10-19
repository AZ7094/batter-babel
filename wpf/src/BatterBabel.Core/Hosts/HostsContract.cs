namespace BatterBabel.Core.Hosts;

using BatterBabel.Core.Domain;

/// <summary>
/// hosts 写入错误码，逐字沿用 scripts/hosts-core.ps1 的错误码集合。
/// **不要新增、不要改名**：这些字符串已经是现有日志/测试/用户提示的一部分，
/// C# 实现如果换了名字，排障口径就断了。
/// </summary>
public enum HostsErrorCode
{
    None,
    Malformed,
    EncodingLossy,
    Conflict,
    FileBusy,
    WriteFailed,
    WriteVerifyFailed,
    FileNotFound,
}

public sealed record HostsApplyResult(HostsErrorCode Code, string Message, string? BackupPath = null)
{
    public bool Success => Code == HostsErrorCode.None;
}

/// <summary>
/// hosts 应用/还原契约。原型阶段只有一个拒绝真实写入的实现。
///
/// 未来 C# 实现必须逐项满足下列已验证的事务顺序（一手来源 scripts/hosts-core.ps1:571-720，
/// Write-BabelHostsDocument），任何"简化"都会丢安全保障：
///   1. 前置校验：文档必须可解析（MALFORMED），编码必须无损（ENCODING_LOSSY），否则**不写入**；
///   2. 取排他锁：&lt;hosts&gt;.batterbabel.lock，FileShare.None 独占句柄 + 50ms 轮询，
///      不做时间戳过期/窃取（FileStateLock.java:23-32 明确 tryLock 不构成跨实现互斥）；
///   3. 乐观锁：比对进入时的文件哈希，不一致 -> CONFLICT，绝不覆盖别人的写入；
///   4. 编码：按原文件的编码 + BOM 状态写回，不擅自转 UTF-8；
///   5. 落盘：同目录 .batterbabel-&lt;guid&gt;.tmp，独占创建 + fsync；
///   6. 权限：先复制源文件 Sddl 再 Replace（否则文件系统会重算继承，BB-102d 实测）；
///   7. 替换：File.Replace(temp, path, backup)，重试 8 次 / 2000ms / 25->400ms 退避；
///   8. 读回：字节级读回（否则 WRITE_VERIFY_FAILED），描述符回读 + 一次回写；
///   9. 语义读回：必须可解析、Unmanaged 区逐字未变、每个托管块一致；
///  10. 失败语义：**不回滚**，保留备份（并发写入者可能已经写过，回滚会覆盖它）；
///  11. 托管块语义：未在本轮提交中的 tag 一律不动（ManagedHostsText.java:33-40），
///      照搬脚本的"整范围提交"会静默删块。
/// </summary>
public interface IHostsApplier
{
    /// <summary>true 表示本实现不会触碰真实 hosts 文件。</summary>
    bool IsSimulationOnly { get; }

    Task<HostsApplyResult> ApplyAsync(IReadOnlyList<DomainProbeResult> bests, CancellationToken cancellationToken = default);

    Task<HostsApplyResult> RestoreAsync(CancellationToken cancellationToken = default);
}

/// <summary>真实 hosts 路径（只做路径拼接，不读不写；用于界面展示）。</summary>
public static class HostsTarget
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "drivers", "etc", "hosts");

    public static string DisplayPath => Path;
}

/// <summary>
/// 原型阶段的 hosts 实现：**拒绝真实写入**并如实回报。
/// 这不是 TODO 占位——用户明确要求本阶段禁止真实 hosts 写入与提权，
/// 因此按钮必须给出清晰、可理解的拒绝原因，而不是假装成功或静默无响应。
/// </summary>
public sealed class RefusingHostsApplier : IHostsApplier
{
    public bool IsSimulationOnly => true;

    public Task<HostsApplyResult> ApplyAsync(IReadOnlyList<DomainProbeResult> bests, CancellationToken cancellationToken = default) =>
        Task.FromResult(new HostsApplyResult(
            HostsErrorCode.WriteFailed,
            $"原型阶段不写 hosts（本次仅模拟 {bests.Count} 个域名）。真实写入需先完成 C# 事务实现与备份/回滚验收。"));

    public Task<HostsApplyResult> RestoreAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new HostsApplyResult(
            HostsErrorCode.FileNotFound,
            "原型阶段不还原 hosts：没有本程序写下的备份，也不会改动现有文件。"));
}
