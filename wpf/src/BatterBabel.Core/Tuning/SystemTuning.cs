namespace BatterBabel.Core.Tuning;

using System.Runtime.Versioning;
using System.Security.Principal;

/// <summary>
/// 系统调优的公开入口。对外只做三件事：提权判定、把工作丢到线程池、把 <see cref="TuningResult"/>
/// 原样交回调用方。真正的编排在 <see cref="TuneEngine"/>，系统边界在 <see cref="WindowsTuneBoundary"/>。
///
/// 调用方（App 的 <c>App.xaml.cs</c> 与 <c>Web/BbRpcHost.cs</c>）目前只读
/// Ok/Message/SnapshotPath/RebootRequired/Changes；页面契约用的 Items/Log/Status 是本轮追加的
/// 可选成员。UAC 由 App 层发起——Core 只回答"需不需要管理员"，不弹窗。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemTuning
{
    /// <summary>发布版 v0.10.3 的快照路径；跨版本还原依赖这个位置，不能漂移。</summary>
    public static string DefaultSnapshotPath => TuneStateStore.DefaultPath;

    /// <summary>需要管理员权限时的中文提示（App 层的 RunAs 引导用同一句）。</summary>
    public const string ElevationMessage = "需要管理员权限：系统调优会修改注册表、网卡驱动设置与电源方案。";

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>true 表示需要提权；调用方应引导 RunAs 而不是直接调用 Apply。</summary>
    public static bool NeedsElevation => !IsAdministrator();

    /// <summary>页面/CLI 的"还原系统调优"：读 <see cref="DefaultSnapshotPath"/> 并按键序还原。</summary>
    public static Task<TuningResult> ApplyAsync(CancellationToken cancellationToken = default) =>
        ApplyAsync(TuneRequest.Default, cancellationToken);

    public static Task<TuningResult> ApplyAsync(TuneRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(token => new TuneEngine(new WindowsTuneBoundary(token), ct: token).Apply(request), cancellationToken);
    }

    public static Task<TuningResult> RestoreAsync(string? snapshotPath = null, CancellationToken cancellationToken = default) =>
        RunAsync(token => new TuneEngine(
            new WindowsTuneBoundary(token),
            statePath: string.IsNullOrWhiteSpace(snapshotPath) ? null : snapshotPath,
            ct: token).Restore(), cancellationToken);

    private static Task<TuningResult> RunAsync(Func<CancellationToken, TuningResult> work, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                return work(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new TuningResult(false, "系统调优已被取消。", null, Array.Empty<TuningChange>(), false,
                    Array.Empty<TuneItem>(), new[] { "系统调优已被取消。" }, TuneStatus.Blocked);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException or InvalidDataException)
            {
                // 提权不足是最常见的失败：给出明确的"需要管理员"而不是把异常抛到 UI 线程。
                var message = NeedsElevation ? $"{ElevationMessage}（{ex.Message}）" : $"系统调优失败：{ex.Message}";
                return new TuningResult(false, message, null, Array.Empty<TuningChange>(), false,
                    Array.Empty<TuneItem>(), new[] { message }, NeedsElevation ? TuneStatus.Blocked : TuneStatus.Failed);
            }
        }, cancellationToken);
}
