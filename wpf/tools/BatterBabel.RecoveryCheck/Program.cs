namespace BatterBabel.RecoveryCheck;

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BatterBabel.Core.Recovery;

/// <summary>
/// S8 恢复边界的自检 CLI：
///   --install &lt;exe&gt;   注册生产任务 BatterBabel-Restore
///   --remove            删除生产任务（幂等）
///   --inspect           查询生产任务并打印 XML
///   --selftest &lt;json&gt;  一次性任务 + desired.json 往返 + 坏 JSON 的完整自检
/// 不执行真实的 --boot-restore，也不改 hosts/QoS。
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "--install" when args.Length >= 2 => Install(args[1]),
                "--remove" => Remove(),
                "--inspect" => Inspect(),
                "--selftest" when args.Length >= 2 => SelfTest(args[1]),
                "--help" or "-h" => Help(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("RECOVERY_CHECK ERROR " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static int Install(string exe)
    {
        var result = BootRecovery.EnsureTask(exe);
        Console.WriteLine((result.Ok ? "OK " : "FAIL ") + result.Message);
        return result.Ok ? 0 : 1;
    }

    private static int Remove()
    {
        var result = BootRecovery.RemoveTask();
        Console.WriteLine((result.Ok ? "OK " : "FAIL ") + result.Message);
        return result.Ok ? 0 : 1;
    }

    private static int Inspect()
    {
        var result = BootRecovery.InspectTask();
        Console.WriteLine((result.Ok ? "OK " : "FAIL ") + result.Message);
        if (result.TaskXml is not null)
        {
            Console.WriteLine("----- task xml -----");
            Console.WriteLine(result.TaskXml);
        }

        return result.Ok ? 0 : 1;
    }

    private static int SelfTest(string reportPath)
    {
        var fullReport = Path.GetFullPath(reportPath);
        var steps = new List<StepResult>();
        var notes = new List<string>
        {
            "自检只创建/删除一次性计划任务（BatterBabel-SelfTest-<8位guid>），不执行真实的 --boot-restore。",
            "自检不写 hosts、不改 QoS/网络、不写生产任务 BatterBabel-Restore。",
            "desired.json 的往返与坏 JSON 分支都在临时目录完成。",
        };

        void Add(string step, bool ok, string detail)
        {
            steps.Add(new StepResult(step, ok, detail));
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {step}: {detail}");
        }

        if (!BootRecovery.IsAdministrator())
        {
            Add("elevation", false, "ELEVATION_REQUIRED：创建/删除计划任务需要管理员权限");
            var payload = new
            {
                mode = "--selftest",
                timestamp = DateTimeOffset.Now.ToString("O"),
                elevated = false,
                taskName = (string?)null,
                passed = false,
                exitCode = 2,
                steps,
                taskXml = (string?)null,
                desiredSample = (string?)null,
                notes,
            };
            TryWriteReport(fullReport, payload);
            Console.Error.WriteLine("ELEVATION_REQUIRED");
            return 2;
        }

        var taskName = "BatterBabel-SelfTest-" + Guid.NewGuid().ToString("N")[..8];
        var tempRoot = Path.Combine(Path.GetTempPath(), "bb-recovery-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        string? taskXml = null;
        string? desiredSample = null;
        var cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        try
        {
            Directory.CreateDirectory(tempRoot);

            if (!File.Exists(cmdExe))
            {
                Add("find-cmd", false, "找不到 cmd.exe：" + cmdExe);
            }
            else
            {
                Add("find-cmd", true, cmdExe);
            }

            // 1) 规定动作：一次性任务 -> cmd.exe /c exit 0。
            try
            {
                var install = BootRecovery.EnsureTask(cmdExe, taskName, "/c exit 0");
                Add("install", install.Ok, install.Message);

                var inspect = BootRecovery.InspectTask(taskName);
                taskXml = inspect.TaskXml;
                var hasRunLevel = taskXml?.Contains("<RunLevel>HighestAvailable</RunLevel>", StringComparison.Ordinal) == true;
                var hasArguments = taskXml?.Contains("/c exit 0", StringComparison.Ordinal) == true;
                var hasCommand = taskXml?.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) == true;
                Add(
                    "inspect",
                    inspect.Ok && hasRunLevel && hasArguments && hasCommand,
                    $"{inspect.Message} | RunLevel={hasRunLevel} args={hasArguments} cmd={hasCommand}");

                var remove = BootRecovery.RemoveTask(taskName);
                Add("remove", remove.Ok, remove.Message);

                var afterRemove = BootRecovery.InspectTask(taskName);
                Add("inspect-after-remove", !afterRemove.Ok, afterRemove.Message);
            }
            catch (Exception ex)
            {
                Add("install-inspect-remove-exception", false, ex.GetType().Name + ": " + ex.Message);
            }

            // 2) 生产参数：同一个一次性任务改指 cmd.exe --boot-restore（只注册/读回/删除，不执行）。
            try
            {
                var installArgs = BootRecovery.EnsureTask(cmdExe, taskName, "--boot-restore");
                Add("install-boot-restore-args", installArgs.Ok, installArgs.Message);

                var inspectArgs = BootRecovery.InspectTask(taskName);
                var xml = inspectArgs.TaskXml;
                var hasRunLevel = xml?.Contains("<RunLevel>HighestAvailable</RunLevel>", StringComparison.Ordinal) == true;
                var hasArguments = xml?.Contains("--boot-restore", StringComparison.Ordinal) == true;
                Add(
                    "inspect-boot-restore-args",
                    inspectArgs.Ok && hasRunLevel && hasArguments,
                    $"{inspectArgs.Message} | RunLevel={hasRunLevel} args={hasArguments}");
                taskXml ??= xml;

                var removeArgs = BootRecovery.RemoveTask(taskName);
                Add("remove-boot-restore-args", removeArgs.Ok, removeArgs.Message);

                var afterArgs = BootRecovery.InspectTask(taskName);
                Add("inspect-boot-restore-args-after-remove", !afterArgs.Ok, afterArgs.Message);
            }
            catch (Exception ex)
            {
                Add("boot-restore-args-exception", false, ex.GetType().Name + ": " + ex.Message);
            }

            // 3) desired.json 原子写 + 读回 + 计划摘要。
            RecoveryPlan? plan = null;
            try
            {
                var desiredPath = Path.Combine(tempRoot, "recovery", "desired.json");
                var tuningSnapshot = Path.Combine(tempRoot, "tuning-20261019-160000.json");
                var state = new RecoveryState(
                    HostsPending: true,
                    HostsEntries: 2,
                    QosEnabled: true,
                    QosExecutable: @"C:\games\demo\game.exe",
                    QosDscp: 46,
                    TuningSnapshot: tuningSnapshot,
                    UpdatedAt: DateTimeOffset.Now,
                    Hosts: new[]
                    {
                        new HostsDesiredEntry("cdn.example.com", "104.16.32.11"),
                        new HostsDesiredEntry("api.example.com", "172.64.41.20"),
                    });

                BootRecovery.WriteDesired(Environment.ProcessPath ?? cmdExe, state, desiredPath);
                desiredSample = File.Exists(desiredPath) ? File.ReadAllText(desiredPath, Encoding.UTF8) : null;

                var read = BootRecovery.ReadDesired(desiredPath);
                plan = BootRecovery.BuildPlan(read);
                var roundtrip =
                    read is not null &&
                    read.HostsPending &&
                    read.HostsEntries == 2 &&
                    read.Hosts?.Count == 2 &&
                    read.Hosts[0].Domain == "cdn.example.com" &&
                    read.Hosts[0].Ip == "104.16.32.11" &&
                    read.QosEnabled &&
                    string.Equals(read.QosExecutable, @"C:\games\demo\game.exe", StringComparison.OrdinalIgnoreCase) &&
                    read.QosDscp == 46 &&
                    string.Equals(read.TuningSnapshot, tuningSnapshot, StringComparison.OrdinalIgnoreCase) &&
                    read.UpdatedAt is not null &&
                    plan is { HasWork: true, HostsEntries: 2, RebuildQos: true, RollbackTuning: true };
                Add(
                    "desired-roundtrip",
                    roundtrip,
                    $"path={desiredPath} entries={read?.HostsEntries} qos={read?.QosEnabled}/{read?.QosDscp} plan={plan?.Summary ?? "null"}");

                // schema 允许 hosts/qos/tuning 任一段为 null：单独验一次“只有 hosts 待处理”的形态。
                var partialPath = Path.Combine(tempRoot, "recovery", "desired-null-sections.json");
                var partialState = new RecoveryState(
                    HostsPending: false,
                    HostsEntries: 0,
                    QosEnabled: false,
                    QosExecutable: null,
                    QosDscp: 0,
                    TuningSnapshot: null,
                    UpdatedAt: null);
                BootRecovery.WriteDesired(Environment.ProcessPath ?? cmdExe, partialState, partialPath);
                var partialText = File.ReadAllText(partialPath, Encoding.UTF8);
                var partialRead = BootRecovery.ReadDesired(partialPath);
                var partialPlan = BootRecovery.BuildPlan(partialRead);
                var nullSections =
                    partialText.Contains("\"hosts\": null", StringComparison.Ordinal) &&
                    partialText.Contains("\"qos\": null", StringComparison.Ordinal) &&
                    partialText.Contains("\"tuning\": null", StringComparison.Ordinal) &&
                    partialRead is { HostsPending: false, QosEnabled: false, TuningSnapshot: null } &&
                    partialPlan is { HasWork: false };
                Add(
                    "desired-null-sections",
                    nullSections,
                    $"null sections -> plan={(partialPlan?.Summary ?? "null")}");
            }
            catch (Exception ex)
            {
                Add("desired-roundtrip", false, ex.GetType().Name + ": " + ex.Message);
            }

            // 4) 执行边界：委托只做假动作，证明本切片不会自己改系统。
            try
            {
                if (plan is null)
                {
                    Add("plan-execute-hooks", false, "没有可用的 RecoveryPlan");
                }
                else
                {
                    var calls = new List<string>();
                    var outcome = BootRecovery.Execute(plan, new RecoveryHooks(
                        entries =>
                        {
                            calls.Add("hosts:" + entries.Count);
                            return new RecoveryStepResult(true, "fake hosts");
                        },
                        (exe, dscp) =>
                        {
                            calls.Add("qos:" + Path.GetFileName(exe) + ":" + dscp);
                            return new RecoveryStepResult(true, "fake qos");
                        },
                        snapshot =>
                        {
                            calls.Add("tuning:" + Path.GetFileName(snapshot));
                            return new RecoveryStepResult(true, "fake tuning");
                        }));

                    Add(
                        "plan-execute-hooks",
                        outcome.Ok && outcome.Steps.Count == 3 && calls.Count == 3,
                        outcome.Message + " | calls=" + string.Join(",", calls));
                }
            }
            catch (Exception ex)
            {
                Add("plan-execute-hooks", false, ex.GetType().Name + ": " + ex.Message);
            }

            // 5) 坏 JSON：必须返回 null，并在同目录留下可诊断日志（≤10 行）。
            try
            {
                var badDir = Path.Combine(tempRoot, "bad");
                Directory.CreateDirectory(badDir);
                var badPath = Path.Combine(badDir, "desired.json");
                File.WriteAllText(badPath, "{bad", new UTF8Encoding(false));
                var bad = BootRecovery.ReadDesired(badPath);
                var logPath = Path.Combine(badDir, "desired.error.log");
                var logLines = File.Exists(logPath) ? File.ReadAllLines(logPath, Encoding.UTF8) : Array.Empty<string>();
                Add(
                    "bad-json-null",
                    bad is null && logLines.Length is >= 1 and <= 10,
                    $"read={(bad is null ? "null" : "non-null")}, logLines={logLines.Length}, log={string.Join(" | ", logLines)}");
            }
            catch (Exception ex)
            {
                Add("bad-json-null", false, ex.GetType().Name + ": " + ex.Message);
            }

            // 6) 文件不存在：正常返回 null，不写日志、不影响登录流程。
            try
            {
                var missing = BootRecovery.ReadDesired(Path.Combine(tempRoot, "missing", "desired.json"));
                Add("missing-file-null", missing is null, "missing file 返回 " + (missing is null ? "null" : "非 null"));
            }
            catch (Exception ex)
            {
                Add("missing-file-null", false, ex.GetType().Name + ": " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            Add("selftest-exception", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            // 无论哪一步失败，都保证一次性任务被删除；再查一次确认。
            try
            {
                var cleanup = BootRecovery.RemoveTask(taskName);
                Add("cleanup-task", cleanup.Ok, cleanup.Message);
                var finalInspect = BootRecovery.InspectTask(taskName);
                Add("final-inspect", !finalInspect.Ok, finalInspect.Message);
            }
            catch (Exception ex)
            {
                Add("cleanup-task", false, ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // 临时目录清理失败不影响结论。
            }
        }

        var passed = steps.All(step => step.Ok);
        var exitCode = passed ? 0 : 1;
        var report = new
        {
            mode = "--selftest",
            timestamp = DateTimeOffset.Now.ToString("O"),
            elevated = BootRecovery.IsAdministrator(),
            taskName,
            passed,
            exitCode,
            steps,
            taskXml,
            desiredSample,
            notes,
        };

        if (!TryWriteReport(fullReport, report))
        {
            return 1;
        }

        Console.WriteLine((passed ? "RECOVERY_SELFTEST PASS " : "RECOVERY_SELFTEST FAIL ") + fullReport);
        return exitCode;
    }

    private static bool TryWriteReport(string path, object payload)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(payload, ReportJson), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("写报告失败：" + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static int Help()
    {
        Usage();
        return 0;
    }

    private static int Unknown(string arg)
    {
        Console.Error.WriteLine("未知参数：" + arg);
        Usage();
        return 1;
    }

    private static void Usage()
    {
        Console.WriteLine("BatterBabel.RecoveryCheck — S8 开机恢复边界自检");
        Console.WriteLine("  --install <exe>   注册 BatterBabel-Restore（登录时以最高权限运行 <exe> --boot-restore）");
        Console.WriteLine("  --inspect         查询 BatterBabel-Restore 并打印任务 XML");
        Console.WriteLine("  --remove          删除 BatterBabel-Restore（不存在也算成功）");
        Console.WriteLine("  --selftest <json> 一次性任务 + desired.json 往返 + 坏 JSON 自检（需要管理员）");
        Console.WriteLine("退出码：0 成功；1 失败；2 需要管理员（ELEVATION_REQUIRED）");
    }

    private sealed record StepResult(string Step, bool Ok, string Detail);
}
