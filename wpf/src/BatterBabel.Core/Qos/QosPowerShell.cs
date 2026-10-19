namespace BatterBabel.Core.Qos;

using System.Diagnostics;
using System.Text;

/// <summary>一次 PowerShell 边界调用的结果。失败只保留类型名 + 短消息，绝不把整段脚本带进 UI。</summary>
internal sealed record QosProcessOutcome(int ExitCode, string StdOut, string StdErr, bool TimedOut, string? Failure)
{
    public bool Ok => Failure is null && !TimedOut && ExitCode == 0;

    internal static string ShortError(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}".Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }
}

/// <summary>
/// QoS 的 PowerShell 边界：一切 Get-/New-/Remove-NetQosPolicy 都只在这里出现一次。
///
/// 调用方式固定为 <c>powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand</c>：
/// 本机组策略拦截 <c>-File</c>（scripts 目录的脚本也因此全部走 EncodedCommand）。
/// 参数（策略名 / 路径 / DSCP / 存储名）一律通过进程环境变量传入，不拼进脚本文本，
/// 因此 exe 路径里的引号、空格、$、反引号都不可能变成 PowerShell 代码。
///
/// 输出约定：每个脚本最后一句把结果 <c>ConvertTo-Json -Compress</c> 到 stdout，中间不写别的；
/// stderr 只用于诊断。超时 20 s，超时即杀整棵进程树，不留后台 powershell。
/// </summary>
internal static class QosPowerShell
{
    internal const int TimeoutMs = 20_000;

    private static readonly string ShellPath = ResolveShell();

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
            // 取不到系统目录就退回 PATH 查找；下面的 Start 会给出明确的失败消息。
        }

        return "powershell.exe";
    }

    internal static async Task<QosProcessOutcome> RunAsync(
        string script,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ShellPath,
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
        foreach (var pair in environment) { psi.Environment[pair.Key] = pair.Value; }

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
            {
                return new QosProcessOutcome(-1, string.Empty, string.Empty, false, "无法启动 powershell.exe");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeoutMs);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new QosProcessOutcome(-1, string.Empty, string.Empty, true, $"PowerShell 调用超时（{TimeoutMs / 1000} s）");
            }

            var stdout = await SafeText(stdoutTask).ConfigureAwait(false);
            var stderr = await SafeText(stderrTask).ConfigureAwait(false);
            return new QosProcessOutcome(process.ExitCode, stdout, stderr, false, null);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new QosProcessOutcome(-1, string.Empty, string.Empty, false, QosProcessOutcome.ShortError(ex));
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); }
        }
        catch
        {
            // 进程可能已经自己退出；这里没有可做的补救，失败信息由调用方报告。
        }
    }

    private static async Task<string> SafeText(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return string.Empty; }
    }
}

/// <summary>
/// 三段脚本：查询、创建（Local 优先，失败回落 ActiveStore）、删除。
/// 参数全部从环境变量读取，脚本内不出现调用方给的任何字面量。
/// </summary>
internal static class QosScripts
{
    private const string Preamble = """
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
""";

    internal const string PrefixVariable = "BB_QOS_PREFIX";
    internal const string NameVariable = "BB_QOS_NAME";
    internal const string AppVariable = "BB_QOS_APP";
    internal const string DscpVariable = "BB_QOS_DSCP";
    internal const string StoreVariable = "BB_QOS_STORE";

    /// <summary>
    /// 读两个存储。Get-NetQosPolicy 的 -Name 接受通配符（实测 'BatterBabel-Game-*' 在无匹配时返回
    /// 0 条且不报错；空的 -Name 才会抛 CmdletizationQuery_NotFound_Name），所以这里用通配符而不是
    /// 枚举全表再过滤。某个存储读不出来（家用版的 Local 会报 Win32 53 "找不到网络路径"）不是
    /// "没有策略"，必须原样上报，否则会把"读不到"说成"已清理干净"。
    /// </summary>
    internal const string Query = Preamble + """
$prefix = $env:BB_QOS_PREFIX
$rules = New-Object System.Collections.Generic.List[object]
$errors = New-Object System.Collections.Generic.List[object]
foreach ($store in @('Local', 'ActiveStore')) {
    try {
        foreach ($policy in @(Get-NetQosPolicy -PolicyStore $store -Name "${prefix}*" -ErrorAction Stop)) {
            $rules.Add([pscustomobject]@{
                name  = "$($policy.Name)"
                store = "$store"
                app   = "$($policy.AppPathNameMatchCondition)"
                dscp  = [int]$policy.DSCPAction
            })
        }
    } catch {
        $errors.Add([pscustomobject]@{ store = "$store"; error = "$($_.FullyQualifiedErrorId): $($_.Exception.Message)" })
    }
}
[pscustomobject]@{ rules = $rules.ToArray(); errors = $errors.ToArray() } | ConvertTo-Json -Depth 5 -Compress
""";

    /// <summary>
    /// 创建一条应用匹配策略。参数与 scripts/optimization.ps1:424 完全一致：
    /// -AppPathNameMatchCondition / -DSCPAction / -NetworkProfile All / -PolicyStore。
    /// 刻意不带 -Precedence：发布版脚本不传（读回默认 127），DSCP 由 DSCPAction 决定。
    /// Local 先试（Group Policy 存储，重启后仍在），失败再试 ActiveStore（内存存储，重启即失）。
    /// </summary>
    internal const string Create = Preamble + """
$name = $env:BB_QOS_NAME
$app = $env:BB_QOS_APP
$dscp = [int]$env:BB_QOS_DSCP
$store = ''
$readback = $null
$errors = New-Object System.Collections.Generic.List[object]
foreach ($candidate in @('Local', 'ActiveStore')) {
    try {
        New-NetQosPolicy -Name $name -AppPathNameMatchCondition $app -DSCPAction $dscp -NetworkProfile All -PolicyStore $candidate -ErrorAction Stop | Out-Null
        $store = $candidate
        break
    } catch {
        $errors.Add([pscustomobject]@{ store = "$candidate"; error = "$($_.FullyQualifiedErrorId): $($_.Exception.Message)" })
    }
}
if ($store) {
    # 写后读回：创建成功不等于策略真的在存储里，读回的形状才是事实。
    try {
        $found = @(Get-NetQosPolicy -PolicyStore $store -Name $name -ErrorAction Stop)
        if ($found.Count -eq 1) {
            $readback = [pscustomobject]@{
                name = "$($found[0].Name)"
                app  = "$($found[0].AppPathNameMatchCondition)"
                dscp = [int]$found[0].DSCPAction
            }
        } else {
            $errors.Add([pscustomobject]@{ store = "$store"; error = "readback returned $($found.Count) rules" })
        }
    } catch {
        $errors.Add([pscustomobject]@{ store = "$store"; error = "readback: $($_.FullyQualifiedErrorId): $($_.Exception.Message)" })
    }
}
[pscustomobject]@{ store = $store; readback = $readback; errors = $errors.ToArray() } | ConvertTo-Json -Depth 5 -Compress
""";

    /// <summary>删除指定存储里的指定策略（替换 DSCP 或清理同名残留时用，不碰其它游戏）。</summary>
    internal const string RemoveOne = Preamble + """
$name = $env:BB_QOS_NAME
$store = $env:BB_QOS_STORE
try {
    Remove-NetQosPolicy -Name $name -PolicyStore $store -Confirm:$false -ErrorAction Stop
    [pscustomobject]@{ ok = $true; error = '' } | ConvertTo-Json -Compress
} catch {
    [pscustomobject]@{ ok = $false; error = "$($_.FullyQualifiedErrorId): $($_.Exception.Message)" } | ConvertTo-Json -Compress
}
""";

    /// <summary>清理所有本前缀策略：先按前缀列出（读回名可能是小写），再逐条删除。</summary>
    internal const string RemoveAll = Preamble + """
$prefix = $env:BB_QOS_PREFIX
$removed = New-Object System.Collections.Generic.List[object]
$errors = New-Object System.Collections.Generic.List[object]
$unreadable = New-Object System.Collections.Generic.List[string]
foreach ($store in @('Local', 'ActiveStore')) {
    try {
        $rules = @(Get-NetQosPolicy -PolicyStore $store -Name "${prefix}*" -ErrorAction Stop)
    } catch {
        $unreadable.Add("$store")
        continue
    }
    foreach ($policy in $rules) {
        $name = "$($policy.Name)"
        try {
            Remove-NetQosPolicy -Name $name -PolicyStore $store -Confirm:$false -ErrorAction Stop
            $removed.Add([pscustomobject]@{ name = $name; store = "$store" })
        } catch {
            $errors.Add([pscustomobject]@{ name = $name; store = "$store"; error = "$($_.FullyQualifiedErrorId): $($_.Exception.Message)" })
        }
    }
}
[pscustomobject]@{ removed = $removed.ToArray(); errors = $errors.ToArray(); unreadable = $unreadable.ToArray() } | ConvertTo-Json -Depth 5 -Compress
""";
}
