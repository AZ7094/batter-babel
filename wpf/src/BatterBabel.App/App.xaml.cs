namespace BatterBabel.App;

using System.IO;
using System.Linq;
using System.Text;
using BatterBabel.Core.Hosts;
using BatterBabel.Core.Qos;
using BatterBabel.Core.Recovery;
using BatterBabel.Core.Tuning;
using System.Text.Json;
using System.Windows;
using BatterBabel.App.Web;
using BatterBabel.Core.Probing;

/// <summary>
/// 启动路由：默认走 Web 壳（复用 Tauri 版 HTML），<c>--native</c> 保留原生 WPF 原型。
/// 没有 StartupUri 是刻意的：两种入口只能有一个窗口。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        // S1 证据采集：真实扫描结果落盘后退出（不开窗口，也不依赖 WebView2 运行时）。
        var dumpPath = Value(args, "--scan-dump");
        if (dumpPath is not null)
        {
            try
            {
                var result = Discovery.WindowsGameScanner.Scan();
                var full = Path.GetFullPath(dumpPath);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
                File.WriteAllText(full, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                Console.WriteLine("scan dumped to " + full);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("scan dump failed: " + ex.Message);
                Shutdown(1);
            }

            return;
        }

        // S3 证据采集：真实测速结果落盘后退出（不开窗口，不依赖 WebView2）。
        var optimizeDump = Value(args, "--optimize-dump");
        if (optimizeDump is not null)
        {
            try
            {
                var gameId = Value(args, "--game") ?? "limbus";
                var result = LiveRoutes
                    .OptimizeAsync(gameId, "cli-job", 1, new BatterBabel.Core.Probing.ProbeBudget(), (_, _) => { }, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                var fullPath = Path.GetFullPath(optimizeDump);
                var folder = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); }
                File.WriteAllText(fullPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                Console.WriteLine("optimize dumped to " + fullPath);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("optimize dump failed: " + ex.GetType().Name + " " + ex.Message);
                Shutdown(1);
            }

            return;
        }

        // 诊断入口：打印某个域名的候选来源（系统 DNS / 阿里 DoH / 腾讯 DoH）与基线。
        var netDiag = Value(args, "--net-diag");
        if (netDiag is not null)
        {
            try
            {
                var doh = DohResolver.ResolveAsync(netDiag).GetAwaiter().GetResult();
                var system = System.Net.Dns.GetHostAddresses(netDiag)
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.ToString()).ToArray();
                Console.WriteLine("domain=" + netDiag);
                Console.WriteLine("system=" + string.Join(",", system));
                Console.WriteLine("merged=" + string.Join(",", doh));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("net diag failed: " + ex.GetType().Name + " " + ex.Message);
                Shutdown(1);
            }
            return;
        }

        // 单候选诊断：--probe-one <domain> <ip>，打印一次 HTTPS 验证的结果。
        if (Has(args, "--probe-one"))
        {
            var domain = Value(args, "--probe-one") ?? "download.limbuscompanycdn.org";
            var ip = Value(args, "--probe-ip") ?? "104.18.17.32";
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var candidate = BatterBabel.Core.Probing.RouteProber
                    .MeasureAsync(domain, ip, new BatterBabel.Core.Probing.ProbeBudget(), CancellationToken.None)
                    .GetAwaiter().GetResult();
                Console.WriteLine("probe=" + candidate.Address + " verdict=" + candidate.Verdict
                    + " median=" + candidate.MedianMs + " samples=" + candidate.Samples
                    + " status=" + candidate.StatusCode + " reason=" + candidate.Reason
                    + " ms=" + watch.ElapsedMilliseconds);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("probe-one failed: " + ex.GetType().Name + " " + ex.Message);
                Shutdown(1);
            }
            return;
        }

        // 诊断入口：分阶段跑 Cloudflare 流水线并打印每一步的耗时/结果。
        if (Has(args, "--cf-diag"))
        {
            try
            {
                var budget = new BatterBabel.Core.Probing.ProbeBudget();
                string DnsState(string label)
                {
                    try
                    {
                        var list = System.Net.Dns.GetHostAddresses("download.limbuscompanycdn.org");
                        return label + "=" + list.Length;
                    }
                    catch (Exception ex)
                    {
                        return label + "=ERR(" + ex.GetType().Name + ")";
                    }
                }
                var poolArg = Value(args, "--pool");
                var poolTarget = int.TryParse(poolArg, out var parsedPool) ? parsedPool : 2000;
                Console.WriteLine(DnsState("dnsBefore"));
                var pool = BatterBabel.Core.Probing.CloudflarePool.Snapshot(poolTarget);
                Console.WriteLine("pool=" + pool.Count + " " + DnsState("dnsAfterPool"));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var alive = BatterBabel.Core.Probing.TcpScreen
                    .ScreenAsync(pool, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine("alive=" + alive.Count + " tcpMs=" + watch.ElapsedMilliseconds + " " + DnsState("dnsAfterTcp"));
                var finals = BatterBabel.Core.Probing.DownloadFinals
                    .MeasureAsync(alive.Take(20).Select(row => row.Ip).ToArray(), CancellationToken.None)
                    .GetAwaiter().GetResult();
                Console.WriteLine(DnsState("dnsAfterFinals"));
                foreach (var final in finals.Take(6))
                {
                    Console.WriteLine("final " + final.Ip + " ok=" + final.Ok + " bytes=" + final.Bytes + " mibps=" + final.MibPerSecond + " " + final.Reason);
                }
                var system = System.Net.Dns.GetHostAddresses("download.limbuscompanycdn.org")
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.ToString()).ToArray();
                Console.WriteLine("dnsAfter=" + string.Join(",", system));
                var one = BatterBabel.Core.Probing.RouteProber
                    .MeasureDomainAsync("download.limbuscompanycdn.org", new[] { alive[0].Ip }, budget, null, CancellationToken.None)
                    .GetAwaiter().GetResult();
                foreach (var candidate in one)
                {
                    Console.WriteLine("probe " + candidate.Address + " verdict=" + candidate.Verdict + " median=" + candidate.MedianMs + " reason=" + candidate.Reason);
                }
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("cf diag failed: " + ex.GetType().Name + " " + ex.Message);
                Shutdown(1);
            }
            return;
        }

        // 提权子进程入口：UI 只负责发起，真正写文件在管理员进程里做（UAC 一次）。
        var applyPlan = Value(args, "--hosts-apply");
        if (applyPlan is not null)
        {
            Shutdown(HostsElevated.Apply(Path.GetFullPath(applyPlan)));
            return;
        }

        var restorePlan = Value(args, "--hosts-restore");
        if (restorePlan is not null)
        {
            Shutdown(HostsElevated.Restore(Path.GetFullPath(restorePlan)));
            return;
        }

        var stripPlan = Value(args, "--hosts-strip");
        if (stripPlan is not null)
        {
            Shutdown(HostsElevated.Strip(Path.GetFullPath(stripPlan)));
            return;
        }

        // S5 证据采集：hosts 写/还原的事务边界在临时副本上验证，不碰系统 hosts。
        var hostsCheck = Value(args, "--hosts-selftest");
        if (hostsCheck is not null)
        {
            Shutdown(HostsSelfCheck.Run(Path.GetFullPath(hostsCheck)));
            return;
        }

        // 0.10.3 语义对齐自检：status/boost/optimize+restore/probe/cf-apply/cf-restore/apply-route。
        var parityCheck = Value(args, "--parity-selftest");
        if (parityCheck is not null)
        {
            Shutdown(ParitySelfCheck.Run(Path.GetFullPath(parityCheck)));
            return;
        }

        // S8：登录恢复任务 / 一次性恢复入口。不建窗口、不弹窗，只做系统状态修复。
        if (Has(args, "--boot-restore"))
        {
            Shutdown(BootRestoreRunner.Run());
            return;
        }

        if (Has(args, "--install-recovery"))
        {
            var report = BootRestoreRunner.Install();
            Console.WriteLine(report);
            Shutdown(report.StartsWith("RECOVERY_INSTALL PASS", StringComparison.Ordinal) ? 0 : 1);
            return;
        }

        if (Has(args, "--remove-recovery"))
        {
            var task = BootRecovery.RemoveTask();
            Console.WriteLine($"RECOVERY_REMOVE {(task.Ok ? "PASS" : "FAIL")} {task.Message}");
            Shutdown(task.Ok ? 0 : 1);
            return;
        }

        if (Has(args, "--recovery-inspect"))
        {
            var task = BootRecovery.InspectTask();
            var desired = BootRecovery.ReadDesired();
            Console.WriteLine($"RECOVERY_INSPECT taskOk={task.Ok} {task.Message}");
            Console.WriteLine("desired=" + (desired is null ? "none" : JsonSerializer.Serialize(desired)));
            Shutdown(0);
            return;
        }

        // 诊断入口：让每个系统写动作都能单独跑、单独回滚，不用去点 UI。
        var qosApply = Value(args, "--qos-apply");
        if (qosApply is not null)
        {
            var result = QosPolicy.ApplyAsync(Path.GetFullPath(qosApply)).GetAwaiter().GetResult();
            Console.WriteLine(QosPolicyLine(result));
            Shutdown(result.Ok ? 0 : 1);
            return;
        }

        if (Has(args, "--qos-remove"))
        {
            var result = QosPolicy.RemoveAsync().GetAwaiter().GetResult();
            Console.WriteLine(QosPolicyLine(result));
            Shutdown(result.Ok ? 0 : 1);
            return;
        }

        if (Has(args, "--tune-apply") || Has(args, "--tune-restore"))
        {
            var apply = Has(args, "--tune-apply");
            var result = apply
                ? SystemTuning.ApplyAsync().GetAwaiter().GetResult()
                : SystemTuning.RestoreAsync(Value(args, "--snapshot")).GetAwaiter().GetResult();
            Console.WriteLine(TuningLine(result));
            var resultPath = Value(args, "--tune-result");
            if (!string.IsNullOrEmpty(resultPath))
            {
                var reply = new
                {
                    ok = result.Ok,
                    status = result.Status,
                    message = result.Message,
                    snapshotPath = result.SnapshotPath,
                    items = result.ItemList,
                    log = result.LogLines,
                    changes = result.Changes,
                };
                File.WriteAllText(resultPath, JsonSerializer.Serialize(reply, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            Shutdown(result.Ok ? 0 : 1);
            return;
        }

        // S10 诊断入口：托管块只碰标记之间，失败原因原样打印。
        if (Has(args, "--cs2-config-apply"))
        {
            var configPath = LiveRoutes.Cs2ConfigPath();
            if (configPath is null)
            {
                Console.WriteLine("CONFIG FAIL 没有找到 CS2 安装目录");
                Shutdown(2);
                return;
            }

            var result = BatterBabel.Core.GameConfig.ManagedConfigFile.Apply(configPath, LiveRoutes.Cs2ConfigLines);
            Console.WriteLine($"CONFIG {(result.Ok ? "PASS" : "FAIL")} changed={result.Changed} {result.Message} path={result.Path}");
            Shutdown(result.Ok ? 0 : 1);
            return;
        }

        if (Value(args, "--config-inspect") is { } inspectPath)
        {
            var info = BatterBabel.Core.GameConfig.ManagedConfigFile.Inspect(Path.GetFullPath(inspectPath));
            Console.WriteLine($"CONFIG_INSPECT ok={info.Ok} exists={info.Exists} wellFormed={info.WellFormed} lines={info.LineCount} encoding={info.Encoding} {info.Message}");
            Shutdown(info.Ok ? 0 : 1);
            return;
        }

        if (Value(args, "--config-restore") is { } restoreConfigPath)
        {
            var result = BatterBabel.Core.GameConfig.ManagedConfigFile.Restore(Path.GetFullPath(restoreConfigPath));
            Console.WriteLine($"CONFIG {(result.Ok ? "PASS" : "FAIL")} changed={result.Changed} {result.Message}");
            Shutdown(result.Ok ? 0 : 1);
            return;
        }

        if (Has(args, "--native"))
        {
            new MainWindow().Show();
            return;
        }

        var pageRoot = Value(args, "--public-dir") ?? PageAssets.FindPublicDir();
        if (pageRoot is null || !File.Exists(Path.Combine(pageRoot, "index.html")))
        {
            MessageBox.Show(
                "找不到页面资源：src/public/index.html。\n请在仓库内运行，或用 --public-dir 指定页面目录。",
                "Batter Babel",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        // WebView2 的文件夹映射要求绝对路径，命令行参数可能给相对路径。
        pageRoot = Path.GetFullPath(pageRoot);

        var evidenceDir = Path.GetFullPath(Value(args, "--evidence-dir")
            ?? Path.Combine(PageAssets.FindRepoRoot(pageRoot) ?? Environment.CurrentDirectory, "evidence", "dsh-p0"));

        new WebShellWindow(pageRoot, evidenceDir, Has(args, "--web-check"), Has(args, "--web-shot"), Has(args, "--web-live"), Has(args, "--apply"), Value(args, "--game") ?? "limbus", Has(args, "--web-i18n"), Value(args, "--lang") ?? "zh").Show();
    }

    private static string QosPolicyLine(QosResult result) =>
        $"QOS {(result.Ok ? "PASS" : "FAIL")} {result.Message} policy={result.PolicyName} dscp={result.Dscp ?? "-"} already={result.AlreadyPresent}";

    private static string TuningLine(TuningResult result) =>
        $"TUNE {(result.Ok ? "PASS" : "FAIL")} {result.Message} snapshot={result.SnapshotPath ?? "-"} reboot={result.RebootRequired} changed={result.Changes.Count(c => c.Changed)}";

    private static bool Has(string[] args, string flag) =>
        args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

/// <summary>
/// 登录恢复的一次性执行入口（计划任务 BatterBabel-Restore 的 target 是 --boot-restore）。
/// 只按 desired.json 描述的动作做系统修复，然后退出；不创建窗口。
/// </summary>
internal static class BootRestoreRunner
{
    public static int Run()
    {
        var plan = BootRecovery.ReadPlan();
        if (plan is null || !plan.HasWork)
        {
            Console.WriteLine("BOOT_RESTORE SKIP " + (plan?.Summary ?? "没有 desired.json"));
            return 0;
        }

        Console.WriteLine("BOOT_RESTORE " + plan.Summary);
        var outcome = BootRecovery.Execute(plan, new RecoveryHooks(
            entries => ApplyHosts(entries),
            (executable, dscp) => ApplyQos(executable, dscp),
            snapshot => RestoreTuning(snapshot)));

        foreach (var step in outcome.Steps)
        {
            Console.WriteLine($"  step {step.Name}: {(step.Ok ? "ok" : "FAIL")} {step.Message}");
        }

        Console.WriteLine($"BOOT_RESTORE {(outcome.Ok ? "PASS" : "FAIL")} {outcome.Message}");
        if (outcome.Ok && plan.HostsPending) { ClearHostsPending(); }
        return outcome.Ok ? 0 : 1;
    }

    public static string Install()
    {
        var executable = Environment.ProcessPath ?? "";
        var result = BootRecovery.EnsureTask(executable);
        return $"RECOVERY_INSTALL {(result.Ok ? "PASS" : "FAIL")} {result.Message} task={result.TaskName} exe={executable}";
    }

    private static RecoveryStepResult ApplyHosts(IReadOnlyList<HostsDesiredEntry> entries)
    {
        var changes = entries.Select(entry => new HostsChange(entry.Domain, entry.Ip)).ToArray();
        var result = HostsFileWriter.Apply(LiveRoutes.HostsPath, changes, HostsFileWriter.DefaultBackupDir);
        return new RecoveryStepResult(result.Ok, result.Message);
    }

    private static RecoveryStepResult ApplyQos(string executable, int dscp)
    {
        var result = QosPolicy.ApplyAsync(executable, dscp).GetAwaiter().GetResult();
        return new RecoveryStepResult(result.Ok, result.Message);
    }

    private static RecoveryStepResult RestoreTuning(string snapshot)
    {
        var result = SystemTuning.RestoreAsync(snapshot).GetAwaiter().GetResult();
        return new RecoveryStepResult(result.Ok, result.Message);
    }

    // hosts 恢复成功后清掉待恢复标记，否则每次登录都会重复写一遍同样的块。
    private static void ClearHostsPending()
    {
        var current = BootRecovery.ReadDesired();
        if (current is null) { return; }
        BootRecovery.WriteDesired(
            Environment.ProcessPath ?? "BatterBabel.exe",
            current with { HostsPending = false, HostsEntries = 0, Hosts = null, UpdatedAt = DateTimeOffset.Now });
    }
}

/// <summary>
/// hosts 文件层自检：只在临时副本上跑 Apply / 重复 Apply / Restore，验证块替换、其它行保留、
/// 备份存在与还原后的字节一致性。真实系统 hosts 由用户点「写入 hosts」时才会碰。
/// </summary>
internal static class HostsSelfCheck
{
    public static int Run(string reportPath)
    {
        var checks = new List<object>();
        var root = Path.Combine(Path.GetTempPath(), "bb-hosts-selftest-" + Guid.NewGuid().ToString("N"));
        var hosts = Path.Combine(root, "hosts");
        var backups = Path.Combine(root, "backup");
        Directory.CreateDirectory(root);
        var original = "# 测试用 hosts\r\n127.0.0.1 localhost\r\n10.0.0.9 keep.example\r\n";
        File.WriteAllText(hosts, original, new UTF8Encoding(false));
        var originalBytes = File.ReadAllBytes(hosts);

        void Check(string name, bool pass, string detail) => checks.Add(new { name, pass, detail });

        var first = HostsFileWriter.Apply(hosts, new[]
        {
            new HostsChange("download.limbuscompanycdn.org", "104.16.32.11"),
            new HostsChange("notice.limbuscompanyapi.com", "172.64.41.20"),
        }, backups);
        var text1 = File.ReadAllText(hosts);
        Check("apply-ok", first.Ok, first.Message);
        Check("apply-two-entries", text1.Contains("104.16.32.11 download.limbuscompanycdn.org") && text1.Contains("172.64.41.20 notice.limbuscompanyapi.com"), "两条映射都在");
        Check("apply-keeps-foreign-line", text1.Contains("10.0.0.9 keep.example") && text1.Contains("127.0.0.1 localhost"), "其它行未被改动");
        Check("apply-backup-exists", first.Backup.Length > 0 && File.Exists(first.Backup), first.Backup);

        var second = HostsFileWriter.Apply(hosts, new[]
        {
            new HostsChange("download.limbuscompanycdn.org", "104.16.99.99"),
        }, backups);
        var text2 = File.ReadAllText(hosts);
        Check("apply-replaces-block", second.Ok && text2.Split(HostsFileWriter.BeginMarker).Length == 2, "只有一个标记块");
        Check("apply-drops-old-ip", !text2.Contains("104.16.32.11") && text2.Contains("104.16.99.99"), "旧映射被替换");
        Check("apply-keeps-foreign-after-replace", text2.Contains("10.0.0.9 keep.example"), "再来一次也不动其它行");

        // cf-restore 只删本程序的标记块：块外（包括本程序写入之后用户又加的行）必须逐字节保留。
        // 这里在块尾追加一行第三方内容，还原后应当正好等于「原始字节 + 这一行」。
        var foreignAfterApply = "192.0.2.7 added-after-apply\r\n";
        File.AppendAllText(hosts, foreignAfterApply, new UTF8Encoding(false));
        var expectedAfterStrip = originalBytes.Concat(Encoding.UTF8.GetBytes(foreignAfterApply)).ToArray();

        var restore = HostsFileWriter.Strip(hosts, backups);
        var restored = File.ReadAllBytes(hosts);
        Check("restore-ok", restore.Ok, restore.Message);
        Check("strip-bytes-equal", restored.SequenceEqual(expectedAfterStrip), restored.Length + " B");
        Check("restore-backup-exists", File.Exists(restore.Backup), restore.Backup);

        var passed = checks.All(item => (bool)item.GetType().GetProperty("pass")!.GetValue(item)!);
        var payload = new
        {
            mode = "--hosts-selftest",
            passed,
            tempRoot = root,
            checks,
        };
        var folder = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine((passed ? "HOSTS_SELFTEST PASS" : "HOSTS_SELFTEST FAIL") + " " + reportPath);
        return passed ? 0 : 1;
    }
}

/// <summary>
/// 管理员子进程入口（受保护 helper）：读计划 → 写 hosts → 把结果写成 &lt;plan&gt;.result.json。
/// 计划文件必须是临时目录里的 <c>bb-hosts-*.json</c>，用完即删；这样即使有人手动以管理员身份
/// 运行本程序，也只能复现一次「写 hosts」这一条窄路径，拿不到任意命令执行能力。
/// </summary>
internal static class HostsElevated
{
    private const int MaxPlanEntries = 64;

    public static int Apply(string planPath)
    {
        if (!IsTrustedPlan(planPath, out var refusal))
        {
            Console.Error.WriteLine("HOSTS_PLAN_REFUSED " + refusal);
            return 2;
        }

        try
        {
            var plan = JsonSerializer.Deserialize<HostsPlan>(
                File.ReadAllText(planPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new HostsPlan();
            if (plan.Changes.Count > MaxPlanEntries)
            {
                Write(planPath, new HostsResult(false, $"计划条目过多（{plan.Changes.Count} > {MaxPlanEntries}）", "", "", 0));
                return 1;
            }

            var result = HostsFileWriter.Apply(LiveRoutes.HostsPath, plan.Changes, HostsFileWriter.DefaultBackupDir);
            Write(planPath, result);
            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Write(planPath, new HostsResult(false, "提权写入失败：" + ex.GetType().Name, "", "", 0));
            return 1;
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    public static int Restore(string planPath)
    {
        if (!IsTrustedPlan(planPath, out var refusal))
        {
            Console.Error.WriteLine("HOSTS_PLAN_REFUSED " + refusal);
            return 2;
        }

        try
        {
            var result = HostsFileWriter.Restore(LiveRoutes.HostsPath, HostsFileWriter.DefaultBackupDir);
            Write(planPath, result);
            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Write(planPath, new HostsResult(false, "提权还原失败：" + ex.GetType().Name, "", "", 0));
            return 1;
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    private static bool IsTrustedPlan(string planPath, out string refusal)
    {
        refusal = "";
        if (string.IsNullOrWhiteSpace(planPath)) { refusal = "计划路径为空"; return false; }

        var full = Path.GetFullPath(planPath);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            refusal = "计划文件不在临时目录";
            return false;
        }

        var name = Path.GetFileName(full);
        if (!name.StartsWith("bb-hosts-", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            refusal = "计划文件名不是 bb-hosts-*.json";
            return false;
        }

        return true;
    }

    private static void TryDelete(string planPath)
    {
        try { File.Delete(planPath); } catch (Exception) { /* 占用/权限问题不影响结果文件 */ }
    }

    private static void Write(string planPath, HostsResult result) =>
        File.WriteAllText(planPath + ".result.json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    private static void Write(string planPath, HostsStripResult result) =>
        File.WriteAllText(planPath + ".result.json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    /// <summary>提权后的「只删标记块」入口（发布版 cf-restore 语义）。</summary>
    public static int Strip(string planPath)
    {
        if (!IsTrustedPlan(planPath, out var refusal))
        {
            Console.Error.WriteLine("HOSTS_PLAN_REFUSED " + refusal);
            return 2;
        }

        try
        {
            var result = HostsFileWriter.Strip(LiveRoutes.HostsPath, HostsFileWriter.DefaultBackupDir);
            Write(planPath, result);
            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Write(planPath, new HostsStripResult(false, "提权还原失败：" + ex.GetType().Name, "", 0));
            return 1;
        }
        finally
        {
            TryDelete(planPath);
        }
    }

    internal sealed class HostsPlan
    {
        public List<HostsChange> Changes { get; set; } = new();
    }
}
