namespace BatterBabel.Core.Optimization;

using BatterBabel.Core.Domain;

/// <summary>一个候选的排名注解，对应 cf-probe.ps1 的 Get-ProbeResultRank 返回值。</summary>
public readonly record struct CandidateRank(bool Rankable, RankReason Reason, int Group, double SortValue);

/// <summary>
/// 排序算法的 C# 复刻，一手来源 scripts/cf-probe.ps1：
///   Get-ProbeMedian        :626-638
///   Get-ProbeResultRank    :640-683
///   Get-ProbeRankedResults :685-729
///   Select-ProbeBest       :731-745
///
/// 生产语义（逐条照搬，不做"优化"）：
///   * 中位数：过滤掉非正/非有限样本后升序取值；偶数个取中间两个的平均；[math]::Round 到整数（银行家舍入）。
///   * 可排名：Verdict == Usable 且中位数有效 且 samples &gt;= MinSamples(默认 2)。
///   * 排序键：group(Usable 0 / Keep 1 / Unusable 2) -> 可排名优先 -> 中位数（无效时为 double.MaxValue，
///     不是 0，旧版 null 强转 0 会造出"幽灵 0ms 冠军"）-> 原始候选顺序。
///   * 赢家：可排名候选里中位数最小者，严格小于，等值保留第一个；全不可排名返回 null
///     （调用方因此不写 hosts，保留系统解析）。
/// </summary>
public static class ProbeRanking
{
    public const int DefaultMinSamples = 2;

    public static bool IsValidLatencySample(double? value) =>
        value is { } number && !double.IsNaN(number) && !double.IsInfinity(number) && number > 0;

    /// <summary>中位数；没有任何有效样本时返回 null（绝不返回 0）。</summary>
    public static double? Median(IEnumerable<double> values)
    {
        var clean = values.Where(v => IsValidLatencySample(v)).OrderBy(v => v).ToArray();
        if (clean.Length == 0)
        {
            return null;
        }

        var mid = clean.Length / 2;
        if (clean.Length % 2 == 1)
        {
            return Math.Round(clean[mid]);
        }

        return Math.Round((clean[mid - 1] + clean[mid]) / 2.0);
    }

    public static CandidateRank RankOf(RouteCandidate result, int minSamples = DefaultMinSamples)
    {
        var group = result.Verdict switch
        {
            ProbeVerdict.Usable => 0,
            ProbeVerdict.Keep => 1,
            _ => 2,
        };

        var hasMedian = IsValidLatencySample(result.MedianMs);
        var rankable = false;
        var reason = RankReason.NotUsable;

        if (result.Verdict == ProbeVerdict.Usable)
        {
            if (!hasMedian)
            {
                reason = RankReason.NoValidLatencySamples;
            }
            else if (result.Samples < minSamples)
            {
                reason = RankReason.InsufficientSamples;
            }
            else
            {
                rankable = true;
                reason = RankReason.None;
            }
        }

        return new CandidateRank(rankable, reason, group, hasMedian ? result.MedianMs!.Value : double.MaxValue);
    }

    /// <summary>
    /// 给每个候选补上 rankable / rankReason，并按生产顺序返回。
    /// 排名原因会作为面向用户的回退说明，因此只在候选自身没有原因时才顶替它。
    /// </summary>
    public static IReadOnlyList<RouteCandidate> Ranked(IEnumerable<RouteCandidate> results, int minSamples = DefaultMinSamples)
    {
        var annotated = new List<(RouteCandidate Candidate, CandidateRank Rank, int Order)>();
        var order = 0;

        foreach (var result in results)
        {
            var rank = RankOf(result, minSamples);
            var reason = result.Reason;
            if (rank.Reason is not RankReason.NotUsable and not RankReason.None &&
                (string.IsNullOrEmpty(reason) || reason == "None"))
            {
                reason = rank.Reason.ToString();
            }

            annotated.Add((
                result with { Rankable = rank.Rankable, RankReason = rank.Reason, Reason = reason },
                rank,
                order));
            order++;
        }

        return annotated
            .OrderBy(entry => entry.Rank.Group)
            .ThenBy(entry => entry.Rank.Rankable ? 0 : 1)
            .ThenBy(entry => entry.Rank.SortValue)
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Candidate)
            .ToArray();
    }

    /// <summary>唯一赢家：可排名且中位数最小；没有任何合格候选时返回 null。</summary>
    public static RouteCandidate? SelectBest(IEnumerable<RouteCandidate> results, int minSamples = DefaultMinSamples)
    {
        RouteCandidate? best = null;
        var bestValue = double.MaxValue;

        foreach (var result in results)
        {
            var rank = RankOf(result, minSamples);
            if (!rank.Rankable)
            {
                continue;
            }

            if (rank.SortValue < bestValue)
            {
                bestValue = rank.SortValue;
                best = result;
            }
        }

        return best;
    }

    /// <summary>把一个域名的全部候选整理成界面/日志用的结果对象。</summary>
    public static DomainProbeResult Summarize(string domain, IEnumerable<RouteCandidate> rawResults, int minSamples = DefaultMinSamples)
    {
        var materialized = rawResults.ToArray();
        var best = SelectBest(materialized, minSamples);
        return new DomainProbeResult(domain, Ranked(materialized, minSamples), best);
    }
}
