using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BatterBabel.Core.Probing;

public sealed record TcpScreenResult(string Ip, int? ConnectMs, string? Reason);

/// <summary>
/// 阶段 1 的 TCP 预筛（对应 <c>optimization.ps1:2406-2453</c>）：
/// 端口 443、单次超时 900ms、并发 200，整批等齐后按连接耗时升序。
/// </summary>
public static class TcpScreen
{
    public const int DefaultTimeoutMs = 900;
    public const int DefaultConcurrency = 200;

    public static async Task<IReadOnlyList<TcpScreenResult>> ScreenAsync(
        IReadOnlyList<string> addresses,
        int timeoutMs = DefaultTimeoutMs,
        int concurrency = DefaultConcurrency,
        CancellationToken cancellationToken = default)
    {
        if (addresses.Count == 0) { return Array.Empty<TcpScreenResult>(); }

        var results = new TcpScreenResult[addresses.Count];
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 512));

        var tasks = addresses.Select(async (ip, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await ScreenOneAsync(ip, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(result => result.ConnectMs is not null).OrderBy(result => result.ConnectMs).ToArray();
    }

    private static async Task<TcpScreenResult> ScreenOneAsync(string ip, int timeoutMs, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return new TcpScreenResult(ip, null, "非法地址");
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        var watch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(address, 443, timeout.Token).ConfigureAwait(false);
            watch.Stop();
            return new TcpScreenResult(ip, (int)watch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TcpScreenResult(ip, null, "超时");
        }
        catch (SocketException ex)
        {
            return new TcpScreenResult(ip, null, ex.SocketErrorCode.ToString());
        }
    }
}
