using System.Security.Cryptography;
using System.Text;

namespace BatterBabel.Core.GameConfig;

/// <summary>
/// 一份游戏配置文件的托管块检查结果（只读，不改文件）。
/// <see cref="Encoding"/> 是识别出来的编码名：utf-8 / utf-8-bom / utf-16le / utf-16be / latin1。
/// </summary>
public sealed record ManagedBlockInfo(
    bool Ok,
    bool Exists,
    bool WellFormed,
    int LineCount,
    string Encoding,
    string Message);

/// <summary>
/// 写入/还原托管块的结果。失败时 <see cref="Message"/> 是可直接展示给用户的中文原因。
/// <see cref="Changed"/> 表示这次调用真的改了文件；<see cref="Migrated"/> 表示覆盖了旧版
/// Batter Babel 写下的整套配置（已先备份）。
/// </summary>
public sealed record ManagedConfigResult(
    bool Ok,
    string Message,
    string Path,
    string Backup = "",
    bool Changed = false,
    bool Migrated = false);

/// <summary>
/// 游戏配置文件的「托管块」读写边界：只碰标记之间的行，标记之外一个字节都不动。
///
/// 语义直接对照原版 PowerShell（只读参考，不复制实现细节）：
///   - 标记与旧格式识别：D:\Batter Babel\scripts\net-profiles.ps1:200-213
///   - Test-HasManagedBlock / Test-WellFormedManagedBlock：同文件 215-234
///   - Merge-ManagedBlock：同文件 236-276
///   - Remove-ManagedBlock：同文件 278-298
///   - Test-NetLegacyBlock：同文件 300-317
///   - 编码识别/回写：同文件 375-429（utf-8(-bom) / utf-16le / utf-16be）
///   - 调用顺序与「不完整标记就不写」：scripts/optimization.ps1:4443-4482、4624-4635
///
/// 与 PS 版有意不同的两点（都为了满足「块外字节不变」）：
///   1) PS 的 Merge/Remove 会先把整个文件的行尾统一成 CRLF；这里不做整文件归一化，
///      只在拼块时用文件自己的行尾风格，否则 LF 文件被改一次就整份变 CRLF，
///      还原也无法回到原字节。旧格式迁移除外——那时候整份旧内容本来就要被替换。
///   2) PS 遇到无 BOM 且不是合法 UTF-8 的字节时退回系统代码页（ANSI）。.NET 10 的
///      Encoding.Default 就是 UTF-8，照搬会静默替换成 U+FFFD 并写坏用户内容；
///      这里退回 ISO-8859-1（单字节，编码回去与原始字节一一对应），保护块外字节。
/// </summary>
public static class ManagedConfigFile
{
    /// <summary>托管块起始标记（net-profiles.ps1:200，逐字符一致）。</summary>
    public const string StartMarker = "// >>> Batter Babel >>>";

    /// <summary>托管块结束标记（net-profiles.ps1:201，逐字符一致）。</summary>
    public const string EndMarker = "// <<< Batter Babel <<<";

    /// <summary>
    /// 旧版（0.10.1 及更早）写进 autoexec.cfg 的整套内容（net-profiles.ps1:205-213）。
    /// 只用于识别「这文件是我们以前写的」，识别到后先备份再整体替换；用户自己写的
    /// 无标记内容不会匹配这里，只会追加块。
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyLines = Array.AsReadOnly(new[]
    {
        "// Batter Babel network tuning (generated)",
        "rate 196608",
        "cl_interp 0.031",
        "cl_interp_ratio 2",
        "cl_net_buffer_ticks 64",
        "net_graph 1",
        "cl_allow_animated_avatars false",
    });

    /// <summary>备份后缀：与 PS 版一致，放在配置文件旁边，只写第一次。</summary>
    public const string BackupSuffix = ".batterbabel.bak";

    /// <summary>新建文件（没有原行尾可继承）时用 CRLF，与 PS 版写出的块一致。</summary>
    private const string DefaultNewline = "\r\n";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // --- 纯文本层：不做任何 I/O，便于单独证明 remove(merge(x)) 的行为 -------------------------

    /// <summary>文件里是否同时出现两个标记（宽松判断，等价 Test-HasManagedBlock）。</summary>
    public static bool HasManagedBlock(string? text) =>
        !string.IsNullOrEmpty(text)
        && text.Contains(StartMarker, StringComparison.Ordinal)
        && text.Contains(EndMarker, StringComparison.Ordinal);

    /// <summary>
    /// 是否是本程序会写出的块：恰好一个起始标记，且它后面恰好一个结束标记（等价
    /// Test-WellFormedManagedBlock）。只有它为 true 才允许写文件。
    /// </summary>
    public static bool IsWellFormedManagedBlock(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return false; }
        var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
        if (start < 0) { return false; }
        var end = text.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0) { return false; }
        if (text.IndexOf(StartMarker, start + StartMarker.Length, StringComparison.Ordinal) >= 0) { return false; }
        return text.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) < 0;
    }

    /// <summary>
    /// 合并托管块（等价 Merge-ManagedBlock，但不整文件重排行尾）：
    ///   - 没有块：保留原文；原文非空且不以换行结尾时先补一个换行，再追加块；
    ///   - 已有块：替换第一个块（含紧随其后的一个换行），块后面的内容原样保留；
    ///   - 标记只出现一个、或结束标记在起始标记之前：原样返回、不做猜测，
    ///     这样调用方可以先用 IsWellFormedManagedBlock 判定并拒绝写入。
    /// </summary>
    public static string Merge(string? existingText, IReadOnlyList<string>? lines)
    {
        var original = existingText ?? string.Empty;
        var hasStart = original.Contains(StartMarker, StringComparison.Ordinal);
        var hasEnd = original.Contains(EndMarker, StringComparison.Ordinal);
        if (hasStart != hasEnd) { return original; }

        var newline = DetectNewline(original);
        var body = lines ?? Array.Empty<string>();
        var block = BuildBlock(body, newline);

        if (!hasStart)
        {
            var prefix = original.Length > 0 && !EndsWithNewline(original) ? newline : string.Empty;
            return original + prefix + block;
        }

        if (!TryLocateBlock(original, out var start, out var afterBlock))
        {
            // 两个标记都在，但结束标记在起始标记之前：不是本程序写过的顺序，原样返回。
            return original;
        }

        return original[..start] + block + original[afterBlock..];
    }

    /// <summary>
    /// 删除托管块（等价 Remove-ManagedBlock）：删掉块本身和块末尾带来的那一个换行。
    /// 块外文本逐字节保留：返回值只由原串切片拼成，不做行尾归一化。
    /// 标记不全 / 顺序反了时原样返回。
    /// </summary>
    public static string Remove(string? existingText)
    {
        var original = existingText ?? string.Empty;
        if (!original.Contains(StartMarker, StringComparison.Ordinal)
            || !original.Contains(EndMarker, StringComparison.Ordinal))
        {
            return original;
        }

        if (!TryLocateBlock(original, out var start, out var afterBlock)) { return original; }
        return original[..start] + original[afterBlock..];
    }

    /// <summary>
    /// 旧格式判定（等价 Test-NetLegacyBlock）：去掉空行和首尾空白后，行数正好等于旧版
    /// 写出的 7 行，且每一行都能对上（PS 的 -eq 不区分大小写，这里同样 OrdinalIgnoreCase）。
    /// </summary>
    public static bool IsNetLegacyBlock(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return false; }
        var seen = new List<string>();
        foreach (var raw in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length > 0) { seen.Add(trimmed); }
        }

        if (seen.Count != LegacyLines.Count) { return false; }
        foreach (var want in LegacyLines)
        {
            if (!seen.Contains(want, StringComparer.OrdinalIgnoreCase)) { return false; }
        }

        return true;
    }

    // --- 文件层 -----------------------------------------------------------------------------

    /// <summary>
    /// 把托管块写进 <paramref name="path"/>：
    ///   1) 目录不存在 / 路径是目录 / 行内容非法 → 结构化失败，不碰文件；
    ///   2) 旧版整套配置 → 先写 &lt;path&gt;.batterbabel.bak（已存在就不覆盖），再整体替换；
    ///   3) 其余情况按原文编码读写，合并后的块必须良构才落盘；
    ///   4) 首次写入前备份原字节；写后读回校验，不一致就用原字节回滚。
    /// 同一文件首次写入之后，任何一次 Apply 都不会覆盖已有备份（备份永远是「没被本程序
    /// 改过的原始内容」）。
    /// </summary>
    public static ManagedConfigResult Apply(string path, IReadOnlyList<string>? lines)
    {
        var full = NormalizePath(path, out var pathError);
        if (full is null) { return new ManagedConfigResult(false, pathError!, path ?? string.Empty); }

        var body = lines ?? Array.Empty<string>();
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i] is null)
            {
                return new ManagedConfigResult(false, $"第 {i + 1} 行为 null，已中止", full);
            }

            if (body[i].Contains('\r') || body[i].Contains('\n'))
            {
                return new ManagedConfigResult(false, $"第 {i + 1} 行包含换行符，无法作为单行写入", full);
            }

            if (body[i].Contains(StartMarker, StringComparison.Ordinal)
                || body[i].Contains(EndMarker, StringComparison.Ordinal))
            {
                return new ManagedConfigResult(false, $"第 {i + 1} 行包含 Batter Babel 标记，已中止", full);
            }
        }

        var directory = System.IO.Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return new ManagedConfigResult(false, $"游戏配置目录不存在：{directory}", full);
        }

        if (Directory.Exists(full))
        {
            return new ManagedConfigResult(false, "目标路径是目录，不是配置文件", full);
        }

        var existed = File.Exists(full);
        // 只读属性会在写入阶段变成 UnauthorizedAccessException；提前判定并给出可操作的中文原因，
        // 同时避免在必然失败时留下备份等副作用。
        if (existed && IsReadOnly(full))
        {
            return new ManagedConfigResult(false, "配置文件是只读的（请先取消只读属性），未改动", full);
        }

        byte[] originalBytes;
        try
        {
            originalBytes = existed ? File.ReadAllBytes(full) : Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            return new ManagedConfigResult(false, "读取配置文件失败：" + Describe(ex), full);
        }

        ConfigEncoding encoding;
        string existingText;
        try
        {
            (encoding, existingText) = Decode(originalBytes);
        }
        catch (Exception ex)
        {
            return new ManagedConfigResult(false, "文件编码无法安全解码，未改动：" + Describe(ex), full);
        }

        if (encoding.Fallback && body.Any(line => line.Any(ch => ch > '\u00FF')))
        {
            return new ManagedConfigResult(
                false,
                "文件不是 UTF-8（按单字节编码处理），写入内容含非 Latin-1 字符，已中止以免写坏文件",
                full);
        }

        var migrated = false;
        var baseText = existingText;
        // 只替换「正好是旧版整套内容」的文件；用户自己的无标记内容走追加分支。
        if (existingText.Length > 0 && !HasManagedBlock(existingText) && IsNetLegacyBlock(existingText))
        {
            migrated = true;
            baseText = string.Empty;
        }

        var merged = Merge(baseText, body);
        if (!IsWellFormedManagedBlock(merged))
        {
            return new ManagedConfigResult(
                false,
                "文件里的 Batter Babel 标记不完整或顺序异常，未写入（请手工检查标记行）",
                full);
        }

        // 内容已经是目标状态：不重复写盘，也不再造备份（幂等）。
        if (existed && merged == existingText)
        {
            var existingBackup = BackupPathFor(full);
            return new ManagedConfigResult(
                true,
                "托管块内容已是最新，未改动文件",
                full,
                File.Exists(existingBackup) ? existingBackup : string.Empty,
                false,
                migrated);
        }

        var backup = BackupPathFor(full);
        if (existed)
        {
            try
            {
                EnsureBackup(full, originalBytes, backup);
            }
            catch (Exception ex)
            {
                return new ManagedConfigResult(false, "写前备份失败，已中止：" + Describe(ex), full);
            }
        }

        try
        {
            var written = Encode(merged, encoding);
            WriteAtomic(full, written);

            // 写后读回：必须能按原编码解出、必须与预期文本逐字符相等、块必须良构。
            // 任何一项不满足都回滚原字节，不报告成功。
            var readBack = File.ReadAllBytes(full);
            var (_, readText) = Decode(readBack);
            if (readText != merged || !IsWellFormedManagedBlock(readText))
            {
                Rollback(full, originalBytes, existed);
                return new ManagedConfigResult(false, "写后读回校验失败，已回滚原文件", full, backup);
            }

            var message = migrated
                ? "已迁移旧版 Batter Babel 配置：原文件备份为 " + System.IO.Path.GetFileName(backup) + "，旧内容已被托管块替换"
                : existed
                    ? (HasManagedBlock(existingText) ? "已更新托管块" : "已写入托管块（原有内容保持不变）")
                    : "已写入托管块（新建配置文件）";
            return new ManagedConfigResult(true, message, full, existed ? backup : string.Empty, true, migrated);
        }
        catch (UnauthorizedAccessException)
        {
            Rollback(full, originalBytes, existed);
            return new ManagedConfigResult(false, "没有写入权限（文件只读或目录受保护），未改动配置", full, backup);
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            Rollback(full, originalBytes, existed);
            return new ManagedConfigResult(false, "没有写入权限（文件只读或目录受保护），未改动配置", full, backup);
        }
        catch (Exception ex)
        {
            Rollback(full, originalBytes, existed);
            return new ManagedConfigResult(false, "写入失败，已尝试回滚：" + Describe(ex), full, backup);
        }
    }

    /// <summary>
    /// 从当前文件里删掉托管块（不是复制 .bak 回来）：按原文编码写回，删块后读回校验，
    /// 失败用原字节回滚。文件不存在 / 没有块 → Ok=true、Changed=false 的「无需还原」。
    /// 标记不成对或顺序异常 → Ok=false，不动文件（这种文件不是本程序写的，不猜）。
    /// </summary>
    public static ManagedConfigResult Restore(string path)
    {
        var full = NormalizePath(path, out var pathError);
        if (full is null) { return new ManagedConfigResult(false, pathError!, path ?? string.Empty); }

        if (!File.Exists(full))
        {
            return new ManagedConfigResult(true, "无需还原：文件不存在", full);
        }

        if (IsReadOnly(full))
        {
            return new ManagedConfigResult(false, "配置文件是只读的（请先取消只读属性），未改动", full);
        }

        byte[] originalBytes;
        try
        {
            originalBytes = File.ReadAllBytes(full);
        }
        catch (Exception ex)
        {
            return new ManagedConfigResult(false, "读取配置文件失败：" + Describe(ex), full);
        }

        ConfigEncoding encoding;
        string existingText;
        try
        {
            (encoding, existingText) = Decode(originalBytes);
        }
        catch (Exception ex)
        {
            return new ManagedConfigResult(false, "文件编码无法安全解码，未改动：" + Describe(ex), full);
        }

        if (!HasManagedBlock(existingText))
        {
            return new ManagedConfigResult(true, "无需还原：未找到 Batter Babel 托管块", full);
        }

        if (!IsWellFormedManagedBlock(existingText))
        {
            return new ManagedConfigResult(false, "文件里的 Batter Babel 标记不完整或顺序异常，未改动", full);
        }

        var stripped = Remove(existingText);
        if (stripped == existingText)
        {
            return new ManagedConfigResult(true, "无需还原：未找到 Batter Babel 托管块", full);
        }

        try
        {
            WriteAtomic(full, Encode(stripped, encoding));
            var (_, readText) = Decode(File.ReadAllBytes(full));
            if (readText != stripped || HasManagedBlock(readText))
            {
                Rollback(full, originalBytes, existed: true);
                return new ManagedConfigResult(false, "还原后读回校验失败，已回滚原文件", full);
            }

            return new ManagedConfigResult(true, "已移除 Batter Babel 托管块", full, BackupPathFor(full), true);
        }
        catch (UnauthorizedAccessException)
        {
            Rollback(full, originalBytes, existed: true);
            return new ManagedConfigResult(false, "没有写入权限（文件只读或目录受保护），未改动配置", full);
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            Rollback(full, originalBytes, existed: true);
            return new ManagedConfigResult(false, "没有写入权限（文件只读或目录受保护），未改动配置", full);
        }
        catch (Exception ex)
        {
            Rollback(full, originalBytes, existed: true);
            return new ManagedConfigResult(false, "还原失败，已尝试回滚：" + Describe(ex), full);
        }
    }

    /// <summary>
    /// 只读检查：是否存在托管块、是否良构、块内有多少行（不含两个标记行）、识别到的编码。
    /// 文件不存在不是异常：Ok=true、Exists=false，由调用方决定怎么显示。
    /// </summary>
    public static ManagedBlockInfo Inspect(string? path)
    {
        var full = NormalizePath(path, out var pathError);
        if (full is null) { return new ManagedBlockInfo(false, false, false, 0, string.Empty, pathError!); }
        if (!File.Exists(full))
        {
            return new ManagedBlockInfo(true, false, false, 0, string.Empty, "文件不存在：" + full);
        }

        try
        {
            var bytes = File.ReadAllBytes(full);
            var (encoding, text) = Decode(bytes);
            var exists = HasManagedBlock(text);
            var wellFormed = IsWellFormedManagedBlock(text);
            var count = CountBlockLines(text);
            var message = !exists
                ? "未找到 Batter Babel 托管块"
                : wellFormed
                    ? $"存在良构托管块，块内 {count} 行"
                    : "块标记存在但不成对或顺序异常（可能被手工编辑）";
            return new ManagedBlockInfo(true, exists, wellFormed, count, encoding.Name, message);
        }
        catch (Exception ex)
        {
            return new ManagedBlockInfo(false, false, false, 0, string.Empty, "检查失败：" + Describe(ex));
        }
    }

    /// <summary>块内行数（不含标记行）。非良构时返回 0。</summary>
    public static int CountBlockLines(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsWellFormedManagedBlock(text)) { return 0; }
        if (!TryLocateBlock(text, out var start, out _)) { return 0; }

        var endMarker = text.IndexOf(EndMarker, start, StringComparison.Ordinal);
        var bodyStart = start + StartMarker.Length;
        var body = text[bodyStart..endMarker];

        // 去掉起始标记后、结束标记前各自的那一个换行。
        if (body.StartsWith("\r\n", StringComparison.Ordinal)) { body = body[2..]; }
        else if (body.StartsWith('\n')) { body = body[1..]; }
        if (body.EndsWith("\r\n", StringComparison.Ordinal)) { body = body[..^2]; }
        else if (body.EndsWith('\n')) { body = body[..^1]; }

        if (body.Length == 0) { return 0; }
        var lines = 1;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '\n') { lines++; }
        }

        return lines;
    }

    // --- 内部：块定位与拼装 ---------------------------------------------------------------

    private static string BuildBlock(IReadOnlyList<string> lines, string newline)
    {
        var builder = new StringBuilder();
        builder.Append(StartMarker).Append(newline);
        foreach (var line in lines)
        {
            builder.Append(line).Append(newline);
        }

        builder.Append(EndMarker).Append(newline);
        return builder.ToString();
    }

    /// <summary>
    /// 找第一个起始标记和它之后的第一个结束标记；<paramref name="afterBlock"/> 是块尾
    /// （连同紧随其后的一个换行，与 PS 版一致）。顺序反了返回 false。
    /// </summary>
    private static bool TryLocateBlock(string text, out int start, out int afterBlock)
    {
        start = text.IndexOf(StartMarker, StringComparison.Ordinal);
        afterBlock = -1;
        if (start < 0) { return false; }

        var end = text.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0) { return false; }

        afterBlock = end + EndMarker.Length;
        if (afterBlock + 2 <= text.Length && text.AsSpan(afterBlock, 2).SequenceEqual("\r\n")) { afterBlock += 2; }
        else if (afterBlock < text.Length && text[afterBlock] == '\n') { afterBlock += 1; }
        return true;
    }

    /// <summary>
    /// 文件自己的行尾风格：CRLF 与单个 LF 谁多算谁；都没有（新文件/单行无换行）用 CRLF。
    /// </summary>
    private static string DetectNewline(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') { continue; }
            if (i > 0 && text[i - 1] == '\r') { crlf++; } else { lf++; }
        }

        if (crlf == 0 && lf == 0) { return DefaultNewline; }
        return crlf >= lf ? "\r\n" : "\n";
    }

    private static bool EndsWithNewline(string text) =>
        text.Length > 0 && (text[^1] == '\n' || text[^1] == '\r');

    // --- 内部：编码识别 / 读写 --------------------------------------------------------------

    /// <summary>一份文件读出来的文本 + 写回去需要的全部信息（编码、BOM 原字节）。</summary>
    private sealed class ConfigEncoding
    {
        public string Name = "utf-8";
        public Encoding Encoding = Utf8NoBom;
        public byte[] Bom = Array.Empty<byte>();

        /// <summary>是否退回了单字节编码（原字节不是合法 UTF-8）。</summary>
        public bool Fallback;
    }

    /// <summary>
    /// 按 BOM 识别；无 BOM 时先严格按 UTF-8 解码，不合法才退回 ISO-8859-1（保字节）。
    /// 与 PS 版相比这里不对无效字节做 U+FFFD 替换，避免把用户的非 UTF-8 配置写坏。
    /// </summary>
    private static (ConfigEncoding Encoding, string Text) Decode(byte[] bytes)
    {
        var encoding = new ConfigEncoding();
        var offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new ConfigEncoding { Name = "utf-8-bom", Encoding = new UTF8Encoding(false, true), Bom = new byte[] { 0xEF, 0xBB, 0xBF } };
            offset = 3;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = new ConfigEncoding { Name = "utf-16le", Encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true), Bom = new byte[] { 0xFF, 0xFE } };
            offset = 2;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = new ConfigEncoding { Name = "utf-16be", Encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true), Bom = new byte[] { 0xFE, 0xFF } };
            offset = 2;
        }
        else
        {
            encoding = new ConfigEncoding { Name = "utf-8", Encoding = new UTF8Encoding(false, throwOnInvalidBytes: true), Bom = Array.Empty<byte>() };
            try
            {
                _ = encoding.Encoding.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                encoding = new ConfigEncoding { Name = "latin1", Encoding = Encoding.Latin1, Bom = Array.Empty<byte>(), Fallback = true };
            }
        }

        var text = bytes.Length > offset ? encoding.Encoding.GetString(bytes, offset, bytes.Length - offset) : string.Empty;
        return (encoding, text);
    }

    private static byte[] Encode(string text, ConfigEncoding encoding)
    {
        var body = encoding.Encoding.GetBytes(text);
        if (encoding.Bom.Length == 0) { return body; }
        var bytes = new byte[encoding.Bom.Length + body.Length];
        encoding.Bom.CopyTo(bytes, 0);
        body.CopyTo(bytes, encoding.Bom.Length);
        return bytes;
    }

    // --- 内部：目录 / 备份 / 原子写 / 回滚 ---------------------------------------------------

    private static string? NormalizePath(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "配置路径为空";
            return null;
        }

        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            error = "配置路径无效：" + Describe(ex);
            return null;
        }
    }

    private static string BackupPathFor(string path) => path + BackupSuffix;

    private static bool IsReadOnly(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        catch (Exception)
        {
            return false; // 属性读不到就交给真正写入时的异常路径处理
        }
    }

    /// <summary>
    /// 只写第一次：已有 .bak 就不覆盖。备份写完立刻读回比对 SHA-256，不一致视为失败。
    /// （PS 版迁移时用 -Force 覆盖备份；这里选择保留更早、更接近原始状态的备份。）
    /// </summary>
    private static void EnsureBackup(string path, byte[] originalBytes, string backup)
    {
        if (File.Exists(backup)) { return; }
        File.WriteAllBytes(backup, originalBytes);
        var readBack = File.ReadAllBytes(backup);
        if (!SHA256.HashData(readBack).AsSpan().SequenceEqual(SHA256.HashData(originalBytes)))
        {
            throw new IOException("备份内容校验不一致");
        }
    }

    /// <summary>
    /// 同目录临时文件 + 原子替换；替换失败时清掉临时文件。
    /// 目标只读时 File.Replace 会抛 UnauthorizedAccessException，由调用方转成中文失败。
    /// </summary>
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var directory = System.IO.Path.GetDirectoryName(path)!;
        var temp = System.IO.Path.Combine(directory, "." + System.IO.Path.GetFileName(path) + ".bbtmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            try { if (File.Exists(temp)) { File.Delete(temp); } } catch (Exception) { /* 清理失败不掩盖原错误 */ }
            throw;
        }
    }

    private static void Rollback(string path, byte[] originalBytes, bool existed)
    {
        try
        {
            if (existed)
            {
                WriteAtomic(path, originalBytes);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 回滚失败无法再做什么；调用方仍会收到 Ok=false，不会误报成功。
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "没有权限",
        FileNotFoundException => "文件不存在",
        DirectoryNotFoundException => "目录不存在",
        DecoderFallbackException => "编码内容非法",
        _ => ex.GetType().Name + ": " + ex.Message,
    };
}
