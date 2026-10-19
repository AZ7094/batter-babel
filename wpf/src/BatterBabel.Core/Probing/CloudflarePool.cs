using System;
using System.Collections.Generic;
using System.Net;

namespace BatterBabel.Core.Probing;

/// <summary>
/// 发布版 cf-optimize 的候选池（对应 <c>ip-core.ps1:Get-CfCandidatePool</c>）。
///
/// 规则要点（与 Tauri 0.10.3 一致）：
/// 1. 只用内置的 Cloudflare 官方 IPv4 快照（15 段），运行期不联网刷新；
/// 2. 采样**完全确定性**：每段 <c>perRange = ceil(target / ranges)</c> 个地址，
///    <c>step = usable / perRange</c>，偏移从 1 开始按 step 递增，取 <c>base + offset</c>；
/// 3. 去重后到 target 即停（最后一段可能被截断）。
///
/// 不做随机化：同一份快照 + 同一个 target 必须得到同一批地址，否则不同机器/不同次
/// 运行的结果无法解释，也无法和 Tauri 版对照。
/// </summary>
public static class CloudflarePool
{
    /// <summary>官方 ips-v4 快照（RetrievedAt 2026-10-15T10:06:39Z，与发布版同一份表）。</summary>
    private static readonly string[] Ranges =
    {
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
    };

    public const int DefaultTarget = 2000;

    public static IReadOnlyList<string> Snapshot(int target = DefaultTarget)
    {
        if (target <= 0) { target = DefaultTarget; }

        var perRange = (int)Math.Ceiling(target / (double)Ranges.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(target);

        foreach (var cidr in Ranges)
        {
            if (result.Count >= target) { break; }
            if (!TryParseRange(cidr, out var baseAddress, out var count)) { continue; }

            var usable = (long)count;
            var step = (long)Math.Floor(usable / (double)perRange);
            if (step < 1) { step = 1; }

            for (var index = 1; index <= perRange && result.Count < target; index++)
            {
                var offset = index * step;
                if (offset >= usable - 1) { break; }
                var candidate = ToIpv4(baseAddress + (uint)offset);
                if (seen.Add(candidate)) { result.Add(candidate); }
            }
        }

        return result;
    }

    private static bool TryParseRange(string cidr, out uint baseAddress, out int count)
    {
        baseAddress = 0;
        count = 0;
        var slash = cidr.IndexOf('/');
        if (slash <= 0 || !IPAddress.TryParse(cidr[..slash], out var address)) { return false; }
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) { return false; }
        if (!int.TryParse(cidr[(slash + 1)..], out var prefix) || prefix < 0 || prefix > 32) { return false; }

        var bytes = address.GetAddressBytes();
        baseAddress = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        count = 1 << (32 - prefix);
        return true;
    }

    private static string ToIpv4(uint value) =>
        string.Join('.', (value >> 24) & 255, (value >> 16) & 255, (value >> 8) & 255, value & 255);
}
