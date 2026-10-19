using System.Globalization;
using System.Text.RegularExpressions;

namespace BatterBabel.Core.Discovery;

/// <summary>一个 Steam 库里的已安装游戏（appmanifest 的解析结果）。</summary>
public sealed record SteamAppInstall(string AppId, string InstallDir, string ManifestPath, string Name = "");

/// <summary>
/// Steam 库清单解析（只读，纯函数）。
///
/// 输入是 Valve 的 KeyValues 文本（libraryfolders.vdf / appmanifest_*.acf），
/// 只取迁移需要的最小字段：库根路径、appid、installdir。格式未知时返回空集合，
/// 让上层降级到注册表/名单，而不是抛异常。
/// </summary>
public static partial class SteamLibraryParser
{
    [GeneratedRegex("\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex("\"appid\"\\s+\"(?<appid>[0-9]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex(@"""name""\s+""(?<name>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    [GeneratedRegex("\"installdir\"\\s+\"(?<dir>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    /// <summary>解析 libraryfolders.vdf 文本中的库根路径（不含 steamapps 本身）。</summary>
    public static IReadOnlyList<string> ParseLibraryPaths(string vdfText)
    {
        var paths = new List<string>();
        foreach (Match match in LibraryPathRegex().Matches(vdfText ?? string.Empty))
        {
            var path = Unescape(match.Groups["path"].Value);
            if (path.Length > 0 && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>从 appmanifest_&lt;appid&gt;.acf 解析 appid 与 installdir；缺失时返回 null。</summary>
    public static SteamAppInstall? ParseAppManifest(string path, string text)
    {
        var appId = AppIdRegex().Match(text ?? string.Empty);
        var dir = InstallDirRegex().Match(text ?? string.Empty);
        if (!appId.Success || !dir.Success) { return null; }
        var installDir = Unescape(dir.Groups["dir"].Value);
        if (installDir.Length == 0) { return null; }
        var name = NameRegex().Match(text ?? string.Empty);
        return new SteamAppInstall(
            appId.Groups["appid"].Value,
            installDir,
            path,
            name.Success ? Unescape(name.Groups["name"].Value) : string.Empty);
    }

    /// <summary>读取一个 Steam 根下的所有库根：根/steamapps 本身 + libraryfolders.vdf 里的 path。</summary>
    public static IReadOnlyList<string> ReadLibraryRoots(string steamRoot)
    {
        var roots = new List<string>();
        var own = Path.Combine(steamRoot, "steamapps");
        if (Directory.Exists(own)) { roots.Add(steamRoot); }

        var manifest = Path.Combine(own, "libraryfolders.vdf");
        if (File.Exists(manifest))
        {
            foreach (var path in ParseLibraryPaths(File.ReadAllText(manifest)))
            {
                if (Directory.Exists(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(path);
                }
            }
        }

        return roots;
    }

    private static string Unescape(string value) => value.Replace("\\\\", "\\", StringComparison.Ordinal);
}

/// <summary>
/// 可执行文件选择：移植发布版 game-paths.ps1 的黑名单语义（防作弊/安装器/运行库不能被「加速」）。
/// 只读，纯函数；找不到返回 null 表示「不要加速这个游戏」。
/// </summary>
public static class ExecutableSelector
{
    private static readonly string[] Blocked =
    {
        "unins", "uninstall", "redist", "vc_redist", "vcredist", "dxsetup", "dxwebsetup",
        "dotnet", "crashpad", "crashreport", "crashreporter", "easyanticheat", "eac",
        "battleye",
        "createdump",
        "steamworks", "beservice", "steamerrorreporter", "launcher_installer",
    };

    public static bool IsBlocked(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return Blocked.Any(token => name.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>
    /// 依次尝试候选相对路径（如 <c>game\bin\win64\cs2.exe</c> 与 <c>cs2.exe</c>），
    /// 命中且未被黑名单拦下才返回；都不存在时在安装根扫一层 exe，仍无可信结果则 null。
    /// </summary>
    public static string? Select(string installRoot, params string[] candidates)
    {
        if (!Directory.Exists(installRoot)) { return null; }
        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var full = Path.GetFullPath(Path.Combine(installRoot, candidate));
            if (File.Exists(full) && !IsBlocked(full)) { return full; }
        }

        return null;
    }
}
