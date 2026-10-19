using BatterBabel.Core.Domain;
using BatterBabel.Core.Optimization;

namespace BatterBabel.App.Web;

/// <summary>
/// Web 壳的演示数据源（FIXTURE）。
///
/// 纪律（与 P0 实验一致，进入产品前必须替换）：
///   * 不访问磁盘、不访问网络、不读注册表；所有延迟/速率都是写死的样本或按域名确定性推导；
///   * 游戏目录直接读 <see cref="GameCatalog"/>（生产值，7 个画像 + 平台分组），不另抄一份；
///   * 排名不自己写：候选交 <see cref="ProbeRanking.Summarize"/>，赢家由生产同款算法选出；
///   * 界面文案里明确标 [FIXTURE]，不能让演示数字看起来像真实测速。
/// </summary>
internal static class DemoRoutes
{
    internal const string FixtureNote = "[FIXTURE] 本地固定样本，非真实网络结果";

    /// <summary>显式样本：与 P0 证据里的数字一致，便于对照截图/证据。</summary>
    private static readonly Dictionary<string, (string Address, double[] Latency, double[] Download)[]> ExplicitSamples =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["download.limbuscompanycdn.org"] = new[]
            {
                ("104.16.32.10", new[] { 88.0, 92, 90 }, new[] { 12.4, 13.0, 12.8 }),
                ("104.16.32.11", new[] { 71.0, 69, 70 }, new[] { 16.2, 15.8, 16.4 }),
                ("104.16.32.12", new[] { 120.0, 124, 118 }, new[] { 9.6, 9.9, 9.7 }),
            },
        };

    /// <summary>scan / scan-force 的答案：目录来自 Core，安装/可支持标记是演示口径（未做磁盘发现）。</summary>
    internal static object ScanResult()
    {
        var games = GameCatalog.Games.Select(game =>
        {
            var groups = GameCatalog.GroupsFor(game);
            return new
            {
                id = game.Id,
                name = game.Name,
                // 演示壳没有做磁盘发现：全部按已安装列出，才能看到页面下拉框与卡片。
                installed = true,
                // fixture 里没有任何真实策略，accelerated 必须是 false（不能跟着 installed 一起假成 true）。
                accelerated = false,
                support = groups.Count > 0,
                standalone = game.Standalone,
                helpsGameplay = game.HelpsGameplay,
                optGroups = groups.Select(group => new
                {
                    tag = TagName(group.Tag),
                    label = group.Label,
                    mode = ModeName(group.Mode),
                    domains = group.Domains.ToArray(),
                }).ToArray(),
            };
        }).ToArray();

        return new
        {
            ok = true,
            status = "SUCCEEDED",
            scannedAt = DateTimeOffset.Now.ToString("o"),
            cached = false,
            acceleratedCount = 0,
            fixture = true,
            scanSource = "Core.GameCatalog（未做磁盘发现）",
            games,
            qos = new
            {
                available = false,
                persistent = false,
                reason = "本版 Web 壳只读：未接入 QoS 加速（没有系统写能力）",
                sources = Array.Empty<object>(),
            },
        };
    }

    /// <summary>
    /// run_action cf-optimize：按 gameId 找 Core 画像，逐个域名走 ProbeRanking。
    /// 卡片粒度跟随 0.10.3 原版界面：一个 OptGroup 一张卡 —— cloudflare 分组把组内域名合在一张卡里
    /// 共用一个赢家，probe 分组一张卡 + 每个域名一条 items[]。
    /// </summary>
    internal static object OptimizeResult(string jobId, int sequence, string? gameId)
    {
        const string hostsPath = @"C:\Windows\System32\drivers\etc\hosts";
        var game = !string.IsNullOrEmpty(gameId) ? GameCatalog.Find(gameId) : GameCatalog.Games[0];
        var cards = new List<object>();

        if (game is not null)
        {
            foreach (var group in GameCatalog.GroupsFor(game))
            {
                if (group.Mode == ProbeMode.Cloudflare)
                {
                    var summaries = group.Domains
                        .Select(domain => new
                        {
                            Domain = domain,
                            Summary = ProbeRanking.Summarize(domain, CandidatesFor(domain, group.Mode)),
                        })
                        .ToArray();

                    // 组内域名共用一张卡：先挑出整体最好的域名，再取该域名的前两名候选
                    // （定稿卡片的「两条线路」= 同一线路的两个可用地址）。
                    var bestDomain = summaries
                        .OrderByDescending(entry => entry.Summary.Best?.DownloadMiBps ?? double.MinValue)
                        .ThenBy(entry => entry.Summary.Best?.MedianMs ?? double.MaxValue)
                        .FirstOrDefault();

                    var ranked = (bestDomain?.Summary.Candidates ?? Array.Empty<RouteCandidate>())
                        .Where(candidate => candidate.Address is not null && candidate.DownloadMiBps is not null)
                        .OrderByDescending(candidate => candidate.DownloadMiBps ?? double.MinValue)
                        .ThenBy(candidate => candidate.MedianMs ?? double.MaxValue)
                        .ToArray();

                    var winner = ranked.FirstOrDefault();
                    var pool = summaries.SelectMany(entry => entry.Summary.Candidates).ToArray();
                    var alive = pool.Count(candidate => candidate.Verdict == ProbeVerdict.Usable);
                    var logLines = new List<string>
                    {
                        $"=== {group.Label} ===",
                        $"pool {pool.Length} candidates",
                        $"finalists {ranked.Length}",
                    };
                    if (winner is not null) { logLines.Add($"best {winner.Address}"); }

                    cards.Add(new
                    {
                        tag = TagName(group.Tag),
                        label = group.Label,
                        domains = group.Domains.ToArray(),
                        mode = "cloudflare",
                        bestIp = winner?.Address,
                        downloadMbps = winner?.DownloadMiBps,
                        latency = winner?.MedianMs,
                        ok = winner is not null,
                        fixture = true,
                        log = logLines.ToArray(),
                        verdict = winner is not null ? "Usable" : "Unusable",
                        reason = winner is not null ? null : (bestDomain?.Summary.BestUnavailableReason ?? "no-candidate"),
                        poolSize = pool.Length,
                        aliveCount = alive,
                        finalistCount = ranked.Length,
                        lines = ranked.Take(2).Select(candidate => new
                        {
                            ip = candidate.Address,
                            downloadMbps = candidate.DownloadMiBps,
                            latency = candidate.MedianMs,
                            domain = bestDomain?.Domain,
                        }).ToArray(),
                        candidates = summaries.SelectMany(entry => entry.Summary.Candidates.Select(candidate => new
                        {
                            domain = entry.Domain,
                            address = candidate.Address,
                            latencyMs = candidate.MedianMs,
                            downloadMbps = candidate.DownloadMiBps,
                            verdict = candidate.Verdict.ToString(),
                            rankable = candidate.Rankable,
                            rankReason = candidate.RankReason.ToString(),
                        })).ToArray(),
                    });
                }
                else
                {
                    var items = group.Domains.Select(domain =>
                    {
                        var summary = ProbeRanking.Summarize(domain, CandidatesFor(domain, group.Mode));
                        var best = summary.Best;
                        return new
                        {
                            ok = best is not null,
                            domain,
                            ip = best?.Address,
                            latency = best?.MedianMs,
                            downloadMbps = best?.DownloadMiBps,
                            reason = best is null ? summary.BestUnavailableReason : string.Empty,
                        };
                    }).ToArray();

                    var alive = items.Count(item => item.ok);
                    var probeLog = new List<string>
                    {
                        $"=== {group.Label} ===",
                        $"pool {items.Length} candidates",
                        $"finalists {alive}",
                    };
                    var bestItem = items.FirstOrDefault(item => item.ok);
                    if (bestItem is not null) { probeLog.Add($"best {bestItem.ip}"); }

                    cards.Add(new
                    {
                        tag = TagName(group.Tag),
                        label = group.Label,
                        domains = group.Domains.ToArray(),
                        mode = "probe",
                        items,
                        ok = alive > 0,
                        fixture = true,
                        log = probeLog.ToArray(),
                        verdict = alive > 0 ? "Usable" : "Unusable",
                        reason = alive > 0 ? null : (items.FirstOrDefault()?.reason ?? "no-candidate"),
                        poolSize = items.Length,
                        aliveCount = alive,
                        finalistCount = alive,
                    });
                }
            }
        }

        return new
        {
            ok = true,
            status = "SUCCEEDED",
            jobId,
            sequence,
            percent = 100,
            log = FixtureNote,
            fixture = true,
            hostsPath,
            groups = cards.ToArray(),
        };
    }

    private static RouteCandidate[] CandidatesFor(string domain, ProbeMode mode)
    {
        if (ExplicitSamples.TryGetValue(domain, out var rows))
        {
            return rows.Select(row => Candidate(row.Address, row.Latency, row.Download)).ToArray();
        }

        var hash = StableHash(domain);
        if (mode == ProbeMode.Cloudflare)
        {
            return Enumerable.Range(0, 3).Select(index =>
            {
                var latency = 50 + (hash >> (index * 4)) % 70;
                var download = 8 + (hash >> (index * 3 + 1)) % 18;
                return Candidate(
                    $"104.16.{20 + hash % 200}.{10 + index}",
                    new[] { latency + 2.0, latency, latency - 1.0 },
                    new[] { download + 0.3, download, download - 0.2 });
            }).ToArray();
        }

        // probe 模式：一个域名解析到一个地址，但样本数按 3 次计算（否则 < minSamples 不可排名）。
        var baseLatency = 40 + hash % 60;
        var baseDownload = 8 + (hash >> 2) % 12;
        return new[]
        {
            Candidate(
                $"203.0.113.{1 + hash % 250}",
                new[] { baseLatency + 1.0, baseLatency, baseLatency - 1.0 },
                new[] { baseDownload + 0.4, baseDownload, baseDownload - 0.3 }),
        };
    }

    private static RouteCandidate Candidate(string address, double[] latency, double[]? download) => new(
        Address: address,
        Verdict: ProbeVerdict.Usable,
        MedianMs: ProbeRanking.Median(latency),
        Samples: latency.Length,
        StatusCode: 200,
        DownloadMiBps: download is null ? null : ProbeRanking.Median(download),
        RankReason: RankReason.None,
        Rankable: false);

    private static uint StableHash(string value)
    {
        uint hash = 2166136261;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return hash;
    }

    private static string TagName(RouteTag tag) => tag switch
    {
        RouteTag.Cf => "cf",
        RouteTag.Amazon => "amazon",
        RouteTag.Steam => "steam",
        RouteTag.Ea => "ea",
        RouteTag.Klei => "klei",
        RouteTag.Ubisoft => "ubisoft",
        RouteTag.Epic => "epic",
        RouteTag.Blizzard => "blizzard",
        _ => "unknown",
    };

    private static string ModeName(ProbeMode mode) => mode == ProbeMode.Cloudflare ? "cloudflare" : "probe";
}
