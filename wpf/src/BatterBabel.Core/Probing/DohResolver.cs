using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace BatterBabel.Core.Probing;

/// <summary>
/// 候选地址来源，语义对齐 cf-probe.ps1 的 Get-ProbeCandidates：
/// 系统 DNS + 阿里 DoH + 腾讯 DoH，三路结果按来源轮询合并、去重，只保留严格 IPv4，
/// 上限 24 个。只做解析，不做任何写入。
/// </summary>
public static class DohResolver
{
    private const int MaxCandidates = 24;
    private const string Alidns = "https://dns.alidns.com/resolve";
    private const string DohPub = "https://doh.pub/dns-query";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(4),
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    public static async Task<IReadOnlyList<string>> ResolveAsync(string domain, CancellationToken cancellationToken = default)
    {
        var system = await SystemAsync(domain, cancellationToken).ConfigureAwait(false);
        var doh = await Task.WhenAll(
            QueryAsync(Alidns, domain, cancellationToken),
            QueryAsync(DohPub, domain, cancellationToken)).ConfigureAwait(false);
        var sources = new[] { system, doh[0], doh[1] };

        // 来源轮询：每个来源各出一个，再回到第一个来源出第二个，和 PS 的 round-robin 一致。
        var result = new List<string>(MaxCandidates);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 0; result.Count < MaxCandidates; round++)
        {
            var added = false;
            foreach (var list in sources)
            {
                if (round >= list.Count) { continue; }
                added = true;
                var address = list[round];
                if (IsStrictIPv4(address) && seen.Add(address)) { result.Add(address); }
                if (result.Count >= MaxCandidates) { break; }
            }
            if (!added) { break; }
        }
        return result;
    }

    private static async Task<IReadOnlyList<string>> SystemAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken).ConfigureAwait(false);
            return addresses
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString())
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static async Task<IReadOnlyList<string>> QueryAsync(
        string endpoint,
        string domain,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = endpoint + "?name=" + Uri.EscapeDataString(domain) + "&type=A";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("accept", "application/dns-json, application/dns-message");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { return Array.Empty<string>(); }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("Answer", out var answers)
                || answers.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>();
            foreach (var answer in answers.EnumerateArray())
            {
                if (!answer.TryGetProperty("type", out var type) || type.GetInt32() != 1) { continue; }
                if (!answer.TryGetProperty("data", out var data)) { continue; }
                var text = data.GetString();
                if (text is null || !IsStrictIPv4(text)) { continue; }
                if (!list.Contains(text, StringComparer.Ordinal)) { list.Add(text); }
            }
            return list;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>严格 IPv4：四段十进制、无前导零、每段 0-255（拒绝 1.2.3.04 这类写法）。</summary>
    public static bool IsStrictIPv4(string? value)
    {
        if (string.IsNullOrEmpty(value)) { return false; }
        var parts = value.Split('.');
        if (parts.Length != 4) { return false; }
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3) { return false; }
            if (part.Length > 1 && part[0] == '0') { return false; }
            if (!int.TryParse(part, out var number) || number is < 0 or > 255) { return false; }
        }
        return true;
    }
}
