using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using BatterBabel.Core.Hosts;

namespace BatterBabel.App.Web;

/// <summary>
/// 发布版 cf-apply 的 <c>domains</c> 解析与校验（<c>tag|ip|domain;…</c>）。
/// 逐条照 ip-core.ps1 / optimization.ps1 的规则，不额外放宽：
///   * 必须 3 段；tag 匹配 ^[A-Z0-9]{2,12}$；IP 必须严格点分十进制（拒绝前导零/越界）；
///     domain 匹配 ^[a-zA-Z0-9][a-zA-Z0-9.-]*\.[a-zA-Z]{2,}$；
///   * tag = CF 时必须落在一个 Cloudflare 官方 IPv4 段内。
/// 无效条目只计数，不写入；全部无效时由调用方返回 ok:false。
/// </summary>
internal static class HostsMapping
{
    public sealed record ParseResult(IReadOnlyList<HostsChange> Entries, int Rejected, string? Error);

    private static readonly Regex TagPattern = new("^[A-Z0-9]{2,12}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StrictIPv4Pattern = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DomainPattern = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9.-]*\.[a-zA-Z]{2,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>发布版 ip-core.ps1 的 $CloudflareRanges（IPv4）。</summary>
    private static readonly string[] CloudflareRanges =
    {
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/12",
        "172.64.0.0/17", "172.64.128.0/18", "172.64.192.0/19", "172.64.224.0/22",
        "172.64.229.0/24", "172.64.230.0/23", "172.64.232.0/21", "172.64.240.0/21",
        "172.64.248.0/21", "172.65.0.0/16", "172.66.0.0/16", "172.67.0.0/16",
        "131.0.72.0/22",
    };

    public static ParseResult Parse(string? domains)
    {
        if (string.IsNullOrWhiteSpace(domains))
        {
            return new ParseResult(Array.Empty<HostsChange>(), 0, "没有可写入的映射（domains 为空）");
        }

        var entries = new List<HostsChange>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejected = 0;

        foreach (var raw in domains.Split(';'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { continue; }

            var parts = line.Split('|');
            if (parts.Length != 3)
            {
                rejected++;
                continue;
            }

            var tag = parts[0].Trim();
            var ip = parts[1].Trim();
            var domain = parts[2].Trim();

            if (!TagPattern.IsMatch(tag))
            {
                rejected++;
                continue;
            }

            if (!IsStrictIPv4(ip))
            {
                rejected++;
                continue;
            }

            if (!DomainPattern.IsMatch(domain))
            {
                rejected++;
                continue;
            }

            if (tag.Equals("CF", StringComparison.Ordinal) && !IsCloudflare(ip))
            {
                rejected++;
                continue;
            }

            if (!seen.Add(domain))
            {
                // 同一个域名重复出现：保留第一条，后续条目算重复（不是非法），不写两次。
                rejected++;
                continue;
            }

            entries.Add(new HostsChange(domain, ip));
        }

        if (entries.Count == 0)
        {
            return new ParseResult(entries, rejected, $"没有可写入的映射（{rejected} 条无效）");
        }

        return new ParseResult(entries, rejected, null);
    }

    /// <summary>严格点分十进制：拒绝前导零（.NET 会按八进制解析）与越界数字。</summary>
    public static bool IsStrictIPv4(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return false; }
        if (!StrictIPv4Pattern.IsMatch(value)) { return false; }
        foreach (var part in value.Split('.'))
        {
            if (part.Length > 1 && part[0] == '0') { return false; }
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) { return false; }
            if (number is < 0 or > 255) { return false; }
        }

        return true;
    }

    public static bool IsCloudflare(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        var value = ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
        foreach (var cidr in CloudflareRanges)
        {
            var pieces = cidr.Split('/');
            if (!IPAddress.TryParse(pieces[0], out var network)) { continue; }
            var prefix = int.Parse(pieces[1], CultureInfo.InvariantCulture);
            var mask = prefix == 0 ? 0L : (0xFFFFFFFFL << (32 - prefix)) & 0xFFFFFFFFL;
            var networkBytes = network.GetAddressBytes();
            var networkValue = ((long)networkBytes[0] << 24) | ((long)networkBytes[1] << 16) | ((long)networkBytes[2] << 8) | networkBytes[3];
            if ((value & mask) == (networkValue & mask)) { return true; }
        }

        return false;
    }
}
