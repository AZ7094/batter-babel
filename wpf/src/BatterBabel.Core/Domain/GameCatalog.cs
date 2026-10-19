namespace BatterBabel.Core.Domain;

/// <summary>
/// 游戏目录与平台线路，逐条摘自 scripts/optimization.ps1 的 $Catalog（:60-120）、
/// $PlatformTargets（:808-845）与 $GamePlatforms（:850-856）。
///
/// 这里是「A 类可复用：资源/配置/测试数据」的直接落地 —— 域名、标签、模式都是生产值，
/// 不是原型随手编的示例数据。Steam 平台被生产代码**故意排除**，此处同样不收录。
/// </summary>
public static class GameCatalog
{
    private static OptGroup G(string label, RouteTag tag, ProbeMode mode, params string[] domains) =>
        new(label, tag, mode, domains);

    /// <summary>与 $Catalog 完全对齐的 7 个画像，顺序即界面下拉框顺序。</summary>
    public static IReadOnlyList<GameProfile> Games { get; } = new[]
    {
        new GameProfile(
            "limbus", "Limbus Company", "1973530", "LimbusCompany.exe", "LimbusCompany.exe", "Steam",
            new[]
            {
                G("Cloudflare (download CDN)", RouteTag.Cf, ProbeMode.Cloudflare,
                    "download.limbuscompanycdn.org",
                    "downloadcommon.limbuscompanycdn.org",
                    "downloadfmod.limbuscompanycdn.org"),
                G("CloudFront (API)", RouteTag.Amazon, ProbeMode.Probe,
                    "www.limbuscompanyapi.com",
                    "notice.limbuscompanyapi.com"),
            },
            HelpsGameplay: true),

        // 对局走 Steam Datagram Relay，域名无关；Steam 端点被故意排除 -> 没有可优选目标，但 QoS 仍可达。
        new GameProfile("cs2", "Counter-Strike 2", "730", "cs2.exe", @"game\bin\win64\cs2.exe", "Steam",
            Array.Empty<OptGroup>()),

        new GameProfile("apex", "Apex Legends", "1172470", "r5apex.exe", "r5apex.exe", "Steam / EA app",
            new[]
            {
                G("EA Account / sign-in", RouteTag.Ea, ProbeMode.Probe, "accounts.ea.com", "signin.ea.com"),
                G("EA CDN", RouteTag.Ea, ProbeMode.Probe, "origin-a.akamaihd.net"),
            }),

        new GameProfile("hunt", "Hunt: Showdown 1896", "594650", "HuntGame.exe", @"bin\win_x64\HuntGame.exe", "Steam",
            Array.Empty<OptGroup>()),

        new GameProfile("dst", "Don't Starve Together", "322330", "dontstarve_steam_x64.exe",
            @"bin64\dontstarve_steam_x64.exe", "Steam / Klei",
            new[]
            {
                G("Klei lobby / account", RouteTag.Klei, ProbeMode.Probe,
                    "lobby.klei.com", "accounts.klei.com", "login.klei.com"),
            }),

        new GameProfile("terraria", "Terraria", "105600", "Terraria.exe", "Terraria.exe", "Steam / Re-Logic",
            new[]
            {
                G("Official site / services", RouteTag.Cf, ProbeMode.Probe, "terraria.org", "re-logic.com"),
            }),

        // 独立发行，无 Steam AppId / 无安装检测：优化只需要域名。
        new GameProfile("bd2", "Brown Dust 2", "", "BrownDust2.exe", "", "Neowiz / STOVE",
            new[]
            {
                G("Game CDN (assets / patches)", RouteTag.Amazon, ProbeMode.Probe,
                    "browndust2.com", "browndust2.jp", "cdn.neowiz.com"),
                G("STOVE platform", RouteTag.Amazon, ProbeMode.Probe, "onstove.com"),
            },
            Standalone: true, HelpsGameplay: true),
    };

    /// <summary>$PlatformTargets：只有这 4 个平台会被合并进非画像游戏（Steam 故意缺席）。</summary>
    private static readonly Dictionary<string, OptGroup[]> PlatformGroups = new(StringComparer.Ordinal)
    {
        ["Ubisoft"] = new[]
        {
            G("Ubisoft Connect (login / services)", RouteTag.Ubisoft, ProbeMode.Probe, "ubisoftconnect.com"),
            G("Ubisoft sites", RouteTag.Ubisoft, ProbeMode.Probe, "ubisoft.com", "www.ubisoft.com"),
        },
        ["Epic"] = new[]
        {
            G("Epic Online Services", RouteTag.Epic, ProbeMode.Probe, "epicgames.com"),
            G("Epic store / sites", RouteTag.Epic, ProbeMode.Probe, "www.epicgames.com"),
        },
        ["Blizzard"] = new[]
        {
            G("Battle.net", RouteTag.Blizzard, ProbeMode.Probe, "us.battle.net"),
            G("Blizzard sites", RouteTag.Blizzard, ProbeMode.Probe, "blizzard.com"),
        },
        ["EA"] = new[]
        {
            G("EA account / sign-in", RouteTag.Ea, ProbeMode.Probe, "accounts.ea.com", "signin.ea.com"),
            G("EA CDN", RouteTag.Ea, ProbeMode.Probe, "origin-a.akamaihd.net"),
        },
    };

    /// <summary>$GamePlatforms：只有例外才登记，其余游戏没有额外平台客户端。</summary>
    private static readonly Dictionary<string, string> GamePlatforms = new(StringComparer.Ordinal)
    {
        ["359550"] = "Ubisoft",   // Rainbow Six Siege
        ["1938090"] = "Blizzard", // Call of Duty
    };

    /// <summary>与 Get-PlatformForApp 同义：没有已知发行商客户端时返回空串（而不是 Steam）。</summary>
    public static string PlatformForApp(string appId) =>
        !string.IsNullOrEmpty(appId) && GamePlatforms.TryGetValue(appId, out var platform) ? platform : string.Empty;

    /// <summary>画像自带分组 + 平台分组（去重，保持画像顺序在前）。</summary>
    public static IReadOnlyList<OptGroup> GroupsFor(GameProfile game)
    {
        var platform = PlatformForApp(game.AppId);
        if (platform.Length == 0 || !PlatformGroups.TryGetValue(platform, out var extra))
        {
            return game.OptGroups;
        }

        var merged = new List<OptGroup>(game.OptGroups);
        foreach (var group in extra)
        {
            if (merged.All(existing => existing.Label != group.Label))
            {
                merged.Add(group);
            }
        }

        return merged;
    }

    public static GameProfile? Find(string id) =>
        Games.FirstOrDefault(game => string.Equals(game.Id, id, StringComparison.Ordinal));
}
