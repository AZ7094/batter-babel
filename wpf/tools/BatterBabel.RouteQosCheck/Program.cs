using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BatterBabel.Core.Qos;

namespace BatterBabel.RouteQosCheck;

/// <summary>
/// S12「按目标 IP 加速线路」命令行自检。两个动作：
///   --inspect              读本机 BatterBabel-Route-* 策略（只读）
///   --selftest &lt;json&gt;    用 2 条文档地址（203.0.113.0/24）跑一遍
///                          建策略 → 读回 DSCP 46 → 幂等 → 校验分支 → remove → 0 残留，
///                          并把每条策略在 Get-NetQosPolicy 里的实际字段写进 JSON。
/// 退出码：0 通过 / 1 失败 / 2 需要管理员 / 64 参数错误。
///
/// 这里的 PowerShell 只用于「证据采集」（把 Get-NetQosPolicy 的原始字段照抄进报告）；
/// 产品路径的全部读写都在 BatterBabel.Core.Qos.RouteQosPolicy 里，调用方式与
/// QosPowerShell 相同：-EncodedCommand、参数走环境变量、20 s 超时、无窗口。
/// </summary>
internal static partial class Program
{
    private const int ExitOk = 0;
    private const int ExitFail = 1;
    private const int ExitElevation = 2;
    private const int ExitUsage = 64;
    private const int PowerShellTimeoutMs = 20_000;

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

        if (args.Length == 0)
        {
            Usage();
            return ExitUsage;
        }

        switch (args[0])
        {
            case "--inspect":
                return await InspectAsync().ConfigureAwait(false);

            case "--selftest":
                if (args.Length < 2) { Usage(); return ExitUsage; }
                return await SelfTestAsync(args[1]).ConfigureAwait(false);

            default:
                Usage();
                return ExitUsage;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  BatterBabel.RouteQosCheck --inspect");
        Console.WriteLine("  BatterBabel.RouteQosCheck --selftest <输出 JSON 路径>");
        Console.WriteLine("退出码：0 通过 / 1 失败 / 2 需要管理员 / 64 参数错误");
    }

    private static async Task<int> InspectAsync()
    {
        var result = await RouteQosPolicy.InspectAsync().ConfigureAwait(false);
        Console.WriteLine($"ok      = {result.Ok}");
        Console.WriteLine($"message = {result.Message}");
        foreach (var item in result.Items)
        {
            Console.WriteLine($"  {item.PolicyName} ip={item.Ip} dscp={item.Dscp ?? "(none)"} dst={item.DstPrefix ?? "(none)"} hasDst={item.HasDstPrefix} present={item.AlreadyPresent}");
        }

        return result.Ok ? ExitOk : ExitFail;
    }

    private sealed record CheckStep(string Id, bool Ok, string Detail);

    private sealed record ReportState
    {
        internal List<Dictionary<string, string>> RawAfterApply { get; set; } = new();
        internal List<Dictionary<string, string>> RawAfterRemove { get; set; } = new();
        internal int RouteResidual { get; set; } = -1;
        internal int AllBatterBabel { get; set; } = -1;
    }

    private static async Task<int> SelfTestAsync(string jsonPath)
    {
        var steps = new List<CheckStep>();
        var observations = new List<CheckStep>();
        var state = new ReportState();
        var entries = new[]
        {
            new RouteQosEntry("cm", "cloudflare.com", "203.0.113.10"),
            new RouteQosEntry("docs", "example.net", "203.0.113.27"),
        };
        var expectedNames = entries
            .Select(entry => RouteQosPolicy.PolicyPrefix + entry.Tag + "-" + entry.Ip)
            .ToArray();

        if (!IsElevated())
        {
            // 不自己弹 UAC：没有权限就把结论和原因写进 JSON，由宿主决定怎么提权。
            steps.Add(new CheckStep("elevation", false, "ELEVATION_REQUIRED: 当前进程不是管理员，QoS 策略写入不会被执行"));
            WriteJson(jsonPath, new
            {
                tool = "BatterBabel.RouteQosCheck",
                generatedAt = DateTimeOffset.Now.ToString("O"),
                policyPrefix = RouteQosPolicy.PolicyPrefix,
                dscp = RouteQosPolicy.DefaultDscp,
                store = "ActiveStore",
                elevated = false,
                elevationRequired = true,
                featureStatus = "ELEVATION_REQUIRED",
                code = "ELEVATION_REQUIRED",
                ok = false,
                assertions = steps.Select(ToJson).ToArray(),
                policiesAfterApply = Array.Empty<object>(),
                policiesAfterRemove = Array.Empty<object>(),
                residual = new { routePolicies = -1, allBatterBabelPolicies = -1 },
            });
            PrintSteps(steps);
            Console.WriteLine("ROUTE_QOS FAIL");
            return ExitElevation;
        }

        var othersBefore = await NonRouteNamesAsync().ConfigureAwait(false);
        RawProbe? probe = null;
        var platformOk = false;
        try
        {
            // 1. 干净起点：先清本前缀（只碰自己的规则），断言 0 条。
            var clean = await RouteQosPolicy.RemoveAsync().ConfigureAwait(false);
            var cleanInspect = await RouteQosPolicy.InspectAsync().ConfigureAwait(false);
            steps.Add(new CheckStep(
                "cleanup-before",
                clean.Ok && cleanInspect.Ok && cleanInspect.Items.Count == 0,
                $"{clean.Message}；复查：{cleanInspect.Message}"));

            // 2. 平台探针：用产品同款命令建 1 条 → 读回原始字段 → 删掉。
            //    这是唯一能区分「产品有 bug」与「平台丢了条件」的证据；同时保证探针不残留。
            const string probeIp = "203.0.113.254";
            var probeName = RouteQosPolicy.PolicyPrefix + "probe-" + probeIp;
            probe = await RawProbeAsync(probeName, probeIp, RouteQosPolicy.DefaultDscp).ConfigureAwait(false);
            platformOk = probe.Ok && probe.Created
                         && Field(probe.Fields, "IPDstPrefixMatchCondition") == probeIp + "/32";
            steps.Add(new CheckStep(
                "platform-probe",
                probe.Ok && probe.Created && probe.RemainingAfter == 0,
                probe.Ok
                    ? $"created={probe.Created} remainingAfter={probe.RemainingAfter}；" +
                      $"Name=[{Field(probe.Fields, "Name")}] dst=[{Field(probe.Fields, "IPDstPrefixMatchCondition")}] " +
                      $"dscp=[{Field(probe.Fields, "DSCPAction")}] profile=[{Field(probe.Fields, "NetworkProfile")}]"
                    : probe.Error));
            if (probe.Ok && probe.Created)
            {
                // 平台事实只作 observation，不参与 PASS 判定：它坏掉时产品应当 fail-closed，
                // 而 PASS 的含义就是「契约成立」，不是「IP 定向 DSCP 可用」。
                observations.Add(new CheckStep(
                    "platform-dst-prefix",
                    platformOk,
                    platformOk
                        ? "平台保存并读回了目标 IP 条件"
                        : "PLATFORM_DEFECT：New-NetQosPolicy 接受 -IPDstPrefixMatchCondition 但读回为空，策略没有任何目标条件（会匹配全部流量）"));
            }

            // 3. 两条文档地址 apply：平台坏时必须 fail-closed（Ok=false / Applied=0 / 全部回滚）。
            var apply = await RouteQosPolicy.ApplyAsync(entries).ConfigureAwait(false);
            var namesOk = apply.Items.Count == entries.Length
                          && apply.Items.Select(item => item.PolicyName).SequenceEqual(expectedNames, StringComparer.Ordinal);
            if (!platformOk)
            {
                steps.Add(new CheckStep(
                    "apply-two-fail-closed",
                    !apply.Ok && apply.Applied == 0 && apply.Skipped == 0
                    && apply.Items.Count == 2
                    && apply.Items.All(item => !item.Ok && !item.HasDstPrefix)
                    && namesOk,
                    $"ok={apply.Ok} applied={apply.Applied} skipped={apply.Skipped} removed={apply.Removed}；" +
                    string.Join("；", apply.Items.Select(item => item.Message))));
            }
            else
            {
                steps.Add(new CheckStep(
                    "apply-two",
                    apply.Ok && apply.Applied == 2 && apply.Skipped == 0 && namesOk,
                    $"ok={apply.Ok} applied={apply.Applied} skipped={apply.Skipped}；" +
                    string.Join("；", apply.Items.Select(item => item.Message))));
            }

            var rawAfterApply = await RawDumpAsync(RouteQosPolicy.PolicyPrefix + "*").ConfigureAwait(false);
            state.RawAfterApply = rawAfterApply.Rules;
            if (!platformOk)
            {
                steps.Add(new CheckStep(
                    "no-catch-all-after-apply",
                    rawAfterApply.Ok && rawAfterApply.Rules.Count == 0,
                    rawAfterApply.Ok
                        ? $"回滚后 {RouteQosPolicy.PolicyPrefix}* 策略数 = {rawAfterApply.Rules.Count}（必须为 0，不能留下 catch-all）"
                        : rawAfterApply.Error));
                var inspectAfter = await RouteQosPolicy.InspectAsync().ConfigureAwait(false);
                steps.Add(new CheckStep("inspect-after-rollback", inspectAfter.Ok && inspectAfter.Items.Count == 0, inspectAfter.Message));
            }
            else
            {
                var rawOk = rawAfterApply.Ok
                            && rawAfterApply.Rules.Count == 2
                            && entries.All(entry => rawAfterApply.Rules.Any(rule =>
                                Field(rule, "Name").Equals(
                                    RouteQosPolicy.PolicyPrefix + entry.Tag + "-" + entry.Ip,
                                    StringComparison.OrdinalIgnoreCase)
                                && Field(rule, "IPDstPrefixMatchCondition") == entry.Ip + "/32"
                                && Field(rule, "DSCPAction") == RouteQosPolicy.DefaultDscp.ToString()));
                steps.Add(new CheckStep(
                    "raw-readback",
                    rawOk,
                    rawAfterApply.Ok
                        ? $"Get-NetQosPolicy 读回 {rawAfterApply.Rules.Count} 条：" +
                          string.Join("；", rawAfterApply.Rules.Select(rule =>
                              $"{Field(rule, "Name")} ip=[{Field(rule, "IPDstPrefixMatchCondition")}] " +
                              $"dscp=[{Field(rule, "DSCPAction")}] app=[{Field(rule, "AppPathNameMatchCondition")}]"))
                        : rawAfterApply.Error));

                var inspect = await RouteQosPolicy.InspectAsync().ConfigureAwait(false);
                steps.Add(new CheckStep(
                    "core-readback",
                    inspect.Ok && inspect.Items.Count == 2
                    && entries.All(entry => inspect.Items.Any(item =>
                        item.Ip == entry.Ip && item.Dscp == RouteQosPolicy.DefaultDscp.ToString() && item.HasDstPrefix)),
                    inspect.Message));

                // 完整模式：同一批再 apply 必须幂等（Applied=0 / Skipped=2 / AlreadyPresent）。
                var again = await RouteQosPolicy.ApplyAsync(entries).ConfigureAwait(false);
                steps.Add(new CheckStep(
                    "apply-idempotent",
                    again.Ok && again.Applied == 0 && again.Skipped == 2 && again.Items.All(item => item.AlreadyPresent),
                    $"ok={again.Ok} applied={again.Applied} skipped={again.Skipped}；" +
                    string.Join("；", again.Items.Select(item => item.Message))));
            }

            // 4. fail-closed 分支的幂等：第二次 apply 仍必须 Ok=false / 0 条 / 0 残留。
            if (!platformOk)
            {
                var again = await RouteQosPolicy.ApplyAsync(entries).ConfigureAwait(false);
                var rawAgain = await RawDumpAsync(RouteQosPolicy.PolicyPrefix + "*").ConfigureAwait(false);
                steps.Add(new CheckStep(
                    "apply-idempotent-fail-closed",
                    !again.Ok && again.Applied == 0 && again.Items.Count == 2 && again.Items.All(item => !item.Ok)
                    && rawAgain.Ok && rawAgain.Rules.Count == 0,
                    $"ok={again.Ok} applied={again.Applied} skipped={again.Skipped}；策略数={rawAgain.Rules.Count}；" +
                    string.Join("；", again.Items.Select(item => item.Message))));
            }

            // 5. 校验分支：非法 tag / 前导零 / 越界段 / 空 tag / 超长 tag 都只让该条失败，不写任何策略。
            var invalid = new[]
            {
                new RouteQosEntry("Bad_Tag", "x.example", "198.51.100.7"),
                new RouteQosEntry("Lead", "x.example", "010.1.1.1"),
                new RouteQosEntry("Range", "x.example", "203.0.113.999"),
                new RouteQosEntry(string.Empty, "x.example", "203.0.113.5"),
                new RouteQosEntry(new string('a', 28), "x.example", "198.51.100.8"),
            };
            var invalidResult = await RouteQosPolicy.ApplyAsync(invalid).ConfigureAwait(false);
            var rawAfterInvalid = await RawDumpAsync(RouteQosPolicy.PolicyPrefix + "*").ConfigureAwait(false);
            steps.Add(new CheckStep(
                "validation",
                !invalidResult.Ok && invalidResult.Applied == 0 && invalidResult.Items.Count == invalid.Length
                && invalidResult.Items.All(item => !item.Ok && item.PolicyName.Length == 0)
                && rawAfterInvalid.Ok && rawAfterInvalid.Rules.Count == 0,
                $"ok={invalidResult.Ok} applied={invalidResult.Applied}；策略数={rawAfterInvalid.Rules.Count}；" +
                string.Join("；", invalidResult.Items.Select(item => item.Message))));

            // 6. 混批：非法条目不连坐（平台坏时合法条目也因回滚而失败，但名字/计数仍要正确）。
            var mixed = await RouteQosPolicy.ApplyAsync(new[]
            {
                new RouteQosEntry("mix", "x.example", "203.0.113.99"),
                new RouteQosEntry("no", "x.example", "300.1.1.1"),
            }).ConfigureAwait(false);
            var rawAfterMixed = await RawDumpAsync(RouteQosPolicy.PolicyPrefix + "*").ConfigureAwait(false);
            var mixedExpectedApplied = platformOk ? 1 : 0;
            steps.Add(new CheckStep(
                "mixed-batch",
                !mixed.Ok && mixed.Applied == mixedExpectedApplied
                && mixed.Items.Count == 2 && mixed.Items[0].Ok == platformOk && !mixed.Items[1].Ok
                && rawAfterMixed.Ok && rawAfterMixed.Rules.Count == mixedExpectedApplied,
                $"ok={mixed.Ok} applied={mixed.Applied}；策略数={rawAfterMixed.Rules.Count}；" +
                string.Join("；", mixed.Items.Select(item => $"{(item.Ok ? "ok" : "fail")}:{item.Message}"))));

            // 7. 条数上限：65 条整批拒绝，一条都不写。
            var many = Enumerable.Range(1, RouteQosPolicy.MaxEntries + 1)
                .Select(index => new RouteQosEntry("t" + index, "x.example", $"198.51.100.{index}"))
                .ToArray();
            var limit = await RouteQosPolicy.ApplyAsync(many).ConfigureAwait(false);
            var rawAfterLimit = await RawDumpAsync(RouteQosPolicy.PolicyPrefix + "*").ConfigureAwait(false);
            steps.Add(new CheckStep(
                "entry-limit",
                !limit.Ok && limit.Applied == 0 && limit.Items.Count == 0
                && limit.Message.Contains(RouteQosPolicy.MaxEntries.ToString(), StringComparison.Ordinal)
                && rawAfterLimit.Ok && rawAfterLimit.Rules.Count == mixedExpectedApplied,
                limit.Message));

            // 8. remove：删除条数必须等于删除前读到的条数，且消息确认无残留。
            var beforeRemove = await RouteQosPolicy.InspectAsync().ConfigureAwait(false);
            var removed = await RouteQosPolicy.RemoveAsync().ConfigureAwait(false);
            steps.Add(new CheckStep(
                "remove",
                removed.Ok && removed.Removed == beforeRemove.Items.Count
                && removed.Message.Contains("已无残留", StringComparison.Ordinal),
                $"{removed.Message}；删除前 Inspect {beforeRemove.Items.Count} 条"));
        }
        catch (Exception ex)
        {
            steps.Add(new CheckStep("harness", false, $"{ex.GetType().Name}: {Short(ex.Message)}"));
        }
        finally
        {
            // 无论上面走到哪一步，结束前都必须把本前缀清到 0 条，并记录删除后的实际字段（应为空）。
            try
            {
                var finalRemove = await RouteQosPolicy.RemoveAsync().ConfigureAwait(false);
                var finalRaw = await RawDumpAsync(RouteQosPolicy.PolicyPrefix + "*").ConfigureAwait(false);
                var finalInspect = await RouteQosPolicy.InspectAsync().ConfigureAwait(false);
                state.RawAfterRemove = finalRaw.Rules;
                state.RouteResidual = finalRaw.Rules.Count;
                steps.Add(new CheckStep(
                    "cleanup-final",
                    finalRemove.Ok && finalRaw.Ok && finalRaw.Rules.Count == 0
                    && finalInspect.Ok && finalInspect.Items.Count == 0,
                    $"{finalRemove.Message}；Get-NetQosPolicy 残留 {finalRaw.Rules.Count} 条；Inspect 残留 {finalInspect.Items.Count} 条"));
            }
            catch (Exception ex)
            {
                steps.Add(new CheckStep("cleanup-final", false, $"清理失败：{ex.GetType().Name}: {Short(ex.Message)}"));
            }
        }

        // 9. 别人的 BatterBabel* 策略必须原样保留（例如 S7S9 留下的 batterbabel-game-1b5793e3）。
        try
        {
            var othersAfter = await NonRouteNamesAsync().ConfigureAwait(false);
            var all = await RawDumpAsync("BatterBabel*").ConfigureAwait(false);
            state.AllBatterBabel = all.Ok ? all.Rules.Count : -1;
            steps.Add(new CheckStep(
                "others-preserved",
                othersBefore.SequenceEqual(othersAfter, StringComparer.Ordinal),
                $"其它 BatterBabel* 策略 前=[{string.Join(",", othersBefore)}] 后=[{string.Join(",", othersAfter)}]；" +
                $"全部 BatterBabel* = {state.AllBatterBabel}，其中 {RouteQosPolicy.PolicyPrefix}* = {state.RouteResidual}"));
        }
        catch (Exception ex)
        {
            steps.Add(new CheckStep("others-preserved", false, $"{ex.GetType().Name}: {Short(ex.Message)}"));
        }

        var allOk = steps.Count > 0 && steps.All(step => step.Ok);
        WriteJson(jsonPath, new
        {
            tool = "BatterBabel.RouteQosCheck",
            generatedAt = DateTimeOffset.Now.ToString("O"),
            policyPrefix = RouteQosPolicy.PolicyPrefix,
            dscp = RouteQosPolicy.DefaultDscp,
            store = "ActiveStore",
            elevated = true,
            elevationRequired = false,
            featureStatus = platformOk ? "AVAILABLE" : "BLOCKED_ON_PLATFORM",
            verdictMeaning = "ROUTE_QOS PASS 只表示按 fail-closed 契约工作（创建后读回不到目标 IP 条件就立即回滚并报错），不代表 IP 定向 DSCP 可用",
            ok = allOk,
            entries = entries.Select(entry => new { tag = entry.Tag, domain = entry.Domain, ip = entry.Ip }).ToArray(),
            expectedPolicyNames = expectedNames,
            assertions = steps.Select(ToJson).ToArray(),
            observations = observations.Select(ToJson).ToArray(),
            platformEvidence = probe is null
                ? null
                : new
                {
                    testedIp = "203.0.113.254",
                    created = probe.Created,
                    remainingAfter = probe.RemainingAfter,
                    fields = probe.Fields,
                    hasDstPrefix = probe.Ok && Field(probe.Fields, "IPDstPrefixMatchCondition").Length > 0,
                    error = probe.Error,
                },
            policiesAfterApply = state.RawAfterApply,
            policiesAfterRemove = state.RawAfterRemove,
            residual = new
            {
                routePolicies = state.RouteResidual,
                allBatterBabelPolicies = state.AllBatterBabel,
                otherBatterBabelPolicies = othersBefore,
            },
            unmetAcceptance = platformOk
                ? new[]
                {
                    "真实目标 IP 未验证（本轮只用 203.0.113.0/24 文档地址）",
                    "DSCP 实际生效未验证（需真实路由 + 抓包）",
                    "非管理员路径未在提权 shell 内验证",
                }
                : new[]
                {
                    "apply-route 的 IP 定向 DSCP 在当前平台不可用：New-NetQosPolicy 丢弃 -IPDstPrefixMatchCondition，读回为空（见 platformEvidence.fields）",
                    "真实目标 IP 未验证（本轮只用 203.0.113.0/24 文档地址）",
                    "DSCP 实际生效未验证（需真实路由 + 抓包）",
                    "非管理员路径未在本轮验证",
                },
        });

        PrintSteps(steps);
        PrintSteps(observations, "OBS", "OBS");
        Console.WriteLine(allOk ? "ROUTE_QOS PASS" : "ROUTE_QOS FAIL");
        return allOk ? ExitOk : ExitFail;
    }

    private static object ToJson(CheckStep step) => new { id = step.Id, ok = step.Ok, detail = step.Detail };

    private static string Field(Dictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) ? value : string.Empty;

    private static string Short(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    private static void PrintSteps(IEnumerable<CheckStep> steps, string passLabel = "PASS", string failLabel = "FAIL")
    {
        foreach (var step in steps)
        {
            Console.WriteLine($"[{(step.Ok ? passLabel : failLabel)}] {step.Id}: {step.Detail}");
        }
    }

#pragma warning disable CA1416 // 只在本机 Windows 上跑；没有 cross-platform 目标。
    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
#pragma warning restore CA1416

    // ---- 证据采集：Get-NetQosPolicy 的原始字段 ----------------------------------------------

    private sealed record RawDump(bool Ok, List<Dictionary<string, string>> Rules, string Error);

    /// <summary>
    /// 把本机 ActiveStore 里匹配 pattern 的策略逐条导出：Name / IPDstPrefixMatchCondition /
    /// DSCPAction / AppPathNameMatchCondition / NetworkProfile / Precedence / Owner … 全部原样照抄。
    /// 只用于报告；产品读写路径不经过这里。
    /// </summary>
    private static async Task<RawDump> RawDumpAsync(string pattern)
    {
        const string script = """
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$pattern = $env:BB_CHECK_PATTERN
$rules = New-Object System.Collections.Generic.List[object]
$errorText = ''
try {
    foreach ($policy in @(Get-NetQosPolicy -PolicyStore 'ActiveStore' -Name $pattern -ErrorAction Stop)) {
        $fields = [ordered]@{}
        foreach ($property in $policy.PSObject.Properties) {
            if ($property.Name -like 'Cim*' -or $property.Name -like 'PS*') { continue }
            $fields[$property.Name] = "$($property.Value)"
        }
        $rules.Add([pscustomobject]$fields)
    }
} catch {
    $errorText = "$($_.FullyQualifiedErrorId): $($_.Exception.Message)"
}
[pscustomobject]@{ ok = ($errorText -eq ''); rules = $rules.ToArray(); error = $errorText } | ConvertTo-Json -Depth 6 -Compress
""";

        var psi = new ProcessStartInfo
        {
            FileName = ResolveShell(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        psi.Environment["BB_CHECK_PATTERN"] = pattern;

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
            {
                return new RawDump(false, new List<Dictionary<string, string>>(), "无法启动 powershell.exe");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(PowerShellTimeoutMs);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new RawDump(false, new List<Dictionary<string, string>>(), $"PowerShell 调用超时（{PowerShellTimeoutMs / 1000} s）");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
                return new RawDump(false, new List<Dictionary<string, string>>(),
                    $"PowerShell 退出码 {process.ExitCode}：{Short(detail)}");
            }

            return ParseRawDump(stdout);
        }
        catch (Exception ex)
        {
            try { if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); } } catch { }
            return new RawDump(false, new List<Dictionary<string, string>>(), $"{ex.GetType().Name}: {Short(ex.Message)}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static RawDump ParseRawDump(string stdout)
    {
        var text = stdout.Trim();
        if (!text.StartsWith('{'))
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return new RawDump(false, new List<Dictionary<string, string>>(), "PowerShell 没有返回 JSON");
            }

            text = text[start..(end + 1)];
        }

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
        var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
            ? errorElement.GetString() ?? string.Empty
            : string.Empty;

        var rules = new List<Dictionary<string, string>>();
        if (root.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rulesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { continue; }
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    fields[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.ToString();
                }

                rules.Add(fields);
            }
        }

        return new RawDump(ok, rules, error);
    }

    private static string ResolveShell()
    {
        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrEmpty(system))
            {
                var candidate = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (File.Exists(candidate)) { return candidate; }
            }
        }
        catch
        {
            // 取不到系统目录就退回 PATH 查找。
        }

        return "powershell.exe";
    }

    private static void WriteJson(string path, object document)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(full, json + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine($"JSON 已写入：{full}");
    }
}
