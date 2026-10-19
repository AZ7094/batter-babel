namespace BatterBabel.Core.Tuning;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using BatterBabel.Core.Qos;
using Microsoft.Win32;

/// <summary>
/// 真实系统边界：注册表走 <see cref="Microsoft.Win32.Registry"/>，全局 TCP 参数与 Wi-Fi 电源走
/// 原生进程（netsh / powercfg），网卡高级属性与网卡电源走 PowerShell CIM cmdlet
/// （<c>Get/Set-NetAdapterAdvancedProperty</c>、<c>Set-NetAdapterPowerManagement</c>）。
///
/// 为什么网卡属性不直接读写驱动注册表：发布版就是调 CIM cmdlet，<c>ValidRegistryValues</c>
/// （"这个驱动接受的合法值集合，按 [int] 升序取端点"）只有 CIM 给得出；自己读 INF/注册表
/// 去猜端点会把 Intel/Realtek 的私有编码搞错。这里复用 CIM 是最小改动、语义最一致的做法。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTuneBoundary : ITuneBoundary
{
    private const string WifiSubgroup = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
    private const string WifiSetting = "12bbebe6-58d6-4636-95bb-3217ef867c1a";
    private const string TcpipInterfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private readonly CancellationToken _ct;

    public WindowsTuneBoundary(CancellationToken ct = default)
    {
        _ct = ct;
    }

    static WindowsTuneBoundary()
    {
        try
        {
            // 中文控制台输出（netsh / powercfg）要以 OEM 代码页解码；.NET 默认不带 GBK。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            // 拿不到额外代码页时就只剩 UTF-8/Latin-1 两条路，不会影响数值解析。
        }
    }

    // ───────────────────────────────── 网卡 ─────────────────────────────────

    public IReadOnlyList<TuneAdapterInfo> Adapters()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
            $list = New-Object System.Collections.ArrayList
            foreach ($a in @(Get-NetAdapter -Physical -ErrorAction Stop)) {
              $props = [ordered]@{}
              foreach ($kw in @('*InterruptModeration','*EEE','*FlowControl')) {
                try {
                  $p = @(Get-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword $kw -ErrorAction Stop) | Select-Object -First 1
                  if ($null -ne $p) {
                    $raw = @($p.RegistryValue)[0]
                    $valid = @($p.ValidRegistryValues | ForEach-Object { [string]$_ })
                    $props[$kw] = [pscustomobject]@{ value = $(if ($null -eq $raw) { $null } else { [string]$raw }); valid = $valid }
                  }
                } catch { }
              }
              $power = $null
              try {
                $pm = Get-NetAdapterPowerManagement -Name $a.Name -ErrorAction Stop
                # 本机 NetAdapter 模块（Win 10.0.26200）没有 AllowComputerToTurnOffDevice 参数/属性，
                # 只有 SelectiveSuspend；两者都是「设备省电开关」，按可用性取一个。
                $allow = $pm.PSObject.Properties['AllowComputerToTurnOffDevice']
                $sel = $pm.PSObject.Properties['SelectiveSuspend']
                if ($null -ne $allow) { $power = [string]$allow.Value }
                elseif ($null -ne $sel) { $power = [string]$sel.Value }
              } catch { }
              [void]$list.Add([pscustomobject]@{
                name = [string]$a.Name;
                guid = $(if ($null -ne $a.InterfaceGuid) { [string]$a.InterfaceGuid } else { $null });
                mediaType = [string]$a.MediaType;
                props = $props;
                power = $power
              })
            }
            ConvertTo-Json -InputObject @($list.ToArray()) -Depth 6 -Compress
            """;

        var outcome = RunPowerShell(script);
        if (!outcome.Ok)
        {
            throw new InvalidOperationException($"读取网卡信息失败：{Summarize(outcome)}");
        }

        var json = outcome.StdOut.Trim();
        if (json.Length == 0 || json == "[]")
        {
            return Array.Empty<TuneAdapterInfo>();
        }

        var adapters = new List<TuneAdapterInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            // 单块网卡时 ConvertTo-Json 仍可能给出对象（-InputObject 数组理论上不会，防一手）。
            adapters.Add(ParseAdapter(doc.RootElement));
        }
        else
        {
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                adapters.Add(ParseAdapter(element));
            }
        }

        return adapters;
    }

    private static TuneAdapterInfo ParseAdapter(JsonElement element)
    {
        var name = Text(element, "name") ?? string.Empty;
        var guid = Text(element, "guid");
        var mediaType = Text(element, "mediaType") ?? string.Empty;
        var props = new Dictionary<string, TuneRawValue>(StringComparer.Ordinal);
        var valid = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        if (element.TryGetProperty("props", out var propsElement) && propsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in propsElement.EnumerateObject())
            {
                var raw = property.Value.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String
                    ? valueElement.GetString()
                    : null;
                props[property.Name] = TuneRawValue.OfText(raw);

                var values = new List<string>();
                if (property.Value.TryGetProperty("valid", out var validElement) && validElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in validElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                        {
                            values.Add(text);
                        }
                    }
                }

                // 发布版：$valid = ValidRegistryValues | Sort-Object { [int]$_ }，端点即 0/1。
                values.Sort(CompareNumericThenOrdinal);
                valid[property.Name] = values;
            }
        }

        var hasTcpipKey = false;
        if (!string.IsNullOrEmpty(guid))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{TcpipInterfaces}\{guid}");
                hasTcpipKey = key is not null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                hasTcpipKey = false;
            }
        }

        return new TuneAdapterInfo(
            name,
            string.IsNullOrEmpty(guid) ? null : guid,
            mediaType,
            hasTcpipKey,
            props,
            valid,
            TuneRawValue.OfText(Text(element, "power")));
    }

    private static int CompareNumericThenOrdinal(string left, string right)
    {
        var leftOk = int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leftNumber);
        var rightOk = int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rightNumber);
        if (leftOk && rightOk && leftNumber != rightNumber)
        {
            return leftNumber.CompareTo(rightNumber);
        }

        return string.CompareOrdinal(left, right);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public IReadOnlyList<string> ProcessNames()
    {
        var names = new List<string>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        names.Add(process.ProcessName);
                    }
                    catch (InvalidOperationException)
                    {
                        // 进程在枚举与读名之间退出，忽略。
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return names;
        }

        return names;
    }

    // ───────────────────────────────── 注册表 ─────────────────────────────────

    public TuneRawValue ReadRegistry(RegistryValueTarget target)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(target.SubKey, writable: false);
            if (key is null)
            {
                return TuneRawValue.Empty;
            }

            var kind = key.GetValueKind(target.Name);
            var value = key.GetValue(target.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return kind switch
            {
                RegistryValueKind.DWord when value is int dword => TuneRawValue.OfNumber(unchecked((uint)dword)),
                RegistryValueKind.DWord => TuneRawValue.Empty,
                RegistryValueKind.QWord when value is long qword => TuneRawValue.OfNumber(qword),
                RegistryValueKind.QWord => TuneRawValue.Empty,
                RegistryValueKind.Binary when value is byte[] bytes => TuneRawValue.OfText(Convert.ToHexString(bytes)),
                _ => TuneRawValue.OfText(value?.ToString()),
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return TuneRawValue.Empty;
        }
    }

    public TuneWriteOutcome WriteRegistryDword(RegistryValueTarget target, int value)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(target.SubKey, writable: true);
            if (key is null)
            {
                return TuneWriteOutcome.Failure($"注册表项不存在：{target.KeyPath}");
            }

            key.SetValue(target.Name, value, RegistryValueKind.DWord);
            var readBack = ReadRegistry(target);
            return readBack.TryNumber(out var actual) && actual == value
                ? TuneWriteOutcome.Success()
                : TuneWriteOutcome.Failure($"写入后读回不一致：{target.Display}（读回 {readBack.Display}）");
        }
        catch (UnauthorizedAccessException)
        {
            return TuneWriteOutcome.Failure($"没有权限写入 {target.Display}（需要管理员）");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return TuneWriteOutcome.Failure($"{target.Display}：{ex.Message}");
        }
    }

    public TuneWriteOutcome WriteRegistryString(RegistryValueTarget target, string value)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(target.SubKey, writable: true);
            if (key is null)
            {
                return TuneWriteOutcome.Failure($"注册表项不存在：{target.KeyPath}");
            }

            key.SetValue(target.Name, value, RegistryValueKind.String);
            var readBack = ReadRegistry(target);
            return string.Equals(readBack.Display, value, StringComparison.Ordinal)
                ? TuneWriteOutcome.Success()
                : TuneWriteOutcome.Failure($"写入后读回不一致：{target.Display}（读回 {readBack.Display}）");
        }
        catch (UnauthorizedAccessException)
        {
            return TuneWriteOutcome.Failure($"没有权限写入 {target.Display}（需要管理员）");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return TuneWriteOutcome.Failure($"{target.Display}：{ex.Message}");
        }
    }

    public TuneWriteOutcome DeleteRegistryValue(RegistryValueTarget target)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(target.SubKey, writable: true);
            if (key is null)
            {
                return TuneWriteOutcome.Success("注册表项不存在，无需删除");
            }

            key.DeleteValue(target.Name, throwOnMissingValue: false);
            return ReadRegistry(target).IsEmpty
                ? TuneWriteOutcome.Success()
                : TuneWriteOutcome.Failure($"删除后读回仍存在：{target.Display}");
        }
        catch (UnauthorizedAccessException)
        {
            return TuneWriteOutcome.Failure($"没有权限删除 {target.Display}（需要管理员）");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return TuneWriteOutcome.Failure($"{target.Display}：{ex.Message}");
        }
    }

    // ───────────────────────────────── 全局 TCP 参数 ─────────────────────────────────

    public string? ReadTcpGlobal(string name)
    {
        var run = RunNative("netsh.exe", new[] { "int", "tcp", "show", "global" });
        if (run.Code != 0)
        {
            return null;
        }

        foreach (var line in run.Out.Split('\n'))
        {
            var index = line.IndexOf(':');
            if (index <= 0)
            {
                continue;
            }

            var label = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            // 发布版正则取 (\S+)：去掉 "normal (recommended)" 这类尾巴。
            var token = value.Split(' ', '\t')[0];
            if (name.Equals("autotuninglevel", StringComparison.OrdinalIgnoreCase) && IsAutoTuningLabel(label))
            {
                return token;
            }

            if (name.Equals("rss", StringComparison.OrdinalIgnoreCase) && IsRssLabel(label))
            {
                return token;
            }
        }

        return null;
    }

    private static bool IsAutoTuningLabel(string label) =>
        label.Contains("Auto-Tuning", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("AutoTuning", StringComparison.OrdinalIgnoreCase) ||
        (label.Contains('自') && (label.Contains("调节") || label.Contains("调整")) && label.Contains("级别"));

    private static bool IsRssLabel(string label) =>
        label.Contains("Receive-Side Scaling", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("Receive Side Scaling", StringComparison.OrdinalIgnoreCase) ||
        (label.Contains("缩放") && label.Contains("状态"));

    public TuneWriteOutcome SetTcpGlobal(string name, string value)
    {
        var run = RunNative("netsh.exe", new[] { "int", "tcp", "set", "global", $"{name}={value}" });
        var code = run.Code;
        return code == 0
            ? TuneWriteOutcome.Success(exitCode: code)
            : TuneWriteOutcome.Failure(FirstLine(run), code);
    }

    // ───────────────────────────────── 网卡属性 / 电源 ─────────────────────────────────

    public TuneWriteOutcome SetAdapterProperty(string adapterName, string keyword, string rawValue)
    {
        var script = """
            $ErrorActionPreference = 'Stop'
            try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
            Set-NetAdapterAdvancedProperty -Name $env:BB_TUNE_ADAPTER -RegistryKeyword $env:BB_TUNE_KEYWORD -RegistryValue $env:BB_TUNE_VALUE -ErrorAction Stop
            'ok'
            """;
        var outcome = RunPowerShell(script, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BB_TUNE_ADAPTER"] = adapterName,
            ["BB_TUNE_KEYWORD"] = keyword,
            ["BB_TUNE_VALUE"] = rawValue,
        });
        return outcome.Ok
            ? TuneWriteOutcome.Success(exitCode: outcome.ExitCode)
            : TuneWriteOutcome.Failure(Summarize(outcome), outcome.ExitCode);
    }

    public TuneWriteOutcome SetAdapterPower(string adapterName, string value)
    {
        var script = """
            $ErrorActionPreference = 'Stop'
            try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
            try {
              Set-NetAdapterPowerManagement -Name $env:BB_TUNE_ADAPTER -AllowComputerToTurnOffDevice $env:BB_TUNE_VALUE -ErrorAction Stop
              'ok'
            } catch {
              if ($_.Exception.Message -notmatch 'AllowComputerToTurnOffDevice') { throw }
              # 平台没有这个参数时退回同义开关：SelectiveSuspend（设备选择性挂起）。
              Set-NetAdapterPowerManagement -Name $env:BB_TUNE_ADAPTER -SelectiveSuspend $env:BB_TUNE_VALUE -ErrorAction Stop
              'ok'
            }
            """;
        var outcome = RunPowerShell(script, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BB_TUNE_ADAPTER"] = adapterName,
            ["BB_TUNE_VALUE"] = value,
        });
        return outcome.Ok
            ? TuneWriteOutcome.Success(exitCode: outcome.ExitCode)
            : TuneWriteOutcome.Failure(Summarize(outcome), outcome.ExitCode);
    }

    // ───────────────────────────────── Wi-Fi 电源 ─────────────────────────────────

    public string? ReadWifiPowerIndex(bool ac)
    {
        var run = RunNative("powercfg.exe", new[] { "/query", "SCHEME_CURRENT", WifiSubgroup, WifiSetting });
        if (run.Code != 0)
        {
            return null;
        }

        var hexes = new List<string>();
        string? acValue = null;
        string? dcValue = null;
        foreach (var line in run.Out.Split('\n'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, "0x([0-9a-fA-F]+)");
            if (!match.Success)
            {
                continue;
            }

            hexes.Add(match.Groups[1].Value);
            if (IsAcLabel(line))
            {
                acValue = match.Groups[1].Value;
            }
            else if (IsDcLabel(line))
            {
                dcValue = match.Groups[1].Value;
            }
        }

        if (ac)
        {
            // powercfg 先打印 AC 再打印 DC；标签认不出来时按出现顺序兜底（与发布版的行内正则等价）。
            return acValue ?? (hexes.Count > 0 ? hexes[0] : null);
        }

        return dcValue ?? (hexes.Count > 1 ? hexes[1] : null);
    }

    private static bool IsAcLabel(string line) =>
        line.Contains("Current AC", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("当前交流", StringComparison.Ordinal) ||
        (line.Contains("AC", StringComparison.OrdinalIgnoreCase) && line.Contains("0x", StringComparison.OrdinalIgnoreCase));

    private static bool IsDcLabel(string line) =>
        line.Contains("Current DC", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("当前直流", StringComparison.Ordinal) ||
        (line.Contains("DC", StringComparison.OrdinalIgnoreCase) && line.Contains("0x", StringComparison.OrdinalIgnoreCase));

    public TuneWriteOutcome SetWifiPowerIndex(string? acHex, string? dcHex)
    {
        var codes = new List<int>();
        string[] common = { "SCHEME_CURRENT", WifiSubgroup, WifiSetting };

        if (acHex is not null)
        {
            codes.Add(RunNative("powercfg.exe", new[] { "/setacvalueindex" }.Concat(common).Append(HexArg(acHex)).ToArray()).Code);
        }

        if (dcHex is not null)
        {
            codes.Add(RunNative("powercfg.exe", new[] { "/setdcvalueindex" }.Concat(common).Append(HexArg(dcHex)).ToArray()).Code);
        }

        codes.Add(RunNative("powercfg.exe", new[] { "/setactive", "SCHEME_CURRENT" }).Code);
        var failed = codes.Where(code => code != 0).ToList();
        var detail = $"powercfg exit {string.Join("/", codes)}";
        return failed.Count == 0 ? TuneWriteOutcome.Success(detail, codes[^1]) : TuneWriteOutcome.Failure($"failed: {detail}", codes[^1]);
    }

    public TuneWriteOutcome SetWifiPowerMax() => SetWifiPowerIndex("0", "0");

    private static string HexArg(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value : "0x" + value;

    // ───────────────────────────────── 进程 ─────────────────────────────────

    private QosProcessOutcome RunPowerShell(string script, IReadOnlyDictionary<string, string>? environment = null) =>
        QosPowerShell
            .RunAsync(script, environment ?? new Dictionary<string, string>(StringComparer.Ordinal), _ct)
            .GetAwaiter()
            .GetResult();

    private (int Code, string Out, string Err) RunNative(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, exe),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return (-1, string.Empty, $"无法启动 {exe}");
            }

            var stdout = process.StandardOutput.BaseStream;
            var stderr = process.StandardError.BaseStream;
            var outTask = ReadAllAsync(stdout);
            var errTask = ReadAllAsync(stderr);
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 已经退出。
                }

                return (-1, string.Empty, $"{exe} 超时");
            }

            return (process.ExitCode, Decode(outTask.GetAwaiter().GetResult()), Decode(errTask.GetAwaiter().GetResult()));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty, $"{exe}：{ex.Message}");
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 继续用控制台代码页解。
        }

        try
        {
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            return oem.GetString(bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string FirstLine((int Code, string Out, string Err) run)
    {
        foreach (var text in new[] { run.Err, run.Out })
        {
            foreach (var line in text.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line.Trim();
                }
            }
        }

        return $"exit {run.Code}";
    }

    private static string Summarize(QosProcessOutcome outcome)
    {
        if (outcome.Failure is not null)
        {
            return outcome.Failure;
        }

        foreach (var text in new[] { outcome.StdErr, outcome.StdOut })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var line = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (line is not null)
            {
                return line.Trim();
            }
        }

        return $"exit {outcome.ExitCode}";
    }
}
