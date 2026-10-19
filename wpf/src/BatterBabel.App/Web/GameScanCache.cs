using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BatterBabel.Core.Qos;
using BatterBabel.Core.Recovery;

namespace BatterBabel.App.Web;

/// <summary>
/// 发布版 scan 的 24 小时缓存（<c>%LOCALAPPDATA%\BatterBabel\games-cache.json</c>）。
///   * 命中缓存：返回 <c>cached:true</c> 与缓存里的 <c>scannedAt</c>；
///   * 缓存里任何 installed 游戏的 installPath 已不存在 -> 整份作废并重扫（发布版 dropped 分支）；
///   * 每次都重算每个游戏的 <c>accelerated</c>：优先看真实存在的 <c>BatterBabel-&lt;gameId&gt;</c>
///     策略，QoS 查不出来时才退回 desired.json 的记录。两者都不猜。
/// </summary>
internal static class GameScanCache
{
    private const int CacheVersion = 6;
    private const int MaxAgeHours = 24;

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatterBabel",
        "games-cache.json");

    public static async Task<object> ScanAsync(bool force, CancellationToken ct)
    {
        if (!force)
        {
            var cached = TryLoad();
            if (cached is not null)
            {
                cached["cached"] = true;
                await RecomputeAcceleratedAsync(cached, ct).ConfigureAwait(false);
                return cached;
            }
        }

        var fresh = JsonSerializer.SerializeToNode(Discovery.WindowsGameScanner.Scan()) as JsonObject
                    ?? new JsonObject { ["ok"] = false, ["message"] = "扫描没有返回结果" };
        fresh["cached"] = false;
        Write(fresh);
        await RecomputeAcceleratedAsync(fresh, ct).ConfigureAwait(false);
        return fresh;
    }

    private static JsonObject? TryLoad()
    {
        try
        {
            if (!File.Exists(CachePath)) { return null; }
            var root = JsonNode.Parse(File.ReadAllText(CachePath)) as JsonObject;
            if (root is null) { return null; }
            if (root["version"]?.GetValue<int>() != CacheVersion) { return null; }

            var savedAt = root["savedAt"]?.GetValue<string>();
            if (string.IsNullOrEmpty(savedAt) || !DateTimeOffset.TryParse(savedAt, out var stamp)) { return null; }
            if (DateTimeOffset.Now - stamp > TimeSpan.FromHours(MaxAgeHours)) { return null; }

            if (root["games"] is not JsonArray games || games.Count == 0) { return null; }

            // 缓存不是磁盘现状：任何已安装游戏的安装目录不在了，就整份作废。
            foreach (var node in games)
            {
                if (node is not JsonObject game) { return null; }
                var installPath = game["installPath"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(installPath) && !Directory.Exists(installPath)) { return null; }
                var executablePath = game["executablePath"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(executablePath) && !File.Exists(executablePath)) { return null; }
            }

            return root;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Write(JsonObject payload)
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
            var copy = (JsonObject)payload.DeepClone();
            copy["version"] = CacheVersion;
            copy["savedAt"] = DateTimeOffset.Now.ToString("O");
            File.WriteAllText(CachePath, copy.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 缓存只是加速，写不进去不算扫描失败。
        }
    }

    private static async Task RecomputeAcceleratedAsync(JsonObject payload, CancellationToken ct)
    {
        if (payload["games"] is not JsonArray games) { return; }

        var policies = await QosPolicy.ActivePoliciesAsync(ct).ConfigureAwait(false);
        var names = new HashSet<string>(policies.Names, StringComparer.OrdinalIgnoreCase);
        var desired = BootRecovery.ReadDesired();
        var count = 0;

        foreach (var node in games)
        {
            if (node is not JsonObject game) { continue; }
            var id = game["id"]?.GetValue<string>() ?? "";
            if (id.Length == 0) { continue; }

            bool accelerated;
            if (policies.Ok)
            {
                accelerated = names.Contains(QosPolicy.GamePolicyName(id));
            }
            else
            {
                // QoS 读不出来时才退回 desired.json，且必须能对上这条游戏的 exe。
                var exe = game["executablePath"]?.GetValue<string>();
                accelerated = desired is { QosEnabled: true, QosExecutable: not null }
                              && !string.IsNullOrEmpty(exe)
                              && string.Equals(Path.GetFileName(desired.QosExecutable), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase);
            }

            game["accelerated"] = accelerated;
            if (accelerated) { count++; }
        }

        payload["acceleratedCount"] = count;
        payload["qosQueryOk"] = policies.Ok;
        if (!policies.Ok) { payload["qosQueryMessage"] = policies.Message; }
    }
}
