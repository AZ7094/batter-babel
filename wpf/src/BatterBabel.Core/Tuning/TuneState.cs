namespace BatterBabel.Core.Tuning;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Wi-Fi 省电模式的原始索引（powercfg 打印出来的 hex，不带 0x）。</summary>
public sealed record TuneWifiPower(
    [property: JsonPropertyName("ac")] TuneRawValue Ac,
    [property: JsonPropertyName("dc")] TuneRawValue Dc)
{
    public static TuneWifiPower Empty { get; } = new(TuneRawValue.Empty, TuneRawValue.Empty);
}

/// <summary>
/// 单文件快照 <c>%LOCALAPPDATA%\BatterBabel\tune-state.json</c>，字段名与发布版逐字一致
/// （TUNE-PARITY §5）：
/// <code>
/// { savedAt, nagle{guid{值名: 原值}}, tcpGlobal{autotuninglevel,rss},
///   adapterProps{网卡{keyword: 原值}}, adapterPower{网卡: "Enabled"}, throttle{...},
///   wifiPower{ac,dc}, appliedTier, managedConfigFiles[] }
/// </code>
/// <b>只抓一次、永不覆盖</b>：第二次调优时 originals 必须还是第一次之前的值，
/// 否则"还原"会把第一次调优后的值当成原始值写回去（发布版 2314 的注释就是这个意思）。
/// 唯一允许重写的时机是"记录本次 appliedTier / managedConfigFiles"（发布版 2496-2525 同样重写整个文件）。
///
/// <see cref="TuneRawValue"/> 保留 null / 数字 / 字符串三态：还原时 null 删值、0 写回 0。
/// </summary>
public sealed class TuneState
{
    [JsonPropertyName("savedAt")]
    public string SavedAt { get; set; } = string.Empty;

    [JsonPropertyName("nagle")]
    public Dictionary<string, Dictionary<string, TuneRawValue>> Nagle { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("tcpGlobal")]
    public Dictionary<string, TuneRawValue> TcpGlobal { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("adapterProps")]
    public Dictionary<string, Dictionary<string, TuneRawValue>> AdapterProps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("adapterPower")]
    public Dictionary<string, TuneRawValue> AdapterPower { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("throttle")]
    public Dictionary<string, TuneRawValue> Throttle { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("wifiPower")]
    public TuneWifiPower WifiPower { get; set; } = TuneWifiPower.Empty;

    /// <summary>本次实际应用成功的档位；有 net-set 失败时发布版写空串，用于下次重算全量 delta。</summary>
    [JsonPropertyName("appliedTier")]
    public string? AppliedTier { get; set; }

    [JsonPropertyName("managedConfigFiles")]
    public List<string> ManagedConfigFiles { get; set; } = new();

    public static TuneState CreateEmpty() => new()
    {
        SavedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
    };

    public TuneRawValue TcpGlobalValue(string name) =>
        TcpGlobal.TryGetValue(name, out var value) ? value : TuneRawValue.Empty;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 字典键原样保留（throttle 的键是 NetworkThrottlingIndex，tcpGlobal 的键是小写）
        DictionaryKeyPolicy = null,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>
/// 快照文件的读写。写入先落临时文件再原子替换，并<b>读回逐项比对</b>：快照是本批唯一的
/// 回滚依据，写坏了比不写更危险（会把一次错误的"原始值"永久写进还原路径）。
/// </summary>
public static class TuneStateStore
{
    public const string ToolName = "BatterBabel";

    /// <summary>与发布版相同的路径，不允许漂移（跨版本还原依赖它）。</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatterBabel", "tune-state.json");

    public static string ResolvePath(string? path) => string.IsNullOrWhiteSpace(path) ? DefaultPath : path;

    public static bool Exists(string? path = null) => File.Exists(ResolvePath(path));

    /// <summary>读快照。文件缺失返回 null；内容损坏抛 <see cref="InvalidDataException"/>。</summary>
    public static TuneState? Load(string? path = null)
    {
        var resolved = ResolvePath(path);
        if (!File.Exists(resolved))
        {
            return null;
        }

        var json = File.ReadAllText(resolved, Encoding.UTF8);
        TuneState? state;
        try
        {
            state = JsonSerializer.Deserialize<TuneState>(json, TuneState.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"快照文件无法解析：{resolved}（{ex.Message}）", ex);
        }

        if (state is null)
        {
            throw new InvalidDataException($"快照文件为空：{resolved}");
        }

        state.Nagle ??= new(StringComparer.OrdinalIgnoreCase);
        state.TcpGlobal ??= new(StringComparer.OrdinalIgnoreCase);
        state.AdapterProps ??= new(StringComparer.OrdinalIgnoreCase);
        state.AdapterPower ??= new(StringComparer.OrdinalIgnoreCase);
        state.Throttle ??= new(StringComparer.OrdinalIgnoreCase);
        state.WifiPower ??= TuneWifiPower.Empty;
        state.ManagedConfigFiles ??= new();
        return state;
    }

    /// <summary>写快照（原子替换 + 读回校验），失败抛异常由调用方转成 BLOCKED。</summary>
    public static void Save(TuneState state, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var resolved = ResolvePath(path);
        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(state, TuneState.JsonOptions);
        var tmp = resolved + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, resolved, overwrite: true);

        var readBack = Load(resolved) ?? throw new InvalidDataException($"快照写入后读回为空：{resolved}");
        if (state.Nagle.Count != readBack.Nagle.Count ||
            state.AdapterProps.Count != readBack.AdapterProps.Count ||
            state.ManagedConfigFiles.Count != readBack.ManagedConfigFiles.Count ||
            !string.Equals(state.AppliedTier ?? string.Empty, readBack.AppliedTier ?? string.Empty, StringComparison.Ordinal))
        {
            TryDelete(resolved);
            throw new InvalidDataException($"快照写入后读回不一致，已删除：{resolved}");
        }
    }

    public static void Delete(string? path = null)
    {
        var resolved = ResolvePath(path);
        if (File.Exists(resolved))
        {
            File.Delete(resolved);
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
