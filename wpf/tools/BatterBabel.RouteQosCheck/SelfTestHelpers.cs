using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BatterBabel.Core.Qos;

namespace BatterBabel.RouteQosCheck;

/// <summary>
/// selftest 专用的证据采集与平台探针。产品读写不经过这里。
/// 探针刻意用与 RouteQosPolicy 相同的 New-NetQosPolicy 形状（-IPDstPrefixMatchCondition &lt;ip&gt;/32
/// -DSCPAction 46 -NetworkProfile All -PolicyStore ActiveStore），建完立刻读回、删掉，
/// 用 Get-NetQosPolicy 的原始字段证明「这个平台到底存不存目标 IP 条件」。
/// </summary>
internal static partial class Program
{
    private sealed record RawProbe(
        bool Ok,
        bool Created,
        int RemainingAfter,
        Dictionary<string, string> Fields,
        string Error);

    private static async Task<RawProbe> RawProbeAsync(string name, string ip, int dscp)
    {
        const string script = """
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$name = $env:BB_PROBE_NAME
$ip = $env:BB_PROBE_IP
$dscp = [int]$env:BB_PROBE_DSCP
$fields = [ordered]@{}
$created = $false
$errorText = ''
$removeError = ''
try {
    foreach ($stale in @(Get-NetQosPolicy -PolicyStore 'ActiveStore' -Name $name -ErrorAction SilentlyContinue)) {
        Remove-NetQosPolicy -Name $stale.Name -PolicyStore 'ActiveStore' -Confirm:$false -ErrorAction Stop | Out-Null
    }
    New-NetQosPolicy -Name $name -IPDstPrefixMatchCondition "$ip/32" -DSCPAction $dscp -NetworkProfile All -PolicyStore 'ActiveStore' -ErrorAction Stop | Out-Null
    $created = $true
} catch {
    $errorText = "$($_.FullyQualifiedErrorId): $($_.Exception.Message)"
}
if ($created) {
    try {
        $read = @(Get-NetQosPolicy -PolicyStore 'ActiveStore' -Name $name -ErrorAction Stop)
        if ($read.Count -eq 1) {
            foreach ($property in $read[0].PSObject.Properties) {
                if ($property.Name -like 'Cim*' -or $property.Name -like 'PS*') { continue }
                $fields[$property.Name] = "$($property.Value)"
            }
        } else {
            $errorText = "readback count $($read.Count)"
        }
    } catch {
        $errorText = "readback: $($_.Exception.Message)"
    }
}
try {
    foreach ($left in @(Get-NetQosPolicy -PolicyStore 'ActiveStore' -Name $name -ErrorAction SilentlyContinue)) {
        Remove-NetQosPolicy -Name $left.Name -PolicyStore 'ActiveStore' -Confirm:$false -ErrorAction Stop | Out-Null
    }
} catch {
    $removeError = "$($_.Exception.Message)"
}
$remaining = @(Get-NetQosPolicy -PolicyStore 'ActiveStore' -Name $name -ErrorAction SilentlyContinue).Count
[pscustomobject]@{
    ok = ($errorText -eq '');
    created = $created;
    remainingAfter = $remaining;
    fields = [pscustomobject]$fields;
    error = $errorText;
    removeError = $removeError;
} | ConvertTo-Json -Depth 6 -Compress
""";

        var (ok, stdout, error) = await RunScriptAsync(script, new Dictionary<string, string>
        {
            ["BB_PROBE_NAME"] = name,
            ["BB_PROBE_IP"] = ip,
            ["BB_PROBE_DSCP"] = dscp.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).ConfigureAwait(false);

        if (!ok)
        {
            return new RawProbe(false, false, -1, new Dictionary<string, string>(), error);
        }

        return ParseProbe(stdout);
    }

    private static RawProbe ParseProbe(string stdout)
    {
        var text = ExtractJson(stdout);
        if (text is null)
        {
            return new RawProbe(false, false, -1, new Dictionary<string, string>(), "探针没有返回 JSON");
        }

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in fieldsElement.EnumerateObject())
            {
                fields[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }
        }

        return new RawProbe(
            root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True,
            root.TryGetProperty("created", out var createdElement) && createdElement.ValueKind == JsonValueKind.True,
            root.TryGetProperty("remainingAfter", out var remainingElement) && remainingElement.TryGetInt32(out var remaining) ? remaining : -1,
            fields,
            RootString(root, "error"));
    }

    /// <summary>读出机器上所有 BatterBabel* 策略名，用来断言「别人的策略没被我们动过」。</summary>
    private static async Task<List<string>> NonRouteNamesAsync()
    {
        var all = await RawDumpAsync("BatterBabel*").ConfigureAwait(false);
        if (!all.Ok)
        {
            return new List<string> { "<read-failed>" };
        }

        return all.Rules
            .Select(rule => Field(rule, "Name"))
            .Where(name => name.Length > 0 && !name.StartsWith(RouteQosPolicy.PolicyPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static string RootString(JsonElement root, string key)
        => root.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static string? ExtractJson(string stdout)
    {
        var text = stdout.Trim();
        if (text.StartsWith('{') && text.EndsWith('}'))
        {
            return text;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static async Task<(bool Ok, string Stdout, string Error)> RunScriptAsync(
        string script,
        IReadOnlyDictionary<string, string> environment)
    {
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
        foreach (var (key, value) in environment)
        {
            psi.Environment[key] = value;
        }

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
            {
                return (false, string.Empty, "无法启动 powershell.exe");
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
                return (false, string.Empty, $"PowerShell 调用超时（{PowerShellTimeoutMs / 1000} s）");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
                return (false, stdout, $"PowerShell 退出码 {process.ExitCode}：{Short(detail)}");
            }

            return (true, stdout, string.Empty);
        }
        catch (Exception ex)
        {
            try { if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); } } catch { }
            return (false, string.Empty, $"{ex.GetType().Name}: {Short(ex.Message)}");
        }
        finally
        {
            process?.Dispose();
        }
    }
}
