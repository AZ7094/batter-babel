using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BatterBabel.Core.Probing;

namespace BatterBabel.App.Web;

/// <summary>发布版 <c>$RouteCatalog</c> 的一条线路。顺序即界面下拉顺序。</summary>
internal sealed record RouteEntry(string Id, string Name, string Host, int Port, string Group);

/// <summary>
/// 发布版 <c>$RouteCatalog</c> + probe / apply-route 用的真实探测。
/// 只做三件事：解析 host、对 443 端口测 TCP 连接耗时、把结果按耗时排序。
/// 不伪造 ICMP（本版没有原始套接字实现）也不伪造下载速度（没有做吞吐测试）。
/// </summary>
internal static class RouteCatalog
{
    /// <summary>与发布版 <c>$RouteCatalog</c> 完全一致（顺序也一致）。</summary>
    public static IReadOnlyList<RouteEntry> Routes { get; } = new[]
    {
        new RouteEntry("cf-dns", "Cloudflare DNS", "1.1.1.1", 443, "Public DNS"),
        new RouteEntry("google-dns", "Google DNS", "8.8.8.8", 443, "Public DNS"),
        new RouteEntry("cf-cdn", "Cloudflare Edge", "speed.cloudflare.com", 443, "CDN / Cloud"),
        new RouteEntry("steam-api", "Steam Service", "api.steampowered.com", 443, "Steam / Valve"),
        new RouteEntry("steam-cm", "Steam Community", "steamcommunity.com", 443, "Steam / Valve"),
        new RouteEntry("ea", "EA Service", "ea.com", 443, "EA / Respawn"),
        new RouteEntry("ea-cdn", "EA CDN", "origin-a.akamaihd.net", 443, "EA / Respawn"),
        new RouteEntry("pm-api", "Limbus Service", "api.limbuscompany.com", 443, "ProjectMoon"),
    };

    public static RouteEntry? Find(string id) =>
        Routes.FirstOrDefault(route => string.Equals(route.Id, id, StringComparison.Ordinal));

    /// <summary>DoH 优先，失败退系统 DNS；IP 字面量直接用。返回第一个 IPv4（没有就第一个地址）。</summary>
    public static async Task<string?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) { return literal.ToString(); }

        try
        {
            var answers = await DohResolver.ResolveAsync(host, ct).ConfigureAwait(false);
            var v4 = answers.FirstOrDefault(address => IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork);
            if (v4 is not null) { return v4; }
            if (answers.Count > 0) { return answers[0]; }
        }
        catch (Exception) { /* DoH 失败 -> 系统 DNS */ }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            var v4 = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
            return v4?.ToString() ?? addresses.FirstOrDefault()?.ToString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>TCP 连接耗时（毫秒）：跑 3 次成功样本取中位数，全失败返回 null。</summary>
    public static async Task<double?> TcpMedianAsync(string ip, int port, int attempts, int timeoutMs, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var address)) { return null; }

        var samples = new List<double>();
        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var client = new TcpClient(address.AddressFamily);
            var watch = Stopwatch.StartNew();
            try
            {
                await client.ConnectAsync(address, port, ct).AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            catch (Exception)
            {
                // 单次失败不算整条线路失败：另外两次仍可能连上。
            }
        }

        if (samples.Count == 0) { return null; }
        samples.Sort();
        var median = samples.Count % 2 == 1
            ? samples[samples.Count / 2]
            : (samples[samples.Count / 2 - 1] + samples[samples.Count / 2]) / 2.0;
        return Math.Round(median, 1);
    }

    /// <summary>发布版 probe：8 条线路真实解析 + TCP 443 三取中位，按耗时升序给 rank。</summary>
    public static async Task<object> ProbeAsync(CancellationToken ct)
    {
        var tasks = Routes.Select(async route =>
        {
            string? ip = null;
            double? tcp = null;
            try
            {
                ip = await ResolveAsync(route.Host, ct).ConfigureAwait(false);
                if (ip is not null)
                {
                    tcp = await TcpMedianAsync(ip, route.Port, 3, 1500, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* 单条线路失败：ip/tcp 留 null，rank 排到末尾 */ }

            return new
            {
                id = route.Id,
                name = route.Name,
                group = route.Group,
                host = route.Host,
                ip,
                icmp = (object?)null,
                tcp,
                score = tcp ?? 9999.0,
            };
        });

        var list = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
        var ranked = list.OrderBy(item => item.score).ThenBy(item => item.id, StringComparer.Ordinal).ToArray();

        var candidates = ranked.Select((item, index) => new
        {
            id = item.id,
            name = item.name,
            group = item.group,
            host = item.host,
            ip = item.ip,
            rank = index + 1,
            icmp = (object?)null,
            tcp = item.tcp,
            score = item.score >= 9999.0 ? (double?)null : item.score,
        }).ToArray();

        return new
        {
            ok = true,
            status = "SUCCEEDED",
            candidates,
            download = (object?)null,
            testedAt = DateTimeOffset.Now,
        };
    }
}
