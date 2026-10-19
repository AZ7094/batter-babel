using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using BatterBabel.Core.Domain;

namespace BatterBabel.Core.Probing;

/// <summary>
/// 一次测速的预算。数值对齐发布版 cf-probe.ps1 / optimization.ps1：
/// 每候选 3 个样本、最少 2 个才能排名，候选上限 24 个，决赛 3 个做吞吐。
/// </summary>
public sealed record ProbeBudget(
    int LatencySamples = 2,
    int MaxCandidatesPerDomain = 24,
    int Finalists = 3,
    int DownloadSeconds = 3,
    long MaxDownloadBytes = 6L * 1024 * 1024,
    int ConnectTimeoutMs = 3000,
    int Concurrency = 8,
    string SpeedTestUrl = "https://speed.cloudflare.com/__down?bytes=3000000");

/// <summary>一个域名的探测规则（cf-probe.ps1 的 $ProbeEndpointRules 的 C# 版本）。</summary>
public sealed record ProbeRule(
    int StatusMin = 200,
    int StatusMax = 399,
    string? RequiredHeader = null,
    string? ContentTypePrefix = null,
    IReadOnlyList<string>? BodyMustContain = null,
    int MaxBodyBytes = 4096);

/// <summary>
/// 单条线路的真实测量，语义与发布版一致：
///   1) 用真实域名做 HTTPS GET（SNI/证书仍是该域名），连接固定到候选 IP；
///   2) 延迟取 TTFB（首字节），每候选 3 个样本取中位数；
///   3) 按域名规则判断状态码/响应头/正文是否成立，403/405 记为 Keep（可保留但不写 hosts）；
///   4) 只有进入决赛的候选才做吞吐测量，排序仍以中位 TTFB 为准。
/// </summary>
public static class RouteProber
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromMilliseconds(4000),
        PooledConnectionLifetime = TimeSpan.Zero,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    public static ProbeRule RuleFor(string domain)
    {
        // 与 cf-probe.ps1:38-72 一致：CDN 根路径回 404 + server: cloudflare + text/html 是健康答案；
        // notice 必须要正文标记；www 走 API Gateway（400 + x-amz-apigw-id 是正常答案）。
        if (domain.Equals("notice.limbuscompanyapi.com", StringComparison.OrdinalIgnoreCase))
        {
            return new ProbeRule(200, 200, null, null,
                new[] { "latestUpdateDate", "noticeDetailList" }, 32768);
        }
        if (domain.EndsWith("limbuscompanyapi.com", StringComparison.OrdinalIgnoreCase))
        {
            // cf-probe.ps1:37-39：www 的 12/12 候选都回 HTTP 400 + x-amz-apigw-id + 空 body，
            // 这是 API Gateway 的正常答案；只认 2xx 会把最重要的域名判死。
            return new ProbeRule(200, 499, "x-amz-apigw-id", null, null, 4096);
        }
        if (domain.EndsWith("limbuscompanycdn.org", StringComparison.OrdinalIgnoreCase))
        {
            return new ProbeRule(200, 499, "server: cloudflare", "text/html", null, 32768);
        }
        return new ProbeRule();
    }

    public static async Task<RouteCandidate> MeasureAsync(
        string domain,
        string ip,
        ProbeBudget budget,
        CancellationToken cancellationToken)
    {
        var rule = RuleFor(domain);
        var samples = new List<double>(budget.LatencySamples);
        var lastStatus = 0;
        string? lastReason = null;
        var keep = false;

        for (var i = 0; i < budget.LatencySamples; i++)
        {
            var probe = await ProbeOnceAsync(domain, ip, rule, budget, cancellationToken).ConfigureAwait(false);
            lastStatus = probe.Status;
            lastReason = probe.Reason;
            if (probe.TtfbMs is not null) { samples.Add(probe.TtfbMs.Value); }
            if (probe.Keep) { keep = true; }
            if (probe.Fatal) { break; }
            if (i < budget.LatencySamples - 1) { await Task.Delay(40, cancellationToken).ConfigureAwait(false); }
        }

        var median = Median(samples);
        // cf-probe.ps1:563-565 与 840-860：Keep(403/405) 与 Unusable 一个样本就是终态，
        // 不要求补满 2 个样本；只有 Usable 才需要 samples>=2 才能排名。
        var verdict = keep && samples.Count >= 1
            ? ProbeVerdict.Keep
            : (samples.Count >= 2 && median is not null ? ProbeVerdict.Usable : ProbeVerdict.Unusable);
        var reason = verdict switch
        {
            ProbeVerdict.Usable => null,
            ProbeVerdict.Keep => lastReason ?? "需要人工确认（403/405）",
            _ => lastReason ?? "没有有效样本",
        };
        return new RouteCandidate(
            Address: ip,
            Verdict: verdict,
            MedianMs: median,
            Samples: samples.Count,
            StatusCode: lastStatus,
            DownloadMiBps: null,
            RankReason: verdict == ProbeVerdict.Unusable
                ? (samples.Count == 0 ? RankReason.NoValidLatencySamples : RankReason.InsufficientSamples)
                : RankReason.None,
            Rankable: verdict != ProbeVerdict.Unusable && samples.Count >= 2,
            Reason: reason);
    }

    private sealed record ProbeSample(double? TtfbMs, int Status, bool Keep, bool Fatal, string? Reason);

    private static async Task<ProbeSample> ProbeOnceAsync(
        string domain,
        string ip,
        ProbeRule rule,
        ProbeBudget budget,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(budget.ConnectTimeoutMs),
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                try
                {
                    await socket.ConnectAsync(IPAddress.Parse(ip), 443, token).ConfigureAwait(false);
                    var network = new NetworkStream(socket, ownsSocket: true);
                    var ssl = new SslStream(network, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = context.DnsEndPoint.Host,
                        // 只做可用性探测、不传任何数据；CRL/OCSP 拉取会让单次握手拖到 10 秒以上。
                        CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                    }, token).ConfigureAwait(false);
                    return ssl;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(4),
            // 和 curl 一致：先按 HTTP/1.1 说话。部分 Cloudflare 边缘在 h2 前置协商上会卡住。
            DefaultRequestVersion = System.Net.HttpVersion.Version11,
            DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact,
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + domain + "/");
        request.Headers.UserAgent.ParseAdd("BatterBabel/0.10 (+probe)");

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var ttfb = watch.Elapsed.TotalMilliseconds;
            var status = (int)response.StatusCode;
            var body = await ReadBodyAsync(response, rule.MaxBodyBytes, cancellationToken).ConfigureAwait(false);

            if (status is 403 or 405)
            {
                return new ProbeSample(ttfb, status, Keep: true, Fatal: false, Reason: "HTTP " + status);
            }
            if (status < rule.StatusMin || status > rule.StatusMax)
            {
                return new ProbeSample(ttfb, status, Keep: false, Fatal: true, Reason: "HTTP " + status);
            }
            if (rule.RequiredHeader is not null)
            {
                var parts = rule.RequiredHeader.Split(':', 2);
                var headerName = parts[0].Trim();
                var expectedValue = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                if (!response.Headers.TryGetValues(headerName, out var values)
                    || (expectedValue.Length > 0
                        && !values.Any(value => value.Contains(expectedValue, StringComparison.OrdinalIgnoreCase))))
                {
                    return new ProbeSample(ttfb, status, Keep: false, Fatal: true, Reason: "缺少 " + rule.RequiredHeader);
                }
            }
            if (rule.ContentTypePrefix is not null)
            {
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!contentType.StartsWith(rule.ContentTypePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return new ProbeSample(ttfb, status, Keep: false, Fatal: true, Reason: "Content-Type 不符");
                }
            }
            if (rule.BodyMustContain is { Count: > 0 })
            {
                foreach (var marker in rule.BodyMustContain)
                {
                    if (!body.Contains(marker, StringComparison.Ordinal))
                    {
                        return new ProbeSample(ttfb, status, Keep: false, Fatal: true, Reason: "正文缺少 " + marker);
                    }
                }
            }
            return new ProbeSample(ttfb, status, Keep: false, Fatal: false, null);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 单次超时不算候选终态：同一 IP 换个时刻往往就通了，继续采样。
            return new ProbeSample(null, 0, Keep: false, Fatal: false,
                "超时(" + ex.GetType().Name + ")");
        }
        catch (HttpRequestException ex)
        {
            return new ProbeSample(null, 0, Keep: false, Fatal: true, "连接失败：" + ex.Message);
        }
        catch (IOException)
        {
            return new ProbeSample(null, 0, Keep: false, Fatal: true, "连接中断");
        }
    }

    private static async Task<string> ReadBodyAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[Math.Min(maxBytes, 32768)];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) { break; }
                total += read;
            }
            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>一个域名的全部候选：并发测 TTFB，取中位延迟前 N 名做吞吐，返回完整列表。</summary>
    public static async Task<IReadOnlyList<RouteCandidate>> MeasureDomainAsync(
        string domain,
        IReadOnlyList<string> candidates,
        ProbeBudget budget,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var gate = new SemaphoreSlim(Math.Max(1, budget.Concurrency));
        var measured = new List<RouteCandidate>();
        var queue = candidates.Take(budget.MaxCandidatesPerDomain).ToArray();
        var tasks = queue.Select(async ip =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await MeasureAsync(domain, ip, budget, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        foreach (var candidate in await Task.WhenAll(tasks).ConfigureAwait(false)) { measured.Add(candidate); }

        var ranked = measured.Where(candidate => candidate.Rankable)
            .OrderBy(candidate => candidate.MedianMs ?? double.MaxValue)
            .ThenBy(candidate => candidate.Address, StringComparer.Ordinal)
            .ToArray();
        progress?.Invoke($"延迟筛选：{queue.Length} 个候选，{ranked.Length} 个可排名");

        // 阶段 2 只做逐域名验证；吞吐由阶段 1（TcpScreen + DownloadFinals）统一给出，
        // 不再在这里对每个域名各挑 3 个候选测速——那是与发布版不一致的旧语义。
        return measured.ToArray();
    }

    /// <summary>决赛候选的吞吐测量：连到候选 IP，请求 speed.cloudflare.com 的固定字节数。</summary>
    public static async Task<double?> MeasureDownloadAsync(
        string ip,
        ProbeBudget budget,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(budget.ConnectTimeoutMs),
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                await socket.ConnectAsync(IPAddress.Parse(ip), 443, token).ConfigureAwait(false);
                var network = new NetworkStream(socket, ownsSocket: true);
                var ssl = new SslStream(network, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = context.DnsEndPoint.Host,
                }, token).ConfigureAwait(false);
                return ssl;
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(budget.DownloadSeconds + 6) };
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(
                budget.SpeedTestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { return null; }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            long total = 0;
            var deadline = TimeSpan.FromSeconds(budget.DownloadSeconds);
            while (watch.Elapsed < deadline && total < budget.MaxDownloadBytes)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) { break; }
                total += read;
            }
            var seconds = watch.Elapsed.TotalSeconds;
            if (total < 64 * 1024 || seconds <= 0.05) { return null; }
            return Math.Round(total / 1024d / 1024d / seconds, 1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double? Median(List<double> values)
    {
        if (values.Count == 0) { return null; }
        var sorted = values.OrderBy(value => value).ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2d
            : sorted[middle];
        return Math.Round(median, 0);
    }
}
