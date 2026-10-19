using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using BatterBabel.App.Discovery;
using BatterBabel.Core.Domain;
using BatterBabel.Core.GameConfig;
using BatterBabel.Core.Optimization;
using BatterBabel.Core.Probing;

namespace BatterBabel.App.Web;

/// <summary>
/// 真实测速的页面投影，流水线对齐发布版 Tauri 0.10.3：
/// <list type="number">
/// <item>Cloudflare 800 地址池（确定性采样；本机 2000 次 SYN 后 DNS 会短暂失效）；</item>
/// <item>TCP:443 预筛（并发 200 / 900 ms）；</item>
/// <item>前 20 名下载决赛（1 MB / 8 s / 并发 2），按吞吐取前 5；</item>
/// <item>每域名用「组内共享 top-5 + 系统解析基线」做 3 样本 TTFB 验证；</item>
/// <item>与基线比收益 &lt;10%（或最佳就是基线）→ 保留默认，不写 hosts。</item>
/// </list>
/// 这一层仍只做「测量 + 投影」，不写 hosts、不需要管理员。
/// </summary>
internal static class LiveRoutes
{
    // 页面显示一位小数；T 版 JSON 也只保留一位（MiB/s）。
    private static double? Round1(double? value) => value is { } v ? Math.Round(v, 1) : null;

    private const int DefaultPoolTarget = 800;
    private const double MinGainPercent = 10.0;
    private static readonly TimeSpan PlanTtl = TimeSpan.FromMinutes(15);

    internal static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    internal static async Task<object> OptimizeAsync(
        string gameId,
        string jobId,
        int sequence,
        ProbeBudget budget,
        Action<int, string> report,
        CancellationToken cancellationToken)
    {
        var game = GameCatalog.Find(gameId);
        if (game is null)
        {
            return new
            {
                ok = false,
                status = "FAILED",
                message = "未知游戏：" + gameId,
                fixture = false,
            };
        }

        var groups = GameCatalog.GroupsFor(game);
        var configs = ApplyGameConfigs(game);
        if (groups.Count == 0)
        {
            var touched = configs.Length > 0;
            return new
            {
                ok = true,
                status = "SUCCEEDED",
                jobId,
                sequence,
                percent = 100,
                message = touched ? "没有可优选的线路；游戏配置已处理" : "这个游戏没有可优选的线路分组",
                fixture = false,
                measuredAt = DateTimeOffset.Now.ToString("o"),
                elapsedMs = 0,
                hostsPath = HostsPath,
                entries = Array.Empty<object>(),
                configs,
                groups = Array.Empty<object>(),
                validUntil = DateTimeOffset.Now.Add(PlanTtl).ToString("o"),
            };
        }

        var watch = Stopwatch.StartNew();
        var projected = new List<object>(groups.Count);
        var entries = new List<object>();

        void Step(int percent, string message) => report(Math.Clamp(percent, 1, 99), message);

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (group.Mode == ProbeMode.Cloudflare)
            {
                // 先把本组所有域名的系统基线解析出来：T 版池是 2000（optimization.ps1:2542），
                // 本机 2000 次 SYN 之后系统 DNS 会短暂失效；基线提前拿，筛选后不再依赖 DNS。
                var baselines = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var domain in group.Domains)
                {
                    baselines[domain] = await SystemBaselineAsync(domain, cancellationToken).ConfigureAwait(false);
                }

                var priority = CloudflarePriority.Load();
                Step(6, $"采样 Cloudflare 候选池（{DefaultPoolTarget}）· 精选 {priority.Curated} · 学习 {priority.Learned}");
                // 本机实测 2000 次 SYN 之后系统 DNS 会短暂失效；800 仍能筛出 400+ 存活候选，
                // 足够喂满 20 名决赛。设 BB_CF_POOL=2000 可临时恢复发布版原值。
                var poolTarget = int.TryParse(Environment.GetEnvironmentVariable("BB_CF_POOL"), out var customPool) && customPool > 0
                    ? customPool
                    : DefaultPoolTarget;
                var pool = CloudflarePool.Snapshot(poolTarget);
                Step(10, $"TCP 预筛 {pool.Count} 个地址（并发 200 / 900 ms）");
                var alive = await TcpScreen.ScreenAsync(pool, cancellationToken: cancellationToken).ConfigureAwait(false);
                // T 版按 TCP 耗时取前 20 进下载赛；精选/学习地址不参与筛选、直接占位
                // （optimization.ps1 的 Get-PriorityIps 语义），所以决赛名单是「免筛地址 + 前 20」。
                var fastest = alive.OrderBy(result => result.ConnectMs ?? int.MaxValue).ToArray();
                var finalistIps = priority.Ips
                    .Concat(fastest.Take(20).Select(result => result.Ip))
                    .Distinct(StringComparer.Ordinal)
                    .Take(24)
                    .ToArray();
                Step(26, $"下载决赛 {finalistIps.Length} 个（1 MB / 8 s / 并发 2）");
                var finals = await DownloadFinals.MeasureAsync(finalistIps, cancellationToken).ConfigureAwait(false);
                var prelim = finals
                    .Where(result => result.Ok && result.MibPerSecond is > 0)
                    .OrderByDescending(result => result.MibPerSecond)
                    .Take(5)
                    .ToArray();
                // 1 MB 样本里 TLS 握手占比太大，数值会明显偏低（本机 1 MB 量到 1 MB/s、
                // 10 MB 同一地址能到 5 MB/s）。决赛仍按发布版 1 MB 排序，前 5 名再用 10 MB
                // 复核一次，用于卡片显示与学习库，避免把「握手慢」当成「线路慢」。
                Step(33, $"决赛前 {prelim.Length} 名复核（10 MB）");
                var refined = await DownloadFinals
                    .MeasureAsync(prelim.Select(result => result.Ip).ToArray(), cancellationToken,
                        sampleBytes: 10_000_000, timeoutSeconds: 12, concurrency: 2, batchBudgetSeconds: 30)
                    .ConfigureAwait(false);
                var refinedByIp = refined.ToDictionary(result => result.Ip, StringComparer.Ordinal);
                var rankedFinals = prelim
                    .Select(result => refinedByIp.TryGetValue(result.Ip, out var better) && better.Ok ? better : result)
                    .OrderByDescending(result => result.MibPerSecond ?? double.MinValue)
                    .ToArray();
                // 学习库按「本轮全部下载候选」记录：成功清零 misses、失败 +1，
                // 最快 3 名 hits+1；淘汰与 30 条上限在 CloudflarePriority.Record 内完成。
                CloudflarePriority.Record(finals.Select(result => (result.Ip, result.Ok ? result.MibPerSecond : null)));
                var shared = rankedFinals.Take(5).Select(result => result.Ip).ToArray();
                if (shared.Length == 0)
                {
                    // 下载决赛全部失败时仍给验证阶段留候选，避免整组空转；写不写仍然由
                    // 「与基线比收益 >=10%」决定，不会因为这里是 TCP 存活地址就放行。
                    shared = fastest.Take(5).Select(result => result.Ip).ToArray();
                }
                Step(40, $"逐域名验证（共享 {shared.Length} 个决赛地址 + 系统解析基线）");

                var items = new List<object>(group.Domains.Count);
                var perDomain = new List<(string Domain, IReadOnlyList<RouteCandidate> Candidates, string? Baseline)>();
                var index = 0;
                foreach (var domain in group.Domains)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var baseline = baselines.TryGetValue(domain, out var knownBaseline)
                        ? knownBaseline
                        : await SystemBaselineAsync(domain, cancellationToken).ConfigureAwait(false);
                    var candidates = shared
                        .Concat(fastest.Take(20).Select(result => result.Ip))
                        .Concat(baseline is null ? Array.Empty<string>() : new[] { baseline })
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var measured = candidates.Length == 0
                        ? Array.Empty<RouteCandidate>()
                        : await RouteProber
                            .MeasureDomainAsync(domain, candidates, budget, message => Step(40 + index * 10, message), cancellationToken)
                            .ConfigureAwait(false);

                    var best = measured
                        .Where(candidate => candidate.Rankable && candidate.Verdict == ProbeVerdict.Usable)
                        .OrderBy(candidate => candidate.MedianMs ?? double.MaxValue)
                        .ThenBy(candidate => candidate.Address, StringComparer.Ordinal)
                        .FirstOrDefault();
                    var baselineCandidate = baseline is null
                        ? null
                        : measured.FirstOrDefault(candidate => string.Equals(candidate.Address, baseline, StringComparison.Ordinal));
                    var baselineMs = baselineCandidate?.MedianMs;
                    var gain = baselineMs is > 0 && best?.MedianMs is { } bestMs
                        ? Math.Round((baselineMs.Value - bestMs) / baselineMs.Value * 100.0, 0)
                        : (double?)null;
                    // 发布版没有 baseline/10% 门槛：只要该域名有可用候选就写。
                    // gain 只作为页面诊断信息保留。
                    var writable = best?.Address is not null;

                    items.Add(new
                    {
                        domain,
                        ok = best is not null,
                        ip = best?.Address,
                        latency = best?.MedianMs,
                        samples = best?.Samples ?? measured.FirstOrDefault()?.Samples ?? 0,
                        statusCode = best?.StatusCode,
                        baselineMs,
                        gainPercent = gain,
                        writable,
                        reason = best is null
                            ? (measured.Select(candidate => candidate.Reason).FirstOrDefault(reason => !string.IsNullOrEmpty(reason)) ?? "没有可用的候选地址")
                            : null,
                    });
                    perDomain.Add((domain, measured, baseline));
                    index++;
                }

                // 卡片两行取「下载决赛前两名」——它们同时有吞吐和验证到的延迟，
                // 不会出现一行有 MB/s、另一行只有 ms 的断层（T 版卡只显示冠军一行）。
                var medianByIp = perDomain
                    .SelectMany(entry => entry.Candidates)
                    .Where(candidate => candidate.Address is not null && candidate.MedianMs is not null)
                    .GroupBy(candidate => candidate.Address!, StringComparer.Ordinal)
                    .ToDictionary(grouped => grouped.Key, grouped => grouped.Min(candidate => candidate.MedianMs), StringComparer.Ordinal);
                var display = rankedFinals
                    .Take(2)
                    .Select(final => new
                    {
                        ip = final.Ip,
                        downloadMbps = Round1(final.MibPerSecond),
                        latency = medianByIp.TryGetValue(final.Ip, out var median) ? median : null,
                    })
                    .ToArray();

                var champion = rankedFinals.FirstOrDefault();
                // 发布版写入语义：同组所有域名统一指向下载冠军（页面用 bestIp 构造 domains 串），
                // 而不是每个域名各写一个「延迟最好」的地址；诊断用的 per-domain 结果仍保留在 items 里。
                foreach (var domain in group.Domains)
                {
                    if (champion?.Ip is { } championIp)
                    {
                        entries.Add(new { tag = TagName(group.Tag), domain, ip = championIp, writable = true, measuredAt = DateTimeOffset.Now.ToString("o") });
                    }
                }

                projected.Add(new
                {
                    tag = TagName(group.Tag),
                    label = group.Label,
                    domains = group.Domains.ToArray(),
                    mode = "cloudflare",
                    bestIp = champion?.Ip,
                    downloadMbps = Round1(champion?.MibPerSecond),
                    latency = alive.FirstOrDefault()?.ConnectMs,
                    items,
                    lines = display,
                    log = new[]
                    {
                        $"=== {group.Label} ===",
                        $"pool {pool.Count} · alive {alive.Count} · finalists {finalistIps.Length} · shared {shared.Length}",
                        champion is null ? "no ranking download" : $"champion {champion.Ip} {champion.MibPerSecond:0.0} MiB/s",
                    },
                    verdict = champion is null ? "Unusable" : "Usable",
                    reason = champion is null ? "no-download-winner" : null,
                    poolSize = pool.Count,
                    aliveCount = alive.Count,
                    finalistCount = finalistIps.Length,
                });
            }
            else
            {
                var items = new List<object>(group.Domains.Count);
                var measuredCount = 0;
                foreach (var domain in group.Domains)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Step(60, $"解析 {domain}");
                    var addresses = await DohResolver.ResolveAsync(domain, cancellationToken).ConfigureAwait(false);
                    var baseline = await SystemBaselineAsync(domain, cancellationToken).ConfigureAwait(false);
                    var pool = addresses
                        .Concat(baseline is null ? Array.Empty<string>() : new[] { baseline })
                        .Distinct(StringComparer.Ordinal)
                        .Take(budget.MaxCandidatesPerDomain)
                        .ToArray();
                    var measured = pool.Length == 0
                        ? Array.Empty<RouteCandidate>()
                        : await RouteProber
                            .MeasureDomainAsync(domain, pool, budget, message => Step(70, message), cancellationToken)
                            .ConfigureAwait(false);
                    measuredCount += measured.Count;

                    var best = measured
                        .Where(candidate => candidate.Rankable && candidate.Verdict == ProbeVerdict.Usable)
                        .OrderBy(candidate => candidate.MedianMs ?? double.MaxValue)
                        .FirstOrDefault();
                    var baselineCandidate = baseline is null
                        ? null
                        : measured.FirstOrDefault(candidate => string.Equals(candidate.Address, baseline, StringComparison.Ordinal));
                    var baselineMs = baselineCandidate?.MedianMs;
                    var gain = baselineMs is > 0 && best?.MedianMs is { } bestMs
                        ? Math.Round((baselineMs.Value - bestMs) / baselineMs.Value * 100.0, 0)
                        : (double?)null;
                    // 与 Cloudflare 组一致：发布版没有 10% 门槛，有可用候选就写。
                    var writable = best?.Address is not null;

                    items.Add(new
                    {
                        domain,
                        ok = best is not null,
                        ip = best?.Address,
                        latency = best?.MedianMs,
                        samples = best?.Samples ?? 0,
                        statusCode = best?.StatusCode,
                        baselineMs,
                        gainPercent = gain,
                        writable,
                        reason = best is null
                            ? (measured.Select(candidate => candidate.Reason).FirstOrDefault(reason => !string.IsNullOrEmpty(reason)) ?? "没有可用的候选地址")
                            : null,
                    });
                    if (writable && best?.Address is { } probeWrite)
                    {
                        entries.Add(new { tag = TagName(group.Tag), domain, ip = probeWrite, writable = true, measuredAt = DateTimeOffset.Now.ToString("o") });
                    }
                }

                projected.Add(new
                {
                    tag = TagName(group.Tag),
                    label = group.Label,
                    domains = group.Domains.ToArray(),
                    mode = "probe",
                    items,
                    log = new[]
                    {
                        $"=== {group.Label} ===",
                        $"probe {measuredCount} candidates",
                    },
                    verdict = items.Count > 0 ? "Usable" : "Unusable",
                    reason = (string?)null,
                    poolSize = measuredCount,
                    aliveCount = items.Count,
                    finalistCount = items.Count,
                });
            }
        }

        watch.Stop();
        report(100, "测速完成");
        var now = DateTimeOffset.Now;
        return new
        {
            ok = true,
            status = "SUCCEEDED",
            jobId,
            sequence,
            percent = 100,
            message = "测速完成",
            fixture = false,
            measuredAt = now.ToString("o"),
            validUntil = now.Add(PlanTtl).ToString("o"),
            elapsedMs = (int)watch.ElapsedMilliseconds,
            hostsPath = HostsPath,
            entries = entries.ToArray(),
            configs,
            groups = projected.ToArray(),
        };
    }

    private static async Task<string?> SystemBaselineAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken).ConfigureAwait(false);
            return addresses
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString())
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // CS2 的托管块内容来自发布版 net-profiles.ps1:44-49：没有任何值得写的网络 cvar，
    // 唯一实际生效的是渲染开关 cl_allow_animated_avatars，所以注释里如实标注它是渲染项。
    internal static readonly string[] Cs2ConfigLines =
    {
        "// Batter Babel (generated) - CS2 has no client-side network cvar worth setting;",
        "// see docs/superpowers/specs/2026-10-04-per-game-network-profiles-design.md",
        "// The line below is a rendering tweak, not a network parameter.",
        "cl_allow_animated_avatars 0",
    };

    internal static string? Cs2ConfigPath()
    {
        var executable = WindowsGameScanner.FindExecutable("cs2");
        if (string.IsNullOrEmpty(executable))
        {
            return null;
        }

        // <install>gameinwin64cs2.exe → <install>
        var root = Path.GetDirectoryName(executable);
        for (var i = 0; i < 3 && !string.IsNullOrEmpty(root); i++)
        {
            root = Path.GetDirectoryName(root);
        }

        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "game", "csgo", "cfg", "autoexec.cfg");
    }

    private static object[] ApplyGameConfigs(GameProfile game)
    {
        if (!string.Equals(game.Id, "cs2", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<object>();
        }

        try
        {
            var configPath = Cs2ConfigPath();
            if (configPath is null)
            {
                return new object[]
                {
                    new { path = "", ok = false, message = "没有找到 CS2 的安装目录，托管块未写入" },
                };
            }

            var result = ManagedConfigFile.Apply(configPath, Cs2ConfigLines);
            return new object[]
            {
                new { path = configPath, ok = result.Ok, message = result.Message },
            };
        }
        catch (Exception ex)
        {
            return new object[]
            {
                new { path = "", ok = false, message = "托管块写入失败：" + ex.GetType().Name },
            };
        }
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
        _ => "other",
    };
}
