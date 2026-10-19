namespace BatterBabel.Core.Tuning;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 快照里的一个原始值。三态是这套语义的核心：
///   - <see cref="IsEmpty"/>：原值不存在 → 还原时删值（发布版 Remove-ItemProperty）；
///   - <see cref="TryNumber"/> 成功：原值是数字 → 还原时按 DWORD 写回，<b>0 也照样写回 0</b>；
///   - 其余：原值是文本（驱动写 REG_SZ 的情况）→ 还原时原样写回 REG_SZ。
/// JSON 形状与发布版一致：null / 数字 / 字符串。
/// </summary>
[JsonConverter(typeof(TuneRawValueJsonConverter))]
public readonly record struct TuneRawValue(long? Number, string? Text)
{
    public static TuneRawValue Empty => default;

    public bool IsEmpty => Number is null && Text is null;

    public static TuneRawValue OfNumber(long value) => new(value, null);

    public static TuneRawValue OfText(string? value) => value is null ? default : new TuneRawValue(null, value);

    /// <summary>能按 DWORD 还原才返回 true；越界（负数或 > 0xFFFFFFFF）视为不可还原的数字。</summary>
    public bool TryNumber(out int value)
    {
        value = 0;
        if (Number is not { } number || number < int.MinValue || number > uint.MaxValue)
        {
            return false;
        }

        value = unchecked((int)(uint)number);
        return true;
    }

    public bool SameAs(TuneRawValue other) =>
        Number == other.Number && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <summary>展示用；缺失值不写成 0，避免把"没设过"说成"设成 0"。</summary>
    public string Display =>
        Number is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : Text is { } t ? t
        : "(未设置)";

    public override string ToString() => Display;
}

public sealed class TuneRawValueJsonConverter : JsonConverter<TuneRawValue>
{
    public override TuneRawValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => TuneRawValue.Empty,
            JsonTokenType.Number when reader.TryGetInt64(out var number) => TuneRawValue.OfNumber(number),
            JsonTokenType.Number => TuneRawValue.OfText(reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonTokenType.String => TuneRawValue.OfText(reader.GetString()),
            JsonTokenType.True => TuneRawValue.OfText("true"),
            JsonTokenType.False => TuneRawValue.OfText("false"),
            _ => TuneRawValue.Empty,
        };

    public override void Write(Utf8JsonWriter writer, TuneRawValue value, JsonSerializerOptions options)
    {
        if (value.Number is { } number)
        {
            writer.WriteNumberValue(number);
        }
        else if (value.Text is { } text)
        {
            writer.WriteStringValue(text);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>注册表值坐标。Hive 只用 HKLM，全部调优面都在本机机器范围。</summary>
public readonly record struct RegistryValueTarget(string Hive, string SubKey, string Name)
{
    public const string Hklm = "HKLM";

    public static RegistryValueTarget LocalMachine(string subKey, string name) => new(Hklm, subKey, name);

    public string KeyPath => $"{Hive}\\{SubKey}";
    public string Display => $"{KeyPath}\\{Name}";
}

/// <summary>
/// 一次系统写入的结果。成功也带 detail：netsh/powercfg 的退出码要原样出现在 items[].detail 里
/// （发布版 detail 就是这么写的："autotuninglevel=normal (netsh exit 0)"）。
/// </summary>
public sealed record TuneWriteOutcome(bool Ok, string Detail = "", int? ExitCode = null)
{
    public static TuneWriteOutcome Success(string detail = "", int? exitCode = null) => new(true, detail, exitCode);

    public static TuneWriteOutcome Failure(string detail, int? exitCode = null) => new(false, detail, exitCode);
}

/// <summary>
/// 一块物理网卡的当前状态：高级属性 keyword → 当前 RegistryValue（字符串形式，与
/// Set-NetAdapterAdvancedProperty -RegistryValue 的入参一致）、驱动自己发布的合法值集合
/// （已按 [int] 升序，发布版就是拿 [0]/[last] 做 0/1 映射）、以及设备省电开关。
/// </summary>
public sealed record TuneAdapterInfo(
    string Name,
    string? Guid,
    string MediaType,
    bool HasTcpipKey,
    IReadOnlyDictionary<string, TuneRawValue> Properties,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ValidValues,
    TuneRawValue PowerTurnOff)
{
    public bool IsWifi =>
        string.Equals(MediaType, "Native 802.11", StringComparison.OrdinalIgnoreCase) ||
        (MediaType?.Contains("802.11", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>
/// 系统边界。上层的编排（档位、delta、快照、items/log/status、还原顺序）全部在
/// <see cref="TuneEngine"/> 里，对真实系统一无所知；自检工具注入一个内存假实现就能把
/// 整条链路跑一遍而不碰注册表（<c>BatterBabel.TuneCheck --write-selftest</c>）。
/// </summary>
public interface ITuneBoundary
{
    /// <summary>当前物理网卡（顺序无所谓，编排按名字/GUID 查）。</summary>
    IReadOnlyList<TuneAdapterInfo> Adapters();

    /// <summary>正在运行的进程名（后台占网扫描只报告，不 kill）。</summary>
    IReadOnlyList<string> ProcessNames();

    TuneRawValue ReadRegistry(RegistryValueTarget target);

    TuneWriteOutcome WriteRegistryDword(RegistryValueTarget target, int value);

    TuneWriteOutcome WriteRegistryString(RegistryValueTarget target, string value);

    TuneWriteOutcome DeleteRegistryValue(RegistryValueTarget target);

    /// <summary>netsh int tcp show global 里的一项（autotuninglevel / rss）；读不到返回 null。</summary>
    string? ReadTcpGlobal(string name);

    /// <summary>netsh int tcp set global "&lt;name&gt;=&lt;value&gt;"。</summary>
    TuneWriteOutcome SetTcpGlobal(string name, string value);

    /// <summary>Set-NetAdapterAdvancedProperty -RegistryKeyword &lt;keyword&gt; -RegistryValue &lt;rawValue&gt;</summary>
    TuneWriteOutcome SetAdapterProperty(string adapterName, string keyword, string rawValue);

    /// <summary>Set-NetAdapterPowerManagement -AllowComputerToTurnOffDevice &lt;Enabled|Disabled&gt;</summary>
    TuneWriteOutcome SetAdapterPower(string adapterName, string value);

    /// <summary>powercfg /query 里 WLAN 省电模式的 AC/DC 索引（hex，不带 0x）；读不到返回 null。</summary>
    string? ReadWifiPowerIndex(bool ac);

    /// <summary>powercfg setac + setdc 同一索引 + setactive；null 表示该路不写。</summary>
    TuneWriteOutcome SetWifiPowerIndex(string? acHex, string? dcHex);

    /// <summary>Wi-Fi 省电模式设为"最高性能"（AC/DC 都写 0）+ setactive。</summary>
    TuneWriteOutcome SetWifiPowerMax();
}
