using System;
using System.Diagnostics;
using System.IO;

namespace BatterBabel.App.Web;

/// <summary>
/// 发布版 boost 的唯一语义：把同名进程的 <see cref="ProcessPriorityClass"/> 设为 AboveNormal。
///
/// 独立出来只为一件事：<c>--parity-selftest</c> 要跑**同一段生产代码**，而不是在测试里
/// 重写一遍设优先级的逻辑（那只能证明测试自己会提升优先级）。QoS 不在这里，属 optimize。
/// </summary>
internal static class BoostAction
{
    public const string AboveNormalText = "AboveNormal";

    public sealed record Outcome(bool Ok, string Message, int Raised);

    /// <summary>按进程名提升优先级。<paramref name="displayName"/> 只用于消息文案（发布版用游戏名）。</summary>
    public static Outcome Raise(string displayName, string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return new Outcome(false, $"{displayName} is not running", 0);
        }

        var name = Path.GetFileNameWithoutExtension(processName.Trim());
        var processes = name.Length == 0 ? Array.Empty<Process>() : Process.GetProcessesByName(name);
        if (processes.Length == 0)
        {
            return new Outcome(false, $"{displayName} is not running", 0);
        }

        var raised = 0;
        foreach (var process in processes)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.AboveNormal;
                raised++;
            }
            catch (Exception)
            {
                // 进程刚退出或系统拒绝：不算整次失败，按实际成功的个数回报。
            }
            finally
            {
                process.Dispose();
            }
        }

        return raised == 0
            ? new Outcome(false, $"{displayName} is not running", 0)
            : new Outcome(true, $"Raised scheduling priority for {raised} {displayName} process(es)", raised);
    }
}
