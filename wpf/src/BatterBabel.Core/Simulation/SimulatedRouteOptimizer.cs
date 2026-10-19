namespace BatterBabel.Core.Simulation;

using BatterBabel.Core.Domain;
using BatterBabel.Core.Optimization;
using BatterBabel.Core.Services;

/// <summary>
/// 原型阶段的**模拟**探测器：不发任何网络请求、不读任何系统状态，
/// 用 (gameId, domain) 的稳定哈希生成可复现的候选与延迟，跑通
/// 「ViewModel 驱动 -> 逐候选进度 -> 逐域名结果 -> 卡片刷新」这条链路。
///
/// 明确边界（写进 BB-WPF-PROTOTYPE.md）：
///   * 这里的 IP 数字只是形状真实的占位值，不是真实可用地址；
///   * 结果不能当作"优选有效"的证据，也不能写进 hosts（见 RefusingHostsApplier）。
/// 真实实现将替换这个类，接口不变，ViewModel 不用改 —— 这就是先做原型的目的。
/// </summary>
public sealed class SimulatedRouteOptimizer : IRouteOptimizer
{
    private readonly int _stepDelayMs;

    public SimulatedRouteOptimizer(int stepDelayMs = 45) => _stepDelayMs = stepDelayMs;

    public bool IsSimulated => true;

    public async Task<IReadOnlyList<RouteGroupResult>> OptimizeAsync(
        GameProfile game,
        IProbeObserver observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(observer);

        var groups = GameCatalog.GroupsFor(game);
        var plans = new List<(int GroupIndex, OptGroup Group, string Domain, IReadOnlyList<RouteCandidate> Candidates)>();

        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            foreach (var domain in group.Domains)
            {
                plans.Add((groupIndex, group, domain, BuildCandidates(game.Id, group, domain)));
            }
        }

        var workTotal = plans.Sum(plan => plan.Candidates.Count);
        observer.OnStarted(game, workTotal);

        var workDone = 0;
        var perGroup = new Dictionary<int, List<DomainProbeResult>>();

        foreach (var (groupIndex, group, domain, candidates) in plans)
        {
            var results = new List<RouteCandidate>(candidates.Count);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_stepDelayMs > 0)
                {
                    await Task.Delay(_stepDelayMs, cancellationToken).ConfigureAwait(false);
                }

                workDone++;
                results.Add(candidate);
                observer.OnWorkDone(workDone, workTotal, $"{domain} · {candidate.Address}");
            }

            var summary = ProbeRanking.Summarize(domain, results);
            if (!perGroup.TryGetValue(groupIndex, out var bucket))
            {
                bucket = new List<DomainProbeResult>();
                perGroup[groupIndex] = bucket;
            }

            bucket.Add(summary);
            observer.OnDomainCompleted(groupIndex, summary);
        }

        observer.OnFinished();

        return groups
            .Select((group, index) => new RouteGroupResult(
                group,
                perGroup.TryGetValue(index, out var domains) ? domains : Array.Empty<DomainProbeResult>()))
            .ToArray();
    }

    /// <summary>
    /// 生成一个域名的候选。Cloudflare 模式取官方 anycast 段形状的地址并带下载速率；
    /// Probe 模式取 AWS/CloudFront 形状的地址，只带延迟。
    /// 第一个候选一定 Usable（保证卡片有赢家可看），其后混入 Keep(403 反机器人) 与 Unusable(超时)。
    /// </summary>
    private static IReadOnlyList<RouteCandidate> BuildCandidates(string gameId, OptGroup group, string domain)
    {
        var seed = StableHash($"{gameId}|{group.Label}|{domain}");
        var random = new DeterministicRandom(seed);
        var count = 3 + (int)(random.NextUInt() % 2); // 3 或 4 个候选

        var candidates = new List<RouteCandidate>(count);
        for (var i = 0; i < count; i++)
        {
            var address = AddressFor(group.Mode, random, i);
            var median = 24.0 + random.NextUInt() % 62;      // 24..85 ms
            var samples = 3;

            // 形状真实但**不真实**：末尾一个候选偶尔是反机器人页，第二个偶尔直接超时。
            if (i == count - 1 && random.NextUInt() % 2 == 0)
            {
                candidates.Add(new RouteCandidate(
                    address, ProbeVerdict.Keep, null, 1, 403, null,
                    RankReason.NotUsable, false, "AntiBotPage"));
                continue;
            }

            if (i == 1 && count > 3 && random.NextUInt() % 3 == 0)
            {
                candidates.Add(new RouteCandidate(
                    address, ProbeVerdict.Unusable, null, 0, 0, null,
                    RankReason.NotUsable, false, "Timeout"));
                continue;
            }

            double? download = group.Mode == ProbeMode.Cloudflare
                ? Math.Round(3.5 + random.NextUInt() % 260 / 10.0, 2)   // 3.5..29.5 MiB/s
                : null;

            candidates.Add(new RouteCandidate(
                address, ProbeVerdict.Usable, median, samples, 200, download,
                RankReason.None, false, "None"));
        }

        return candidates;
    }

    private static string AddressFor(ProbeMode mode, DeterministicRandom random, int index)
    {
        if (mode == ProbeMode.Cloudflare)
        {
            // Cloudflare 官方 anycast 段（104.16.0.0/13、172.64.0.0/13）的形状。
            return index % 2 == 0
                ? $"104.{16 + random.NextUInt() % 16}.{random.NextUInt() % 256}.{random.NextUInt() % 256}"
                : $"172.{64 + random.NextUInt() % 8}.{random.NextUInt() % 256}.{random.NextUInt() % 256}";
        }

        // AWS / CloudFront 常见前缀的形状。
        return (index % 3) switch
        {
            0 => $"13.{32 + random.NextUInt() % 32}.{random.NextUInt() % 256}.{random.NextUInt() % 256}",
            1 => $"18.{random.NextUInt() % 256}.{random.NextUInt() % 256}.{random.NextUInt() % 256}",
            _ => $"143.204.{random.NextUInt() % 256}.{random.NextUInt() % 256}",
        };
    }

    /// <summary>FNV-1a：跨进程/跨运行稳定，保证「同一游戏同一域名」每次生成同样的数据。</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 16777619u;
        }

        return hash;
    }

    /// <summary>xorshift32：只依赖自身状态的确定性随机数（不用 System.Random，避免实现差异）。</summary>
    private sealed class DeterministicRandom
    {
        private uint _state;

        public DeterministicRandom(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

        public uint NextUInt()
        {
            var x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }
    }
}
