using System.IO;

namespace BatterBabel.App.Web;

/// <summary>定位页面资源（开发期用仓库里的页面目录；打包后可放在 exe 旁 assets/）。</summary>
internal static class PageAssets
{
    /// <summary>
    /// 页面目录优先级：打包后的 assets/ → src/web（当前界面）；旧的 public-original/public 仅作兼容回退。
    /// 原版优先，是因为它才是用户要保留的那套界面；适配版留着对照与回退。
    /// </summary>
    private static readonly string[] PageDirNames = { "web", "public-original", "public" };

    /// <summary>返回含 index.html 的目录；找不到返回 null。</summary>
    internal static string? FindPublicDir()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "assets");
        if (File.Exists(Path.Combine(beside, "index.html"))) { return beside; }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            foreach (var name in PageDirNames)
            {
                var candidate = Path.Combine(dir.FullName, "src", name);
                if (File.Exists(Path.Combine(candidate, "index.html"))) { return candidate; }
            }
        }

        return null;
    }

    /// <summary>从页面根目录回推仓库根（.../src/public → ...），用于 evidence 输出位置。</summary>
    internal static string? FindRepoRoot(string publicDir)
    {
        var dir = new DirectoryInfo(publicDir);
        if (dir.Parent?.Parent is { } root && Directory.Exists(Path.Combine(root.FullName, "wpf")))
        {
            return root.FullName;
        }

        return null;
    }
}
