using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatterBabel.Core.Tuning;

namespace BatterBabel.App.Web;

/// <summary>
/// 系统调优的提权通道。发布版由 optimization.ps1 自己 Start-Process -Verb RunAs，
/// 这里同样把真正写入放进管理员子进程：非管理员时用 runas 重新启动自己。
/// </summary>
internal static class TuneRunner
{
    internal sealed record TuneReply(
        bool Ok,
        string Status,
        string Message,
        string? SnapshotPath,
        IReadOnlyList<TuneItem>? Items,
        IReadOnlyList<string>? Log,
        IReadOnlyList<TuningChange>? Changes);

    public static TuneReply Apply() => Run("--tune-apply");

    public static TuneReply Restore() => Run("--tune-restore");

    private static TuneReply Run(string mode)
    {
        if (SystemTuning.IsAdministrator())
        {
            var result = mode == "--tune-apply"
                ? SystemTuning.ApplyAsync().GetAwaiter().GetResult()
                : SystemTuning.RestoreAsync().GetAwaiter().GetResult();
            return new TuneReply(result.Ok, result.Status, result.Message, result.SnapshotPath,
                result.Items, result.Log, result.Changes);
        }

        var resultPath = Path.Combine(Path.GetTempPath(), "bb-tune-" + Guid.NewGuid().ToString("N") + ".json");
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return new TuneReply(false, TuneStatus.Failed, "找不到当前程序路径，无法申请管理员授权", null, null, null, null);
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{mode} --tune-result \"{resultPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return new TuneReply(false, TuneStatus.Failed, "未能启动管理员进程", null, null, null, null);
            }
            if (!process.WaitForExit(300_000))
            {
                return new TuneReply(false, TuneStatus.Failed, "管理员进程超时未返回（300 秒）", null, null, null, null);
            }
            if (!File.Exists(resultPath))
            {
                return new TuneReply(false, TuneStatus.Failed, "管理员进程没有返回结果（可能取消了 UAC 授权）", null, null, null, null);
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter());
            var reply = JsonSerializer.Deserialize<TuneReply>(File.ReadAllText(resultPath), options);
            return reply ?? new TuneReply(false, TuneStatus.Failed, "管理员进程返回了无法解析的结果", null, null, null, null);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new TuneReply(false, TuneStatus.Failed, "已取消管理员授权，系统调优未改动", null, null, null, null);
        }
        catch (Exception ex)
        {
            return new TuneReply(false, TuneStatus.Failed, "申请管理员授权失败：" + ex.GetType().Name, null, null, null, null);
        }
        finally
        {
            try { File.Delete(resultPath); } catch (IOException) { }
        }
    }
}
