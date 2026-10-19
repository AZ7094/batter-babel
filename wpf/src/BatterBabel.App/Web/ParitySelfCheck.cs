using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using BatterBabel.Core.Hosts;
using BatterBabel.Core.Qos;

namespace BatterBabel.App.Web;

/// <summary>
/// <c>--parity-selftest &lt;json&gt;</c>：把「C# 宿主动作 vs 发布版 0.10.3 语义」逐条落成可复跑的断言。
///
/// 纪律：
///   * 只调用生产代码（QosPolicy / BoostAction / HostsMapping / RouteCatalog / HostsFileWriter），
///     不在测试里重写被测算法；
///   * 每条 check 都写进 JSON（name/pass/detail），fail 时退出码 1；
///   * 需要管理员权限的检查如果当前进程未提权，记为 pass=false 且 detail 写明「SKIPPED(未提权)」，
///     **不把未验证写成通过**；
///   * 临时策略名 <c>BatterBabel-parity-selftest</c> 用完即删，结束时复查残留为 0。
/// </summary>
internal static class ParitySelfCheck
{
    private const string GameId = "parity-selftest";
    private const string PolicyName = "BatterBabel-" + GameId;

    public static int Run(string reportPath)
    {
        var checks = new List<object>();
        void Check(string name, bool pass, string detail) => checks.Add(new { name, pass, detail });
        void Skip(string name, string detail) => checks.Add(new { name, pass = false, skipped = true, detail = "SKIPPED(" + detail + ")" });

        var elevated = QosPolicy.CurrentProcessElevated();

        // 1) status：elevated 必须是真实进程令牌状态，activePolicies 必须来自真实 Get-NetQosPolicy。
        var policies = QosPolicy.ActivePoliciesAsync().GetAwaiter().GetResult();
        Check("status-elevated-real", elevated == QosPolicy.CurrentProcessElevated(), "elevated=" + elevated);
        if (!elevated)
        {
            Skip("status-elevated-expected", "未提权");
        }
        else
        {
            Check("status-elevated-expected", elevated, "已提权");
        }

        Check("status-active-policies-real", policies.Ok, policies.Message);

        // 2) boost：真实起一个名为 notepad.exe 的替身进程，走生产代码 BoostAction（不是测试自己设优先级）。
        //    Windows 11 的 notepad 会单实例交接后立刻退出、PriorityClass 读不到，所以用 cmd.exe 的副本
        //    改名 notepad.exe 并 sleep 20s，保证整个检查期间进程稳定存在。
        Process? notepad = null;
        try
        {
            var standInDir = Path.Combine(Path.GetTempPath(), "bb-parity-notepad");
            Directory.CreateDirectory(standInDir);
            var standInPath = Path.Combine(standInDir, "notepad.exe");
            if (!File.Exists(standInPath))
            {
                File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), standInPath, overwrite: true);
            }
            notepad = Process.Start(new ProcessStartInfo(standInPath, "/c ping -n 21 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true });
            for (var i = 0; i < 20 && notepad is not null && notepad.HasExited; i++) { Thread.Sleep(100); }
            Thread.Sleep(600);
            var raised = BoostAction.Raise("Notepad", "notepad.exe");
            Check("boost-ok", raised.Ok, raised.Message);
            Check("boost-raised-positive", raised.Raised > 0, "raised=" + raised.Raised);
            Check(
                "boost-message-shape",
                raised.Ok
                && raised.Message.StartsWith("Raised scheduling priority for ", StringComparison.Ordinal)
                && raised.Message.EndsWith(" Notepad process(es)", StringComparison.Ordinal),
                raised.Message);
            var priority = "(替身进程已退出，未读优先级)";
            var priorityOk = false;
            for (var i = 0; i < 15 && !priorityOk; i++)
            {
                try
                {
                    if (notepad is not null && !notepad.HasExited)
                    {
                        notepad.Refresh();
                        priority = notepad.PriorityClass.ToString();
                        priorityOk = notepad.PriorityClass == ProcessPriorityClass.AboveNormal;
                    }
                }
                catch (Exception ex) { priority = "读取优先级失败：" + ex.GetType().Name; }
                if (!priorityOk) { Thread.Sleep(200); }
            }
            Check("boost-priority-class", priorityOk, priority);
        }
        catch (Exception ex)
        {
            Check("boost-start", false, "启动 notepad 替身失败：" + ex.GetType().Name + ":" + ex.Message);
        }
        finally
        {
            try { notepad?.Kill(entireProcessTree: true); } catch { }
            try { notepad?.Dispose(); } catch { }
        }

        var idle = BoostAction.Raise("Notepad", "bb-no-such-process-xyz");
        Check("boost-idle-message", !idle.Ok && idle.Message == "Notepad is not running", idle.Message);

        // 3) optimize / restore：临时策略名 BatterBabel-parity-selftest，建后读回 DSCP=46，删后残留 0。
        if (!elevated)
        {
            Skip("optimize-ok", "未提权");
            Skip("optimize-readback-dscp46", "未提权");
            Skip("restore-no-residue", "未提权");
        }
        else
        {
            var exe = Environment.ProcessPath ?? "";
            if (File.Exists(exe))
            {
                var leftover = QosPolicy.InspectGameAsync(GameId).GetAwaiter().GetResult();
                if (leftover.Name is not null)
                {
                    QosPolicy.RemoveGameAsync(GameId).GetAwaiter().GetResult();
                }

                var created = QosPolicy.ApplyForGameAsync(GameId, exe).GetAwaiter().GetResult();
                Check("optimize-ok", created.Ok, created.Message);
                Check("optimize-policy-name", string.Equals(created.PolicyName, PolicyName, StringComparison.OrdinalIgnoreCase), created.PolicyName);
                Check("optimize-store", created.Store is "Local" or "ActiveStore", created.Store ?? "(null)");

                var read = QosPolicy.InspectGameAsync(GameId).GetAwaiter().GetResult();
                Check(
                    "optimize-readback-dscp46",
                    read.Name is not null && read.Dscp == 46 && string.Equals(read.Name, PolicyName, StringComparison.OrdinalIgnoreCase),
                    $"name={read.Name ?? "(null)"} dscp={read.Dscp} store={read.Store ?? "(null)"}");

                var again = QosPolicy.ApplyForGameAsync(GameId, exe).GetAwaiter().GetResult();
                Check("optimize-idempotent", again.Ok && again.AlreadyPresent, $"alreadyPresent={again.AlreadyPresent}");

                var removed = QosPolicy.RemoveGameAsync(GameId).GetAwaiter().GetResult();
                var after = QosPolicy.InspectGameAsync(GameId).GetAwaiter().GetResult();
                Check("restore-ok", removed.Ok, removed.Message);
                Check("restore-no-residue", after.Name is null, after.Message);
            }
            else
            {
                Check("optimize-ok", false, "找不到本进程 exe（" + exe + "）");
            }
        }

        // 4) probe：8 条线路真实探测，rank 唯一且 tcp 为真实数字；icmp/download 必须为 null（本版没实现）。
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var probe = RouteCatalog.ProbeAsync(cts.Token).GetAwaiter().GetResult();
            var node = JsonSerializer.SerializeToNode(probe) as JsonObject;
            var candidates = node?["candidates"] as JsonArray;
            Check("probe-candidate-count", candidates?.Count == 8, "count=" + (candidates?.Count ?? -1));

            var ranks = candidates?
                .Select(item => item?["rank"]?.GetValue<int>() ?? -1)
                .OrderBy(value => value)
                .ToArray() ?? Array.Empty<int>();
            Check("probe-ranks-unique", ranks.SequenceEqual(Enumerable.Range(1, 8)), string.Join(",", ranks));

            var tcpCount = candidates?.Count(item => item?["tcp"]?.GetValue<double>() is > 0) ?? 0;
            Check("probe-tcp-measured", tcpCount > 0, "tcp>0 的线路=" + tcpCount);
            var icmpNull = candidates?.All(item => item?["icmp"] is null) ?? false;
            var downloadNull = node?["download"] is null;
            Check("probe-no-fake-fields", icmpNull && downloadNull, $"icmpNull={icmpNull} downloadNull={downloadNull}");
        }
        catch (Exception ex)
        {
            Check("probe-candidate-count", false, "探测失败：" + ex.GetType().Name);
        }

        // 5) cf-apply：domains 校验（3 段/tag/严格 IPv4/domain 正则/CF 官方段）。
        var invalid = HostsMapping.Parse(
            "CF|1.1.1.1|not a domain;" +
            "XX|999.1.1.1|ok.example;" +
            "CF|8.8.8.8|wrong-cf.example;" +
            "BAD_TAG|104.16.1.1|ok.example;" +
            "CF|104.016.1.1|leading-zero.example");
        Check("cf-apply-rejects-invalid", invalid.Rejected == 5 && invalid.Entries.Count == 0, $"rejected={invalid.Rejected} entries={invalid.Entries.Count}");
        var valid = HostsMapping.Parse("CF|104.16.1.1|ok.example");
        Check("cf-apply-accepts-cf", valid.Rejected == 0 && valid.Entries.Count == 1, $"rejected={valid.Rejected} entries={valid.Entries.Count}");
        Check("cf-apply-strict-ipv4", !HostsMapping.IsStrictIPv4("104.016.1.1") && !HostsMapping.IsStrictIPv4("256.1.1.1"), "前导零/越界都拒绝");

        // 6) cf-restore：只删标记块，块外（含写入后用户新增的行）逐字节保留。
        var root = Path.Combine(Path.GetTempPath(), "bb-parity-" + Guid.NewGuid().ToString("N"));
        var hosts = Path.Combine(root, "hosts");
        var backups = Path.Combine(root, "backup");
        Directory.CreateDirectory(root);
        var original = "# parity\r\n127.0.0.1 localhost\r\n10.0.0.9 keep.example\r\n";
        File.WriteAllText(hosts, original, new UTF8Encoding(false));
        var originalBytes = File.ReadAllBytes(hosts);
        var applied = HostsFileWriter.Apply(hosts, new[] { new HostsChange("ok.example", "104.16.1.1") }, backups);
        var foreign = "192.0.2.7 added-after-apply\r\n";
        File.AppendAllText(hosts, foreign, new UTF8Encoding(false));
        var stripped = HostsFileWriter.Strip(hosts, backups);
        var expected = originalBytes.Concat(Encoding.UTF8.GetBytes(foreign)).ToArray();
        var actual = File.ReadAllBytes(hosts);
        Check("cf-apply-fixture-ok", applied.Ok, applied.Message);
        Check("cf-restore-ok", stripped.Ok, stripped.Message);
        Check("cf-restore-bytes-equal", actual.SequenceEqual(expected), actual.Length + " B / 期望 " + expected.Length + " B");

        // 7) apply-route：本机 Windows 会静默丢弃 IPDstPrefixMatchCondition。
        //    契约是 fail-closed：ok=false + 策略已回滚（残留 0），绝不留下 catch-all 规则。
        if (!elevated)
        {
            Skip("apply-route-fail-closed", "未提权");
        }
        else
        {
            var route = BatterBabel.Core.Qos.RouteQosPolicy.ApplyAsync(
                new[] { new BatterBabel.Core.Qos.RouteQosEntry("parity-selftest", "parity.example", "1.1.1.1") }, 46)
                .GetAwaiter().GetResult();
            var routePolicies = QosPolicy.ActivePoliciesAsync().GetAwaiter().GetResult();
            var remaining = routePolicies.Names.Count(name => name.StartsWith(BatterBabel.Core.Qos.RouteQosPolicy.PolicyPrefix, StringComparison.OrdinalIgnoreCase));
            var blocked = !route.Ok && route.Items.Count > 0 && !route.Items[0].HasDstPrefix;
            if (route.Ok)
            {
                // 平台保留了 IP 条件：这与 S12 实测不一致，不能算通过，先记下来并清理。
                Check("apply-route-fail-closed", false, "平台保留了 IP 条件（与 S12 实测不一致）" + route.Message);
                BatterBabel.Core.Qos.RouteQosPolicy.RemoveAsync().GetAwaiter().GetResult();
            }
            else
            {
                Check(
                    "apply-route-fail-closed",
                    remaining == 0 && blocked,
                    $"ok=false blocked={blocked} remaining={remaining} message={route.Message}");
            }
        }

        try { Directory.Delete(root, true); } catch { }

        var passed = checks.All(item => (bool)item.GetType().GetProperty("pass")!.GetValue(item)!);
        var payload = new
        {
            mode = "--parity-selftest",
            passed,
            elevated,
            policyName = PolicyName,
            hostOs = Environment.OSVersion.VersionString,
            checks,
        };
        var folder = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine((passed ? "PARITY PASS" : "PARITY FAIL") + " " + reportPath + " elevated=" + elevated);
        return passed ? 0 : 1;
    }
}
