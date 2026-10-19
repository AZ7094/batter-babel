using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BatterBabel.Core.Probing;

public sealed record DownloadFinalResult(
    string Ip,
    long Bytes,
    double? MibPerSecond,
    bool Ok,
    string? Reason);

/// <summary>
/// 阶段 1 的下载决赛（对应 <c>optimization.ps1:2456-2533</c>）：
/// 1,000,000 字节、8 秒、并发 2，URL <c>https://speed.cloudflare.com/__down?bytes=1000000</c>，
/// 每个候选都 pin 到指定 IP（SNI/Host 仍是 speed.cloudflare.com）。
///
/// 成功判定与 Tauri 一致：HTTP 200 + 落盘字节数 == 样本数 + 有吞吐。
/// 大小写错或只读到一半都不算成功，避免把「连上了但被截断」当成快。
/// </summary>
public static class DownloadFinals
{
    public const int SampleBytes = 1_000_000;
    public const int TimeoutSeconds = 8;
    public const int Concurrency = 2;
    /// <summary>整批下载决赛的预算（对应 optimization.ps1:2499-2512 的 8+5 秒单一时钟）。</summary>
    public const int BatchBudgetSeconds = 15;
    private const string TargetHost = "speed.cloudflare.com";

    public static async Task<IReadOnlyList<DownloadFinalResult>> MeasureAsync(
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken = default,
        int sampleBytes = SampleBytes,
        int timeoutSeconds = TimeoutSeconds,
        int concurrency = Concurrency,
        int batchBudgetSeconds = BatchBudgetSeconds)
    {
        if (addresses.Count == 0) { return Array.Empty<DownloadFinalResult>(); }

        var results = new DownloadFinalResult[addresses.Count];
        using var gate = new SemaphoreSlim(concurrency);
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        batch.CancelAfter(TimeSpan.FromSeconds(batchBudgetSeconds));

        var tasks = addresses.Select(async (ip, index) =>
        {
            try
            {
                await gate.WaitAsync(batch.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                results[index] = new DownloadFinalResult(ip, 0, null, false, "整批超时");
                return;
            }

            try
            {
                results[index] = await MeasureOneAsync(ip, sampleBytes, timeoutSeconds, batch.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                results[index] = new DownloadFinalResult(ip, 0, null, false, "整批超时");
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results
            .OrderByDescending(result => result.Ok)
            .ThenByDescending(result => result.MibPerSecond ?? double.MinValue)
            .ToArray();
    }

    private static async Task<DownloadFinalResult> MeasureOneAsync(string ip, int sampleBytes, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return new DownloadFinalResult(ip, 0, null, false, "非法地址");
        }

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(address, 443, token).ConfigureAwait(false);
                    var network = new NetworkStream(socket, ownsSocket: true);
                    var ssl = new SslStream(network, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = TargetHost,
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

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds + 4) };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://{TargetHost}/__down?bytes={sampleBytes}");
        request.Headers.UserAgent.ParseAdd("BatterBabel/0.10 (+download-final)");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var watch = Stopwatch.StartNew();
        long total = 0;

        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                return new DownloadFinalResult(ip, 0, null, false, "HTTP " + (int)response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            while (total < sampleBytes)
            {
                var want = (int)Math.Min(buffer.Length, sampleBytes - total);
                var read = await stream.ReadAsync(buffer.AsMemory(0, want), deadline.Token).ConfigureAwait(false);
                if (read <= 0) { break; }
                total += read;
            }

            watch.Stop();
            var ok = total == sampleBytes && watch.Elapsed.TotalSeconds > 0;
            var mibps = ok ? total / 1024.0 / 1024.0 / watch.Elapsed.TotalSeconds : (double?)null;
            return new DownloadFinalResult(ip, total, mibps, ok, ok ? null : "字节不足（" + total + "）");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DownloadFinalResult(ip, total, null, false, "下载超时");
        }
        catch (HttpRequestException ex)
        {
            return new DownloadFinalResult(ip, total, null, false, "连接失败：" + ex.Message);
        }
        catch (IOException)
        {
            // Cloudflare 边缘偶发直接断流（"unexpected EOF or 0 bytes"），只算这个候选失败。
            return new DownloadFinalResult(ip, total, null, false, "连接中断");
        }
    }
}
