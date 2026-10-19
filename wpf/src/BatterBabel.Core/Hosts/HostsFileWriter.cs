using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BatterBabel.Core.Hosts;

/// <summary>一条待写入的映射（域名 → IPv4）。</summary>
public sealed record HostsChange(string Domain, string Ip);

/// <summary>写入/还原的结果。失败时 <see cref="Message"/> 是可直接展示给用户的中文原因。</summary>
public sealed record HostsResult(bool Ok, string Message, string Path, string Backup, int Entries);

/// <summary>「只删标记块」的结果。<see cref="Removed"/> 是被删掉的映射行数（不含标记行）。</summary>
public sealed record HostsStripResult(bool Ok, string Message, string Backup, int Removed);

/// <summary>
/// hosts 写入的事务边界（只做文件层，不决定策略）：
///   1) 写前把原文件复制成带时间戳的备份，并回读校验哈希；
///   2) 只删自己标记块内的行，不动其它行；重复应用会替换旧块而不是叠加；
///   3) 同目录临时文件 + 原子替换，写后回读校验每一条都在；
///   4) 任何一步失败都尝试把原内容写回去，并如实返回失败原因（不改「成功」文案）。
///
/// 需要管理员权限；没有权限时返回 Ok=false 而不是抛异常，让页面能显示原因。
/// </summary>
public static class HostsFileWriter
{
    public const string BeginMarker = "# >>> Batter Babel";
    public const string EndMarker = "# <<< Batter Babel";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static HostsResult Apply(string hostsPath, IReadOnlyList<HostsChange> changes, string backupDir)
    {
        if (!File.Exists(hostsPath))
        {
            return new HostsResult(false, "找不到 hosts 文件：" + hostsPath, hostsPath, "", 0);
        }

        var cleaned = changes
            .Where(change => IPAddress.TryParse(change.Ip, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Where(change => IsSafeDomain(change.Domain))
            .DistinctBy(change => change.Domain, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (cleaned.Length == 0)
        {
            return new HostsResult(false, "没有可写入的优选结果", hostsPath, "", 0);
        }

        var original = File.ReadAllBytes(hostsPath);
        var originalText = Decode(original);
        var hasBom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        var newline = originalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        string backup;
        try
        {
            backup = Snapshot(hostsPath, original, backupDir);
        }
        catch (Exception ex)
        {
            return new HostsResult(false, "写前备份失败，已中止：" + ex.GetType().Name, hostsPath, "", 0);
        }

        try
        {
            var kept = StripExisting(originalText, cleaned.Select(change => change.Domain));
            var builder = new StringBuilder(kept);
            if (builder.Length > 0 && builder[^1] != '\n') { builder.Append(newline); }
            builder.Append(BeginMarker + "（自动生成，删除本块即可撤销）").Append(newline);
            foreach (var change in cleaned)
            {
                builder.Append(change.Ip).Append(' ').Append(change.Domain).Append(newline);
            }

            builder.Append(EndMarker).Append(newline);

            WriteAtomic(hostsPath, Encode(builder.ToString(), hasBom), original);
            var written = Decode(File.ReadAllBytes(hostsPath));
            var missing = cleaned
                .Where(change => !written.Contains($"{change.Ip} {change.Domain}", StringComparison.OrdinalIgnoreCase))
                .Select(change => change.Domain)
                .ToArray();
            if (missing.Length > 0)
            {
                WriteAtomic(hostsPath, original, null);
                return new HostsResult(false, "写后校验失败，已回滚：" + string.Join("、", missing), hostsPath, backup, 0);
            }

            return new HostsResult(true, $"已写入 {cleaned.Length} 条映射", hostsPath, backup, cleaned.Length);
        }
        catch (UnauthorizedAccessException)
        {
            return new HostsResult(false, "需要管理员权限才能修改 hosts（请以管理员身份运行）", hostsPath, backup, 0);
        }
        catch (Exception ex)
        {
            if (ex is IOException io2 && (io2.Message.StartsWith("LOCKED:") || io2.Message.StartsWith("CONFLICT:")))
            {
                var reason = io2.Message[(io2.Message.IndexOf(':') + 1)..].Trim();
                return new HostsResult(false, reason, hostsPath, backup, 0);
            }

            TryRestore(hostsPath, original);
            if (ex is IOException io && io.HResult == unchecked((int)0x80070005))
            {
                return new HostsResult(false, "需要管理员权限才能修改 hosts（请以管理员身份运行）", hostsPath, backup, 0);
            }

            return new HostsResult(false, "写入失败，已尝试回滚：" + ex.GetType().Name, hostsPath, backup, 0);
        }
    }

    /// <summary>
    /// 还原 = 从当前文件里删掉本程序写入的标记块（并备份当前文件）。
    /// 比"复制最近一次备份"更可预测：用户点几次「还原」都回到没有本程序映射的状态，
    /// 不会因为中途又应用过一次而把带映射的旧备份装回去。
    /// </summary>
    public static HostsResult Restore(string hostsPath, string backupDir)
    {
        var strip = Strip(hostsPath, backupDir);
        return new HostsResult(strip.Ok, strip.Message, hostsPath, strip.Backup, strip.Removed);
    }

    /// <summary>
    /// 只删本程序的标记块（<see cref="BeginMarker"/> 到 <see cref="EndMarker"/>），块外的字节一个都不动：
    /// 逐行在**原始字节**上切分，删掉的行连同行尾一起丢弃，剩下的字节原样拼回去。
    ///
    /// 为什么不用「把最近一次备份整文件复制回来」：那会把用户/第三方在这之后对 hosts 的其它修改
    /// 一起回滚。为什么不用 Decode/Encode 走字符串：带 BOM 的文件或含非 UTF-8 字节的行会被改写成
    /// 另一种字节序列，"删块后与原文件逐字节相等" 就不成立（发布版 cf-restore 声称只删自己的行）。
    ///
    /// 先快照，再原子替换，写后回读逐字节比对；不一致就把原字节写回去并返回失败。
    /// </summary>
    public static HostsStripResult Strip(string hostsPath, string backupDir)
    {
        if (!File.Exists(hostsPath))
        {
            return new HostsStripResult(false, "找不到 hosts 文件：" + hostsPath, "", 0);
        }

        byte[] original;
        try
        {
            original = File.ReadAllBytes(hostsPath);
        }
        catch (Exception ex)
        {
            return new HostsStripResult(false, "无法读取 hosts：" + ex.GetType().Name, "", 0);
        }

        var (kept, removed, found, incomplete) = StripBlocks(original);
        if (!found)
        {
            return new HostsStripResult(true, "hosts 里没有本程序写入的映射，无需还原", "", 0);
        }

        // 标记块只有开始行、没有结束行：后面的所有内容都无法判断归属。
        // 这时删任何东西都可能连带删掉第三方映射，所以宁可失败也不改文件。
        if (incomplete)
        {
            return new HostsStripResult(false, "hosts 的 Batter Babel 标记块不完整（缺少结束行），已中止还原，文件未改动", "", 0);
        }

        string backup;
        try
        {
            backup = Snapshot(hostsPath, original, backupDir);
        }
        catch (Exception ex)
        {
            return new HostsStripResult(false, "还原前备份失败，已中止：" + ex.GetType().Name, "", 0);
        }

        try
        {
            WriteAtomic(hostsPath, kept, original);
            if (!File.ReadAllBytes(hostsPath).SequenceEqual(kept))
            {
                TryRestore(hostsPath, original);
                return new HostsStripResult(false, "还原后回读与预期字节不一致，已回滚", backup, 0);
            }

            return new HostsStripResult(true, removed > 0 ? $"已还原：移除 {removed} 条映射" : "已移除空的标记块", backup, removed);
        }
        catch (UnauthorizedAccessException)
        {
            return new HostsStripResult(false, "需要管理员权限才能还原 hosts（请以管理员身份运行）", backup, 0);
        }
        catch (Exception ex)
        {
            if (ex is IOException io2 && (io2.Message.StartsWith("LOCKED:") || io2.Message.StartsWith("CONFLICT:")))
            {
                var reason = io2.Message[(io2.Message.IndexOf(':') + 1)..].Trim();
                return new HostsStripResult(false, reason, backup, 0);
            }

            TryRestore(hostsPath, original);
            if (ex is IOException io && io.HResult == unchecked((int)0x80070005))
            {
                return new HostsStripResult(false, "需要管理员权限才能还原 hosts（请以管理员身份运行）", backup, 0);
            }

            return new HostsStripResult(false, "还原失败，已尝试回滚：" + ex.GetType().Name, backup, 0);
        }
    }

    /// <summary>
    /// 在原始字节上删掉所有标记块。返回 (保留字节, 被删映射行数, 是否找到块)。
    /// 行边界只认 '\n' / "\r\n"；块外的每一行（含其行尾）原样复制，所以「写块再删块」逐字节回到写入前。
    /// </summary>
    private static (byte[] Kept, int Removed, bool Found, bool Incomplete) StripBlocks(byte[] original)
    {
        using var output = new MemoryStream(original.Length);
        var index = 0;
        var insideBlock = false;
        var found = false;
        var removed = 0;

        while (index < original.Length)
        {
            var lineStart = index;
            var lineEnd = index;
            while (lineEnd < original.Length && original[lineEnd] != (byte)'\n') { lineEnd++; }
            var next = lineEnd < original.Length ? lineEnd + 1 : lineEnd;

            var textStart = lineStart;
            var textEnd = lineEnd;
            while (textEnd > textStart && (original[textEnd - 1] == (byte)'\r' || original[textEnd - 1] == (byte)' ' || original[textEnd - 1] == (byte)'\t')) { textEnd--; }
            while (textStart < textEnd && (original[textStart] == (byte)' ' || original[textStart] == (byte)'\t')) { textStart++; }

            var skip = false;
            if (StartsWithAscii(original, textStart, textEnd, BeginMarker))
            {
                insideBlock = true;
                found = true;
                skip = true;
            }
            else if (StartsWithAscii(original, textStart, textEnd, EndMarker) && insideBlock)
            {
                insideBlock = false;
                skip = true;
            }
            else if (insideBlock)
            {
                if (textEnd > textStart && original[textStart] != (byte)'#') { removed++; }
                skip = true;
            }

            if (!skip) { output.Write(original, lineStart, next - lineStart); }
            index = next;
        }

        // 走到文件末尾还在块里，说明开始标记没有对应的结束行。
        return (output.ToArray(), removed, found, insideBlock);
    }

    private static bool StartsWithAscii(byte[] bytes, int start, int end, string prefix)
    {
        if (end - start < prefix.Length) { return false; }
        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[start + i] != (byte)prefix[i]) { return false; }
        }

        return true;
    }

    private static int CountBlockEntries(string text)
    {
        var count = 0;
        var inside = false;
        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith(BeginMarker, StringComparison.Ordinal)) { inside = true; continue; }
            if (trimmed.StartsWith(EndMarker, StringComparison.Ordinal)) { inside = false; continue; }
            if (inside && trimmed.Length > 0 && !trimmed.StartsWith('#')) { count++; }
        }

        return count;
    }

    public static string DefaultBackupDir =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BatterBabel",
            "hosts-backup");

    private static string StripExisting(string text, IEnumerable<string> domains)
    {
        var set = new HashSet<string>(domains, StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder(text.Length);
        var insideBlock = false;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith(BeginMarker, StringComparison.Ordinal)) { insideBlock = true; continue; }
            if (trimmed.StartsWith(EndMarker, StringComparison.Ordinal)) { insideBlock = false; continue; }
            if (insideBlock) { continue; }

            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && set.Contains(parts[1]))
            {
                continue; // 已有同名映射：删掉，统一由本程序的块管理
            }

            output.Append(line);
            // 最后一段是原文末尾换行切出来的空串：不再补换行，保证"删块"后字节回到原样。
            if (index < lines.Length - 1) { output.Append(newline); }
        }

        return output.ToString();
    }

    /// <summary>
    /// 原子替换：同目录随机临时文件 + File.Replace。
    /// 额外两道保护：①独占锁文件，避免两个实例同时写；②替换前比对当前内容与调用方读到的字节，
    /// 外部编辑器/另一个进程在这期间改过 hosts 时中止，不覆盖别人的修改。
    /// </summary>
    private static void WriteAtomic(string path, byte[] bytes, byte[]? expectedCurrent = null)
    {
        var directory = System.IO.Path.GetDirectoryName(path)!;
        var lockPath = System.IO.Path.Combine(directory, "." + System.IO.Path.GetFileName(path) + ".bblk");
        FileStream? lockHandle = null;
        try
        {
            lockHandle = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            throw new IOException("LOCKED: 另一个 Batter Babel 进程正在写入 hosts，请稍后重试");
        }

        var temp = System.IO.Path.Combine(directory, "." + System.IO.Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".bbtmp");
        try
        {
            if (expectedCurrent is not null && File.Exists(path))
            {
                var current = File.ReadAllBytes(path);
                if (!current.SequenceEqual(expectedCurrent))
                {
                    throw new IOException("CONFLICT: hosts 已被其它程序修改，请重新执行本操作");
                }
            }

            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            TryDelete(temp);
            lockHandle?.Dispose();
            TryDelete(lockPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { }
    }

    private static void TryRestore(string path, byte[] original)
    {
        try { WriteAtomic(path, original); } catch (Exception) { /* 回滚失败交给调用方报告 */ }
    }

    private static string Snapshot(string hostsPath, byte[] content, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var target = System.IO.Path.Combine(backupDir, $"hosts-{stamp}.bak");
        File.WriteAllBytes(target, content);
        if (!SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(SHA256.HashData(content)))
        {
            throw new IOException("备份校验不一致");
        }

        return target;
    }

    private static bool IsSafeDomain(string domain) =>
        !string.IsNullOrWhiteSpace(domain)
        && domain.Length <= 253
        && domain.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '.' || ch == '_');

    private static string Decode(byte[] bytes)
    {
        var skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return StrictUtf8.GetString(bytes, skip, bytes.Length - skip);
    }

    private static byte[] Encode(string text, bool withBom = false)
    {
        var body = StrictUtf8.GetBytes(text);
        if (!withBom) { return body; }
        var bytes = new byte[body.Length + 3];
        bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF;
        body.CopyTo(bytes, 3);
        return bytes;
    }
}
