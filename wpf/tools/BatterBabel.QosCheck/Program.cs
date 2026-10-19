using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Text.Json;
using BatterBabel.Core.Qos;

namespace BatterBabel.QosCheck;

/// <summary>
/// S7「加速游戏」命令行自检。四个动作：
///   --inspect              读本机 BatterBabel-Game-* 策略（只读）
///   --apply &lt;exe&gt;        给一个 exe 打 DSCP（幂等）
///   --remove               清理本前缀全部策略
///   --selftest &lt;jsonPath&gt; 用 notepad.exe 跑一遍完整生命周期，逐步写 {step,ok,detail}
/// 退出码：0 通过 / 1 失败 / 2 需要管理员 / 64 参数错误。
/// </summary>
internal static class Program
{
    private const string Notepad = @"C:\Windows\System32\notepad.exe";
    private const int ExitOk = 0;
    private const int ExitFail = 1;
    private const int ExitElevation = 2;
    private const int ExitUsage = 64;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return ExitUsage;
        }

        switch (args[0])
        {
            case "--inspect":
                return Report(await QosPolicy.InspectAsync().ConfigureAwait(false));

            case "--apply":
                if (args.Length < 2) { Usage(); return ExitUsage; }
                var dscp = args.Length > 2 ? int.Parse(args[2]) : QosPolicy.DefaultDscp;
                return Report(await QosPolicy.ApplyAsync(args[1], dscp).ConfigureAwait(false));

            case "--remove":
                return Report(await QosPolicy.RemoveAsync().ConfigureAwait(false));

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
        Console.WriteLine("  BatterBabel.QosCheck --inspect");
        Console.WriteLine("  BatterBabel.QosCheck --apply <游戏 exe 路径> [dscp]");
        Console.WriteLine("  BatterBabel.QosCheck --remove");
        Console.WriteLine("  BatterBabel.QosCheck --selftest <输出 JSON 路径>");
        Console.WriteLine("退出码：0 通过 / 1 失败 / 2 需要管理员 / 64 参数错误");
    }

    /// <summary>单动作报告：把 OK/消息/策略名都打出来，退出码按 Ok 与提权原因区分。</summary>
    private static int Report(QosResult result)
    {
        Console.WriteLine($"ok         = {result.Ok}");
        Console.WriteLine($"message    = {result.Message}");
        Console.WriteLine($"policy     = {result.PolicyName}");
        Console.WriteLine($"executable = {result.Executable ?? "(none)"}");
        Console.WriteLine($"dscp       = {result.Dscp ?? "(none)"}");
        Console.WriteLine($"present    = {result.AlreadyPresent}");

        if (result.Ok) { return ExitOk; }
        return IsElevation(result.Message) ? ExitElevation : ExitFail;
    }

    private static bool IsElevation(string message)
        => message.Contains("需要管理员权限", StringComparison.Ordinal);

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

    private sealed record CheckStep(string Step, bool Ok, string Detail);

    private static async Task<int> SelfTestAsync(string jsonPath)
    {
        var steps = new List<CheckStep>();

        if (!IsElevated())
        {
            // 不自己弹 UAC：没有权限就把结论和原因写进 JSON，由宿主决定怎么提权。
            steps.Add(new CheckStep("elevation", false, "ELEVATION_REQUIRED: 当前进程不是管理员，QoS 策略写入不会被执行"));
            var notElevated = new
            {
                tool = "BatterBabel.QosCheck",
                generatedAt = DateTimeOffset.Now.ToString("O"),
                target = Notepad,
                policyPrefix = QosPolicy.PolicyPrefix,
                dscp = QosPolicy.DefaultDscp,
                elevated = false,
                elevationRequired = true,
                code = "ELEVATION_REQUIRED",
                ok = false,
                steps = steps.Select(ToJson).ToArray(),
            };
            WriteJson(jsonPath, notElevated);
            PrintSteps(steps);
            Console.WriteLine("结果：需要管理员权限（未做任何写入）");
            return ExitElevation;
        }

        try
        {
            // 1. before：只读，记录当前前缀策略。
            var before = await QosPolicy.InspectAsync().ConfigureAwait(false);
            steps.Add(new CheckStep("inspect-before", before.Ok, before.Message));

            // 2. apply：断言 Ok 且策略名带本产品前缀。
            var apply = await QosPolicy.ApplyAsync(Notepad).ConfigureAwait(false);
            var applyOk = apply.Ok
                          && apply.PolicyName.StartsWith(QosPolicy.PolicyPrefix, StringComparison.OrdinalIgnoreCase)
                          && !string.IsNullOrEmpty(apply.PolicyName);
            steps.Add(new CheckStep("apply", applyOk, Describe("apply", apply)));

            // 3. after-apply：断言能找到 notepad 且 DSCP=46。
            var afterApply = await QosPolicy.InspectAsync().ConfigureAwait(false);
            var found = afterApply.Ok
                        && afterApply.AlreadyPresent
                        && (afterApply.Executable?.Contains("notepad", StringComparison.OrdinalIgnoreCase) ?? false)
                        && afterApply.Dscp == QosPolicy.DefaultDscp.ToString();
            steps.Add(new CheckStep("inspect-after-apply", found, Describe("inspect-after-apply", afterApply)));

            // 4. 幂等：同一 exe 再 apply 一次必须 AlreadyPresent，且不重复建。
            var again = await QosPolicy.ApplyAsync(Notepad).ConfigureAwait(false);
            var idempotent = again.Ok && again.AlreadyPresent;
            steps.Add(new CheckStep("apply-idempotent", idempotent, Describe("apply-idempotent", again)));

            // 5. remove：断言 Ok。
            var remove = await QosPolicy.RemoveAsync().ConfigureAwait(false);
            steps.Add(new CheckStep("remove", remove.Ok, remove.Message));

            // 6. after-remove：断言无本前缀策略。
            var afterRemove = await QosPolicy.InspectAsync().ConfigureAwait(false);
            var clean = afterRemove.Ok && !afterRemove.AlreadyPresent;
            steps.Add(new CheckStep("inspect-after-remove", clean, afterRemove.Message));
        }
        catch (Exception ex)
        {
            steps.Add(new CheckStep("harness", false, $"{ex.GetType().Name}: {Short(ex.Message)}"));
        }

        // 无论上面走到哪一步，结束前都必须把本前缀策略清到 0 条。
        QosResult? cleanup = null;
        QosResult? post = null;
        string cleanupDetail;
        try
        {
            cleanup = await QosPolicy.RemoveAsync().ConfigureAwait(false);
            post = await QosPolicy.InspectAsync().ConfigureAwait(false);
            cleanupDetail = $"{cleanup.Message}；复查：{post.Message}";
        }
        catch (Exception ex)
        {
            cleanupDetail = $"清理失败：{ex.GetType().Name}: {Short(ex.Message)}";
        }

        var cleanupOk = cleanup is { Ok: true } && post is { Ok: true, AlreadyPresent: false };
        steps.Add(new CheckStep("cleanup", cleanupOk, cleanupDetail));

        var allOk = steps.All(step => step.Ok);
        var document = new
        {
            tool = "BatterBabel.QosCheck",
            generatedAt = DateTimeOffset.Now.ToString("O"),
            target = Notepad,
            policyPrefix = QosPolicy.PolicyPrefix,
            dscp = QosPolicy.DefaultDscp,
            elevated = true,
            elevationRequired = false,
            ok = allOk,
            remainingRules = post is { Ok: true, AlreadyPresent: false } ? 0 : -1,
            steps = steps.Select(ToJson).ToArray(),
        };
        WriteJson(jsonPath, document);
        PrintSteps(steps);
        Console.WriteLine(allOk ? "结果：全部通过" : "结果：有失败项（见上）");
        return allOk ? ExitOk : ExitFail;
    }

    private static object ToJson(CheckStep step) => new { step = step.Step, ok = step.Ok, detail = step.Detail };

    private static string Describe(string label, QosResult result)
        => $"{label}: ok={result.Ok} present={result.AlreadyPresent} policy={result.PolicyName} " +
           $"exe={result.Executable ?? "(none)"} dscp={result.Dscp ?? "(none)"} | {result.Message}";

    private static string Short(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    private static void PrintSteps(IEnumerable<CheckStep> steps)
    {
        foreach (var step in steps)
        {
            Console.WriteLine($"[{(step.Ok ? "PASS" : "FAIL")}] {step.Step}: {step.Detail}");
        }
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
        File.WriteAllText(full, json + Environment.NewLine, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"JSON 已写入：{full}");
    }
}
