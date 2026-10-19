using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using BatterBabel.Core.Discovery;
using BatterBabel.Core.Domain;
using Microsoft.Win32;

namespace BatterBabel.App.Discovery;

/// <summary>
/// Windows 上的真实游戏发现（S1）。只读：读 Steam 库清单、Epic manifest、注册表卸载键与进程表，
/// 不写任何文件、不改注册表、不启动游戏。
///
/// 口径与发布版一致的地方：7 个画像来自 <see cref="GameCatalog"/>，只列**真的装在磁盘上**的游戏；
/// 找不到就 installed=false，不为了界面好看强行置 true。
/// </summary>
internal static class WindowsGameScanner
{
    internal static object Scan()
    {
        var steamApps = ReadSteamInstalls();
        var epicApps = ReadEpicInstalls();
        var running = RunningProcessNames();

        var catalogAppIds = new HashSet<string>(GameCatalog.Games.Select(g => g.AppId), StringComparer.OrdinalIgnoreCase);
        var installedCount = 0;
        var games = GameCatalog.Games.Select(game =>
        {
            var groups = GameCatalog.GroupsFor(game);
            var (installed, source, installPath) = Locate(game, steamApps, epicApps);
            var executable = installed && installPath is not null
                ? ExecutableSelector.Select(installPath, ExtraCandidates(game.Id)
                    .Append(game.RelativePath).Append(game.Executable).ToArray())
                : null;
            if (installed) { installedCount++; }
            var isRunning = executable is not null
                && running.Contains(Path.GetFileNameWithoutExtension(executable));

            return new
            {
                id = game.Id,
                name = game.Name,
                appId = game.AppId,
                vendor = game.Vendor,
                installed,
                running = isRunning,
                executablePath = executable,
                source,
                installPath,
                support = groups.Count > 0,
                standalone = game.Standalone,
                helpsGameplay = game.HelpsGameplay,
                platform = GameCatalog.PlatformForApp(game.AppId),
                optGroups = groups.Select(group => new
                {
                    tag = TagName(group.Tag),
                    label = group.Label,
                    mode = ModeName(group.Mode),
                    domains = group.Domains.ToArray(),
                }).ToArray(),
            };
        }).Cast<object>().ToList();

        // 发布版的 scan 也会把「目录里认得但不是 7 个画像」的 Steam 游戏列出来（support=false），
        // 这样用户能在同一个列表里看到本机全部已装游戏，而不是被我们的画像名单挡住。
        foreach (var (appId, install) in steamApps
                     .Where(pair => !catalogAppIds.Contains(pair.Key))
                     .OrderBy(pair => pair.Value.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            // 没有可执行文件的条目（运行库、纯数据目录）不是游戏，不列进来。
            var extraExe = ExecutableSelector.Select(install.InstallDir, RootExecutables(install.InstallDir));
            if (extraExe is null) { continue; }
            // 运行库/共享组件不是游戏：Steamworks Common Redistributables（228980）是最常见的误报。
            if (install.AppId == "228980"
                || install.InstallDir.Contains("redistributable", StringComparison.OrdinalIgnoreCase)
                || install.InstallDir.Contains("Steamworks Shared", StringComparison.OrdinalIgnoreCase)
                || install.InstallDir.Contains("_CommonRedist", StringComparison.OrdinalIgnoreCase)) { continue; }
            installedCount++;
            games.Add(new
            {
                id = "steam-" + appId,
                name = string.IsNullOrEmpty(install.Name) ? "Steam App " + appId : install.Name,
                appId,
                vendor = "Steam",
                installed = true,
                running = extraExe is not null && running.Contains(Path.GetFileNameWithoutExtension(extraExe)),
                executablePath = extraExe,
                source = "steam",
                installPath = install.InstallDir,
                support = false,
                standalone = false,
                helpsGameplay = false,
                platform = "",
                optGroups = Array.Empty<object>(),
            });
        }

        // 发布版 Get-QosCapability 的真实探测：ms_pacer 绑定是否全部启用 + 组策略 QoS 存储是否可读。
        // 探测失败/不可用就如实返回 available:false，不用常量冒充。
        var capability = BatterBabel.Core.Qos.QosPolicy.CapabilityAsync().GetAwaiter().GetResult();

        return new
        {
            ok = true,
            status = "SUCCEEDED",
            scannedAt = DateTimeOffset.Now.ToString("o"),
            fixture = false,
            scanSource = "Steam libraryfolders/appmanifest + Epic manifests",
            installedCount,
            games,
            qos = new
            {
                available = capability.Available,
                persistent = capability.Persistent,
                store = capability.Store,
                reason = capability.Reason,
                sources = Array.Empty<object>(),
            },
        };
    }

    /// <summary>Steam：解析 appid -&gt; 安装目录；库根来自注册表、默认目录与 libraryfolders.vdf。</summary>
    private static Dictionary<string, SteamAppInstall> ReadSteamInstalls()
    {
        var installs = new Dictionary<string, SteamAppInstall>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in SteamRoots())
        {
            foreach (var library in SteamLibraryParser.ReadLibraryRoots(root))
            {
                var steamApps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamApps)) { continue; }
                foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
                {
                    try
                    {
                        var parsed = SteamLibraryParser.ParseAppManifest(manifest, File.ReadAllText(manifest));
                        if (parsed is null) { continue; }
                        var dir = Path.Combine(steamApps, "common", parsed.InstallDir);
                        if (Directory.Exists(dir)) { installs[parsed.AppId] = parsed with { InstallDir = dir }; }
                    }
                    catch (IOException) { /* 单个清单读失败不影响其它游戏 */ }
                }
            }
        }

        return installs;
    }

    private static IEnumerable<string> SteamRoots()
    {
        var roots = new List<string>();
        Add(ReadRegistryValue(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"));
        Add(ReadRegistryValue(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        return roots;

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
                && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(path);
            }
        }
    }

    private static string? ReadRegistryValue(RegistryKey hive, string subKey, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception) { return null; } // 注册表被策略锁住时如实降级到目录探测
    }

    /// <summary>Epic：读 launcher 的 *.item manifest，按显示名匹配画像。</summary>
    private static Dictionary<string, string> ReadEpicInstalls()
    {
        var installs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) { return installs; }

        foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                var name = root.TryGetProperty("DisplayName", out var display) ? display.GetString() : null;
                var location = root.TryGetProperty("InstallLocation", out var install) ? install.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
                {
                    installs[name] = location;
                }
            }
            catch (Exception) { /* 坏 manifest 跳过，不让一个文件拖垮整次扫描 */ }
        }

        return installs;
    }

    /// <summary>exe 与安装目录名的相似度：0 = 名字对得上，1 = 只是同目录里的别的程序。</summary>
    private static int NameAffinity(string installDirName, string relativeExe)
    {
        var dir = Normalize(installDirName);
        var exe = Normalize(Path.GetFileNameWithoutExtension(relativeExe));
        if (dir.Length == 0 || exe.Length == 0) { return 1; }
        return dir.Contains(exe, StringComparison.Ordinal) || exe.Contains(dir, StringComparison.Ordinal) ? 0 : 1;
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>目录根上的 exe 名单（只用于非画像游戏，取第一个没被黑名单挡住的）。</summary>
    private static string[] RootExecutables(string installRoot)
    {
        try
        {
            if (!Directory.Exists(installRoot)) { return Array.Empty<string>(); }
            var root = Path.GetFullPath(installRoot);
            return Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}redist{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetRelativePath(root, file))
                .Where(relative => relative.Count(c => c == Path.DirectorySeparatorChar) <= 3)
                // 优先选「文件名像目录名」的 exe（Crimson Desert → CrimsonDesert.exe），
                // 避免被同目录的工具程序（pers.exe）抢走；同分再选层级浅、名字短的。
                .OrderBy(relative => NameAffinity(Path.GetFileName(root), relative))
                .ThenBy(relative => relative.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(relative => relative.Length)
                .ToArray();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>画像里记的可执行文件名和真实安装有出入时，在这里补已知别名（不猜路径）。</summary>
    private static string[] ExtraCandidates(string gameId) => gameId switch
    {
        "apex" => new[] { "r5apex_dx12.exe" },
        _ => Array.Empty<string>(),
    };

    private static (bool Installed, string Source, string? InstallPath) Locate(
        GameProfile game,
        Dictionary<string, SteamAppInstall> steamApps,
        Dictionary<string, string> epicApps)
    {
        if (game.AppId.Length > 0 && steamApps.TryGetValue(game.AppId, out var steam))
        {
            return (true, "steam", steam.InstallDir);
        }

        foreach (var (name, location) in epicApps)
        {
            if (name.Contains(game.Name, StringComparison.OrdinalIgnoreCase))
            {
                return (true, "epic", location);
            }
        }

        return (false, string.Empty, null);
    }

    /// <summary>
    /// 只解析一个游戏的可执行文件全路径（不枚举全部画像）。「加速游戏」要按 exe 建 QoS 策略，
    /// 复用它就不会出现「扫描说装了、加速却找不到路径」这种两套解析。
    /// </summary>
    internal static string? FindExecutable(string gameId)
    {
        var game = GameCatalog.Find(gameId);
        if (game is null) { return null; }

        var (installed, _, installPath) = Locate(game, ReadSteamInstalls(), ReadEpicInstalls());
        if (!installed || installPath is null) { return null; }

        var candidates = new List<string>();
        candidates.AddRange(ExtraCandidates(gameId));
        candidates.Add(game.RelativePath);
        candidates.Add(game.Executable);
        return ExecutableSelector.Select(installPath, candidates.ToArray());
    }

    private static HashSet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try { names.Add(process.ProcessName); }
            catch (Exception) { /* 进程在枚举途中退出 */ }
        }

        return names;
    }

    internal static string TagName(RouteTag tag) => tag switch
    {
        RouteTag.Cf => "cf",
        RouteTag.Amazon => "amazon",
        RouteTag.Steam => "steam",
        RouteTag.Ea => "ea",
        RouteTag.Klei => "klei",
        RouteTag.Ubisoft => "ubisoft",
        RouteTag.Epic => "epic",
        RouteTag.Blizzard => "blizzard",
        _ => "cf",
    };

    internal static string ModeName(ProbeMode mode) => mode == ProbeMode.Cloudflare ? "cloudflare" : "probe";
}
