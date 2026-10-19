using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BatterBabel.Core.Probing;

/// <summary>
/// 精选地址 + 学习库，对齐发布版 <c>optimization.ps1:2295-2345</c>（<c>Get-PriorityIps</c>）：
/// 精选地址默认跳过延迟筛选直接进下载决赛（连续 2 次失败才在本轮降级），
/// 学习库里命中 ≥2 次且未连续失败的地址同样免筛。
/// </summary>
public static class CloudflarePriority
{
    /// <summary><c>$CfCandidateIps</c>，顺序与发布版一致。</summary>
    public static readonly IReadOnlyList<string> Curated = new[]
    {
        "172.64.229.1",
        "108.162.192.1",
        "104.16.0.1",
        "104.24.0.1",
        "172.66.0.1",
        "162.159.140.220",
        "104.16.132.229",
        "104.17.105.123",
        "104.17.112.28",
        "172.67.0.1",
    };

    public sealed record Entry(int Hits, int Misses, double? BestMbps);

    public sealed record Plan(IReadOnlyList<string> Ips, int Curated, int Learned, int Demoted)
    {
        public static readonly Plan Empty = new(Array.Empty<string>(), 0, 0, 0);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatterBabel",
        "cf-good-ips.json");

    public static Plan Load(int maxLearned = 10, string? path = null)
    {
        var table = ReadAll(path);
        var liveCurated = new List<string>();
        var demoted = 0;
        foreach (var ip in Curated)
        {
            if (table.TryGetValue(ip, out var record) && record.Misses >= 2)
            {
                demoted++;
                continue;
            }
            liveCurated.Add(ip);
        }

        var learned = table
            .Where(pair => pair.Value.Hits >= 2 && pair.Value.Misses < 2)
            .OrderByDescending(pair => pair.Value.Hits)
            .ThenByDescending(pair => pair.Value.BestMbps ?? 0)
            .Take(maxLearned)
            .Select(pair => pair.Key)
            .ToArray();

        var ips = liveCurated.Concat(learned).Distinct(StringComparer.Ordinal).ToArray();
        return new Plan(ips, liveCurated.Count, learned.Length, demoted);
    }

    /// <summary>
    /// 学习库的「入选 / 淘汰」与发布版 <c>Update-LearnedGoodIps</c>（optimization.ps1:1348-1432）一致：
    /// <list type="bullet">
    /// <item>只下载测过的地址参与；未被记录过的地址只有「赢过」（Mbps 非空）才会入库，
    ///       避免探索期的失败把真正的好地址挤出名额；</item>
    /// <item>成功一次把 misses 清零；失败一次 misses+1；</item>
    /// <item>本轮最快前 3 名 hits+1，并刷新 bestMbps / lastSeen；</item>
    /// <item>misses ≥ 2 淘汰（精选地址保留记录，避免「删掉→又免筛→再失败」的死循环）；</item>
    /// <item>只保留精选地址与「至少赢过一次」的地址，按 hits*100+bestMbps 排序取前 30。</item>
    /// </list>
    /// </summary>
    public static void Record(IEnumerable<(string Ip, double? Mbps)> rows, string? path = null)
    {
        try
        {
            var table = ReadAll(path);
            var now = DateTimeOffset.Now.ToString("o");
            var ordered = rows.Where(row => IsIpv4(row.Ip)).ToArray();
            if (ordered.Length == 0) { return; }

            foreach (var (ip, mbps) in ordered)
            {
                if (!table.TryGetValue(ip, out var record))
                {
                    if (mbps is null) { continue; }
                    record = new Entry(0, 0, null);
                    table[ip] = record;
                }
                table[ip] = mbps is null
                    ? record with { Misses = Math.Min(99, record.Misses + 1) }
                    : record with { Misses = 0 };
            }

            foreach (var (ip, mbps) in ordered
                         .Where(row => row.Mbps is not null)
                         .OrderByDescending(row => row.Mbps)
                         .Take(3))
            {
                var record = table[ip];
                var best = record.BestMbps is { } previous && mbps is { } fresh ? Math.Max(previous, fresh) : mbps;
                table[ip] = record with { Hits = record.Hits + 1, Misses = 0, BestMbps = best };
            }

            var keep = new List<(string Ip, Entry Record, double Score)>();
            foreach (var pair in table)
            {
                var curated = Curated.Contains(pair.Key, StringComparer.Ordinal);
                if (pair.Value.Misses >= 2 && !curated) { continue; }
                if (pair.Value.Hits <= 0 && !curated) { continue; }
                var score = pair.Value.Hits * 100.0 + (pair.Value.BestMbps ?? 0);
                keep.Add((pair.Key, pair.Value, score));
            }

            var top = keep.OrderByDescending(item => item.Score).Take(30).ToArray();
            var document = new
            {
                version = 1,
                updatedAt = now,
                ips = top.ToDictionary(
                    item => item.Ip,
                    item => new { hits = item.Record.Hits, misses = item.Record.Misses, bestMbps = item.Record.BestMbps, lastSeen = item.Record.Hits > 0 ? now : null }),
            };
            var target = path ?? DefaultPath;
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
            var temporary = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, target, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Dictionary<string, Entry> ReadAll(string? path)
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var target = path ?? DefaultPath;
        if (!File.Exists(target)) { return result; }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(target));
            if (!document.RootElement.TryGetProperty("ips", out var ips) || ips.ValueKind != JsonValueKind.Object) { return result; }
            foreach (var property in ips.EnumerateObject())
            {
                var hits = property.Value.TryGetProperty("hits", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : 0;
                var misses = property.Value.TryGetProperty("misses", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
                double? best = property.Value.TryGetProperty("bestMbps", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : null;
                result[property.Name] = new Entry(hits, misses, best);
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return result;
    }

    private static bool IsIpv4(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && System.Net.IPAddress.TryParse(value, out var address)
        && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && !value.StartsWith('0');
}
