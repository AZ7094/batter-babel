using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using BatterBabel.Core.Hosts;

namespace BatterBabel.App.Web;

/// <summary>
/// hosts 写/还原的进程边界：普通权限下的进程不直接写文件，而是把计划写到临时目录，
/// 再用 runas 启动自己的提权入口（--hosts-apply / --hosts-strip），等它把结果写回。
/// 这样 UAC 只弹一次、结果可回传，UI 进程始终以 asInvoker 运行。
/// </summary>
internal static class HostsRunner
{
    public static HostsResult Apply(IReadOnlyList<HostsChange> changes)
    {
        if (changes.Count == 0) { return new HostsResult(false, "没有可写入的优选结果", LiveRoutes.HostsPath, "", 0); }
        if (IsElevated())
        {
            return HostsFileWriter.Apply(LiveRoutes.HostsPath, changes, HostsFileWriter.DefaultBackupDir);
        }

        var plan = Path.Combine(Path.GetTempPath(), "bb-hosts-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new { changes }), new System.Text.UTF8Encoding(false));
        return RunElevated("--hosts-apply", plan, message => new HostsResult(false, message, LiveRoutes.HostsPath, "", 0));
    }

    /// <summary>发布版 cf-restore：只删本程序的标记块（不是整文件回滚到备份）。</summary>
    public static HostsStripResult Strip()
    {
        if (IsElevated())
        {
            return HostsFileWriter.Strip(LiveRoutes.HostsPath, HostsFileWriter.DefaultBackupDir);
        }

        var plan = Path.Combine(Path.GetTempPath(), "bb-hosts-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(plan, "{}", new System.Text.UTF8Encoding(false));
        return RunElevated("--hosts-strip", plan, message => new HostsStripResult(false, message, "", 0));
    }

    /// <summary>旧入口（不在页面契约里）：仍按整文件备份还原，保留给既有命令行使用。</summary>
    public static HostsResult Restore()
    {
        if (IsElevated())
        {
            return HostsFileWriter.Restore(LiveRoutes.HostsPath, HostsFileWriter.DefaultBackupDir);
        }

        var plan = Path.Combine(Path.GetTempPath(), "bb-hosts-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(plan, "{}", new System.Text.UTF8Encoding(false));
        return RunElevated("--hosts-restore", plan, message => new HostsResult(false, message, LiveRoutes.HostsPath, "", 0));
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static T RunElevated<T>(string mode, string planPath, Func<string, T> fail)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return fail("找不到当前程序路径，无法申请管理员授权");
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{mode} \"{planPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return fail("未能启动管理员进程");
            }

            if (!process.WaitForExit(120_000))
            {
                return fail("管理员进程超时未返回（120 秒）");
            }

            var resultPath = planPath + ".result.json";
            if (!File.Exists(resultPath))
            {
                return fail("管理员进程没有返回结果（可能取消了 UAC 授权）");
            }

            var result = JsonSerializer.Deserialize<T>(File.ReadAllText(resultPath));
            return result ?? fail("管理员进程返回了无法解析的结果");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return fail("已取消管理员授权，hosts 未改动");
        }
        catch (Exception ex)
        {
            return fail("申请管理员授权失败：" + ex.GetType().Name);
        }
        finally
        {
            try { File.Delete(planPath); File.Delete(planPath + ".result.json"); } catch (IOException) { }
        }
    }
}
