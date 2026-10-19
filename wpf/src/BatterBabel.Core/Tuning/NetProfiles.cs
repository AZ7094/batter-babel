namespace BatterBabel.Core.Tuning;

using System.Globalization;

/// <summary>
/// 发布版 net-profiles.ps1 的档位/配方/差异逻辑，逐条搬到 C#（只读参考，行号见 TUNE-PARITY §1）。
///
/// 为什么档位值要区分 Bool/Int/Text：发布版里 latency 的 Nagle 是 <c>$true</c>，
/// 而 throughput 的 Nagle 是字符串 <c>'Restore'</c>。PowerShell 的 <c>$true -eq 'Restore'</c>
/// 会得到 true（非空字符串被强制成布尔），所以发布版必须先做 <c>-is [string]</c> 判定。
/// C# 里用显式 Kind 表达同一件事，不存在隐式转换。
/// </summary>
public enum TuneValueKind
{
    Bool,
    Int,
    Text,
}

public readonly record struct TuneValue(TuneValueKind Kind, bool BoolValue, int IntValue, string? TextValue)
{
    public static TuneValue OfBool(bool value) => new(TuneValueKind.Bool, value, 0, null);

    public static TuneValue OfInt(int value) => new(TuneValueKind.Int, false, value, null);

    public static TuneValue OfText(string value) => new(TuneValueKind.Text, false, 0, value);

    /// <summary>发布版 <c>value -is [string] -and $value -eq 'Restore'</c> 的等价物。</summary>
    public bool IsRestore => Kind == TuneValueKind.Text && string.Equals(TextValue, "Restore", StringComparison.Ordinal);

    public bool SameAs(TuneValue other) => Kind == other.Kind && Kind switch
    {
        TuneValueKind.Bool => BoolValue == other.BoolValue,
        TuneValueKind.Int => IntValue == other.IntValue,
        _ => string.Equals(TextValue, other.TextValue, StringComparison.OrdinalIgnoreCase),
    };

    public override string ToString() => Kind switch
    {
        TuneValueKind.Bool => BoolValue ? "true" : "false",
        TuneValueKind.Int => IntValue.ToString(CultureInfo.InvariantCulture),
        _ => TextValue ?? string.Empty,
    };
}

/// <summary>一套档位。Values 的顺序就是发布版 ordered hashtable 的顺序（Id/Label 之后 9 个键）。</summary>
public sealed record TuneTier(string Id, string Label, IReadOnlyList<KeyValuePair<string, TuneValue>> Values)
{
    public TuneValue? Get(string key)
    {
        foreach (var pair in Values)
        {
            if (string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }
}

/// <summary>档位差异：Set 要写成档位值，Restore 要写回快照原值（'Restore' 键）。</summary>
public sealed record TuneDelta(IReadOnlyList<string> Set, IReadOnlyList<string> Restore);

/// <summary>配方里的一个配置文件（相对安装根目录 + 托管块内容）。</summary>
public sealed record RecipeConfigFile(string RelativePath, IReadOnlyList<string> Lines);

/// <summary>一个游戏的网络配方（net-profiles.ps1:51-147）。</summary>
public sealed record GameRecipe(
    string GameId,
    string Tier,
    int ExecutableParentDepth,
    IReadOnlyList<RecipeConfigFile> ConfigFiles,
    IReadOnlyList<string> NonNetworkNotes,
    bool HasSurface);

/// <summary>本次要做什么：档位 + 差异 + 配置写入 + 说明行（Get-NetTunePlan 的等价物）。</summary>
public sealed record TunePlan(
    string Tier,
    IReadOnlyList<string> Set,
    IReadOnlyList<string> Restore,
    IReadOnlyList<TuneConfigTarget> ConfigWrites,
    IReadOnlyList<string> Notes);

public static class NetProfiles
{
    public const string Latency = "latency";
    public const string Throughput = "throughput";

    /// <summary>Get-NetTierIds：字面量顺序，不依赖字典的枚举顺序。</summary>
    public static IReadOnlyList<string> TierIds { get; } = new[] { Latency, Throughput };

    /// <summary>Get-NagleValueNames（net-profiles.ps1:153-157）。</summary>
    public static IReadOnlyList<string> NagleValueNames { get; } = new[] { "TcpAckFrequency", "TCPNoDelay", "TcpDelAckTicks" };

    /// <summary>TcpDelAckTicks 写 0，另外两个写 1（optimization.ps1:1863-1883）。</summary>
    public static int NagleValueFor(string valueName) =>
        string.Equals(valueName, "TcpDelAckTicks", StringComparison.Ordinal) ? 0 : 1;

    /// <summary>档位键 → 网卡高级属性 keyword（optimization.ps1:1826-1830）。</summary>
    public static IReadOnlyDictionary<string, string> AdapterKeywords { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["InterruptModeration"] = "*InterruptModeration",
            ["Eee"] = "*EEE",
            ["FlowControl"] = "*FlowControl",
        };

    /// <summary>tier 键的固定应用顺序（latency/throughput 两个 ordered hashtable 的键序一致）。</summary>
    public static IReadOnlyList<string> TuneOrder { get; } = new[]
    {
        "Nagle", "InterruptModeration", "Eee", "FlowControl", "AutoTuning",
        "Rss", "Throttling", "AdapterPower", "WifiPower",
    };

    /// <summary>后台占网进程 → 中文标签（optimization.ps1:2406-2434 的进程名单）。只报告，不 kill。</summary>
    public static IReadOnlyDictionary<string, string> HogNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BaiduNetdisk"] = "百度网盘",
            ["BaiduNetdiskHost"] = "百度网盘",
            ["Thunder"] = "迅雷",
            ["ThunderPlatform"] = "迅雷",
            ["XLServicePlatform"] = "迅雷",
            ["xunlei"] = "迅雷",
            ["QQDownload"] = "QQ旋风",
            ["IDMan"] = "Internet Download Manager",
            ["aria2c"] = "aria2",
            ["qbittorrent"] = "qBittorrent",
            ["utorrent"] = "uTorrent",
            ["BitTorrent"] = "BitTorrent",
            ["iQIYI"] = "爱奇艺",
            ["QQLive"] = "腾讯视频",
            ["YoukuDesktop"] = "优酷",
            ["PPTV"] = "PPTV",
            ["bilibili"] = "哔哩哔哩",
            ["BilibiliLive"] = "哔哩哔哩直播",
            ["KuGou"] = "酷狗音乐",
            ["QQMusic"] = "QQ音乐",
            ["CloudMusic"] = "网易云音乐",
        };

    /// <summary>CS2 的托管块内容（net-profiles.ps1:44-49，与 App 的 LiveRoutes.Cs2ConfigLines 逐字一致）。</summary>
    public static IReadOnlyList<string> Cs2ConfigLines { get; } = new[]
    {
        "// Batter Babel (generated) - CS2 has no client-side network cvar worth setting;",
        "// see docs/superpowers/specs/2026-10-04-per-game-network-profiles-design.md",
        "// The line below is a rendering tweak, not a network parameter.",
        "cl_allow_animated_avatars 0",
    };

    private static readonly Dictionary<string, GameRecipe> Recipes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs2"] = new GameRecipe(
            "cs2",
            Latency,
            4,
            new[] { new RecipeConfigFile("game\\csgo\\cfg\\autoexec.cfg", Cs2ConfigLines) },
            new[] { "cl_allow_animated_avatars 0 是渲染项，不是网络参数。" },
            HasSurface: true),
        ["limbus"] = new GameRecipe("limbus", Throughput, 0, Array.Empty<RecipeConfigFile>(), Array.Empty<string>(), HasSurface: false),
        ["bd2"] = new GameRecipe("bd2", Throughput, 0, Array.Empty<RecipeConfigFile>(), Array.Empty<string>(), HasSurface: false),
        ["apex"] = new GameRecipe("apex", Latency, 0, Array.Empty<RecipeConfigFile>(), Array.Empty<string>(), HasSurface: false),
        ["hunt"] = new GameRecipe("hunt", Latency, 0, Array.Empty<RecipeConfigFile>(), Array.Empty<string>(), HasSurface: false),
        ["dst"] = new GameRecipe("dst", Latency, 0, Array.Empty<RecipeConfigFile>(), Array.Empty<string>(), HasSurface: false),
        ["terraria"] = new GameRecipe("terraria", Latency, 0, Array.Empty<RecipeConfigFile>(), Array.Empty<string>(), HasSurface: false),
    };

    private static readonly Dictionary<string, TuneTier> Tiers = new(StringComparer.Ordinal)
    {
        [Latency] = new TuneTier(Latency, "低延迟（实时）", new[]
        {
            Pair("Nagle", TuneValue.OfBool(true)),
            Pair("InterruptModeration", TuneValue.OfInt(0)),
            Pair("Eee", TuneValue.OfInt(0)),
            Pair("FlowControl", TuneValue.OfInt(0)),
            Pair("AutoTuning", TuneValue.OfText("normal")),
            Pair("Rss", TuneValue.OfText("enabled")),
            Pair("Throttling", TuneValue.OfText("off")),
            Pair("AdapterPower", TuneValue.OfText("disabled")),
            Pair("WifiPower", TuneValue.OfText("max")),
        }),
        [Throughput] = new TuneTier(Throughput, "吞吐优先（下载）", new[]
        {
            Pair("Nagle", TuneValue.OfText("Restore")),
            Pair("InterruptModeration", TuneValue.OfInt(1)),
            Pair("Eee", TuneValue.OfInt(0)),
            Pair("FlowControl", TuneValue.OfInt(0)),
            Pair("AutoTuning", TuneValue.OfText("normal")),
            Pair("Rss", TuneValue.OfText("enabled")),
            Pair("Throttling", TuneValue.OfText("off")),
            Pair("AdapterPower", TuneValue.OfText("disabled")),
            Pair("WifiPower", TuneValue.OfText("max")),
        }),
    };

    private static KeyValuePair<string, TuneValue> Pair(string key, TuneValue value) => new(key, value);

    /// <summary>Get-NetTier：未知档位是调用方 bug，直接抛（发布版同样 throw）。</summary>
    public static TuneTier GetTier(string tierId)
    {
        if (string.IsNullOrEmpty(tierId) || !Tiers.TryGetValue(tierId, out var tier))
        {
            throw new ArgumentException($"未知档位：{tierId}", nameof(tierId));
        }

        return tier;
    }

    public static bool IsKnownTier(string? tierId) => tierId is not null && Tiers.ContainsKey(tierId);

    /// <summary>
    /// Get-NetTierDelta（net-profiles.ps1:159-196）：
    ///   1) From == To → 空 delta（同一档位重跑完全不碰注册表）；
    ///   2) 目标值是 'Restore' → 进 Restore 列表；
    ///   3) 源档位同键同值 → 跳过；
    ///   4) 其余进 Set 列表。
    /// </summary>
    public static TuneDelta Delta(string? fromTierId, string toTierId)
    {
        var target = GetTier(toTierId);
        var set = new List<string>();
        var restore = new List<string>();

        if (!string.IsNullOrEmpty(fromTierId) && string.Equals(fromTierId, toTierId, StringComparison.Ordinal))
        {
            return new TuneDelta(set, restore);
        }

        TuneTier? source = null;
        if (!string.IsNullOrEmpty(fromTierId) && Tiers.TryGetValue(fromTierId, out var known))
        {
            source = known;
        }

        foreach (var pair in target.Values)
        {
            if (pair.Value.IsRestore)
            {
                restore.Add(pair.Key);
                continue;
            }

            if (source?.Get(pair.Key) is { } sourceValue && sourceValue.SameAs(pair.Value))
            {
                continue;
            }

            set.Add(pair.Key);
        }

        return new TuneDelta(set, restore);
    }

    /// <summary>Get-NetRecipe：未知 / 动态 id（steam-&lt;appid&gt;）走保守的实时档，且不写任何配置文件。</summary>
    public static GameRecipe Recipe(string? gameId)
    {
        if (!string.IsNullOrEmpty(gameId) && Recipes.TryGetValue(gameId, out var recipe))
        {
            return recipe;
        }

        return new GameRecipe(gameId ?? string.Empty, Latency, 0, Array.Empty<RecipeConfigFile>(), Array.Empty<string>(), HasSurface: false);
    }

    /// <summary>
    /// Get-NetTunePlan：只做决策、不做 IO。安装根目录由调用方给；给了就把配方里的相对路径
    /// 拼成绝对路径，没给就在 Notes 里说明"不会写配置文件"（发布版对未安装游戏的说法）。
    /// <paramref name="explicitTargets"/> 优先于配方（App 已经算好 CS2 的精确路径时用）。
    /// </summary>
    public static TunePlan Plan(
        string? gameId,
        string? previousTier,
        string? installRoot,
        IReadOnlyList<TuneConfigTarget>? explicitTargets = null)
    {
        var recipe = Recipe(gameId);
        var delta = Delta(previousTier, recipe.Tier);
        var notes = new List<string>(recipe.NonNetworkNotes);
        var writes = new List<TuneConfigTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in explicitTargets ?? Array.Empty<TuneConfigTarget>())
        {
            if (string.IsNullOrWhiteSpace(target.Path) || !seen.Add(target.Path))
            {
                continue;
            }

            writes.Add(target);
        }

        if (recipe.ConfigFiles.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                notes.Add("游戏安装路径未知，本次不写游戏配置文件。");
            }
            else
            {
                foreach (var file in recipe.ConfigFiles)
                {
                    var path = Path.Combine(installRoot, file.RelativePath);
                    if (seen.Add(path))
                    {
                        writes.Add(new TuneConfigTarget(path, file.Lines));
                    }
                }
            }
        }

        if (!recipe.HasSurface)
        {
            notes.Add("这个游戏没有客户端网络参数可写，只应用系统档位。");
        }

        return new TunePlan(recipe.Tier, delta.Set, delta.Restore, writes, notes);
    }

    /// <summary>档位键 → netsh 全局项名与目标值；不是 netsh 项返回 null。</summary>
    public static (string Name, string Value)? TcpGlobalFor(string tierKey, TuneTier tier)
    {
        switch (tierKey)
        {
            case "AutoTuning":
                return tier.Get("AutoTuning") is { } at ? ("autotuninglevel", at.ToString()) : null;
            case "Rss":
                return tier.Get("Rss") is { } rss ? ("rss", rss.ToString()) : null;
            default:
                return null;
        }
    }
}
