namespace BatterBabel.ConfigCheck;

using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BatterBabel.Core.GameConfig;

/// <summary>
/// S10 检查工具。只调用 BatterBabel.Core.GameConfig.ManagedConfigFile，不引 App / Web。
/// 所有文件读写都在临时目录里，不碰真实游戏目录，也不改系统设置。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFail = 1;
    private const int ExitUsage = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16Le = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16Be = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    // CS2 配方（net-profiles.ps1:46-51）；自检里只当作"任意块内容"使用。
    private static readonly string[] Recipe =
    {
        "// Batter Babel (generated) - CS2 自检用",
        "cl_allow_animated_avatars 0",
    };

    private static readonly string[] UpdatedRecipe =
    {
        "// Batter Babel (generated) - 更新后的内容",
        "cl_allow_animated_avatars 0",
        "cl_crosshairalpha 200",
    };

    // 真实 CS2 配方全是 ASCII；非 UTF-8 文件只能安全写入 ASCII/Latin-1 的块内容。
    private static readonly string[] AsciiRecipe =
    {
        "// Batter Babel (generated)",
        "cl_allow_animated_avatars 0",
    };

    private static readonly List<CaseResult> Cases = new();
    private static int _assertions;
    private static int _failed;

    private sealed record CaseResult(string Name, bool Ok, int Assertions, string Detail);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0) { return Usage(); }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "--selftest" => SelfTest(args.Length > 1 ? args[1] : null),
                "--inspect" => Inspect(args.Length > 1 ? args[1] : null),
                "--apply" => Apply(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null),
                "--restore" => Restore(args.Length > 1 ? args[1] : null),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"未处理的异常：{ex.GetType().Name}: {ex.Message}");
            return ExitFail;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            用法：
              BatterBabel.ConfigCheck --selftest <report.json>
              BatterBabel.ConfigCheck --inspect <path>
              BatterBabel.ConfigCheck --apply <path> <linesFile>
              BatterBabel.ConfigCheck --restore <path>
            退出码：0 通过 / 1 失败 / 2 参数用法错误
            """);
        return ExitUsage;
    }

    // --- 只读检查 / 手工验证入口 --------------------------------------------------------------

    private static int Inspect(string? path)
    {
        var info = ManagedConfigFile.Inspect(path);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            tool = "BatterBabel.ConfigCheck",
            action = "inspect",
            path,
            info,
        }, Json));
        return info.Ok ? ExitOk : ExitFail;
    }

    private static int Apply(string? path, string? linesFile)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(linesFile) || !File.Exists(linesFile))
        {
            Console.Error.WriteLine("用法：--apply <path> <linesFile>（linesFile 必须是已存在的 UTF-8 文本，一行一条）");
            return ExitUsage;
        }

        var raw = File.ReadAllText(linesFile, Utf8);
        var lines = raw.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Where(line => line.Length > 0)
            .ToArray();
        var result = ManagedConfigFile.Apply(path, lines);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            tool = "BatterBabel.ConfigCheck",
            action = "apply",
            path,
            lines = lines.Length,
            result,
        }, Json));
        return result.Ok ? ExitOk : ExitFail;
    }

    private static int Restore(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("用法：--restore <path>");
            return ExitUsage;
        }

        var result = ManagedConfigFile.Restore(path);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            tool = "BatterBabel.ConfigCheck",
            action = "restore",
            path,
            result,
        }, Json));
        return result.Ok ? ExitOk : ExitFail;
    }

    // --- 自检 ------------------------------------------------------------------------------

    private static int SelfTest(string? reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            Console.Error.WriteLine("用法：--selftest <report.json>");
            return ExitUsage;
        }

        var started = Stopwatch.StartNew();
        var work = Path.Combine(
            Path.GetTempPath(),
            "bb-configcheck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);
        Directory.CreateDirectory(work);
        Console.WriteLine($"临时目录：{work}");

        try
        {
            RunMarkerConstants();
            RunNewFile(work);
            RunAppendPreservesUserBytes(work);
            RunIdempotentAndUpdate(work);
            RunRoundTripBytes(work);
            RunUtf16AndBom(work);
            RunNewlineStylePreserved(work);
            RunIncompleteMarker(work);
            RunReadOnly(work);
            RunLegacyMigration(work);
            RunBlockInMiddle(work);
            RunInvalidUtf8Preserved(work);
            RunMissingDirectoryAndNoOpRestore(work);
            RunInspect(work);
        }
        finally
        {
            DeleteTree(work);
        }

        started.Stop();
        var total = Cases.Count;
        var passed = Cases.Count(c => c.Ok);
        var report = new
        {
            tool = "BatterBabel.ConfigCheck",
            action = "selftest",
            generatedAt = DateTimeOffset.Now.ToString("o"),
            runtime = Environment.Version.ToString(),
            os = Environment.OSVersion.ToString(),
            markers = new
            {
                start = ManagedConfigFile.StartMarker,
                end = ManagedConfigFile.EndMarker,
                legacyCount = ManagedConfigFile.LegacyLines.Count,
                backupSuffix = ManagedConfigFile.BackupSuffix,
            },
            ok = _failed == 0,
            total,
            passed,
            failed = _failed,
            assertions = _assertions,
            durationMs = started.ElapsedMilliseconds,
            cases = Cases,
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json), Utf8);
        Console.WriteLine();
        Console.WriteLine($"{(report.ok ? "通过" : "失败")}：{passed}/{total} 用例，{_assertions} 条断言，{started.ElapsedMilliseconds} ms");
        Console.WriteLine($"证据：{Path.GetFullPath(reportPath)}");
        return report.ok ? ExitOk : ExitFail;
    }

    private static void RunMarkerConstants()
    {
        RunCase("marker-constants-match-powershell", () =>
        {
            CheckEqual("// >>> Batter Babel >>>", ManagedConfigFile.StartMarker, "StartMarker");
            CheckEqual("// <<< Batter Babel <<<", ManagedConfigFile.EndMarker, "EndMarker");
            CheckEqual(".batterbabel.bak", ManagedConfigFile.BackupSuffix, "BackupSuffix");
            CheckEqual(7, ManagedConfigFile.LegacyLines.Count, "旧格式行数");
            CheckEqual("// Batter Babel network tuning (generated)", ManagedConfigFile.LegacyLines[0], "旧格式首行");
            CheckEqual("cl_allow_animated_avatars false", ManagedConfigFile.LegacyLines[^1], "旧格式末行");

            var legacy = string.Join("\r\n", ManagedConfigFile.LegacyLines);
            Check(ManagedConfigFile.IsNetLegacyBlock(legacy + "\r\n"), "旧格式应被识别");
            Check(ManagedConfigFile.IsNetLegacyBlock(string.Join("\n", ManagedConfigFile.LegacyLines)), "旧格式识别不应依赖行尾");
            Check(!ManagedConfigFile.IsNetLegacyBlock("rate 196608"), "用户自己的一行不应算旧格式");
            Check(!ManagedConfigFile.IsNetLegacyBlock(legacy + "\r\nexec my.cfg"), "多一行不应算旧格式");
            Check(!ManagedConfigFile.HasManagedBlock("plain text"), "普通文本不应报告块");
            Check(ManagedConfigFile.HasManagedBlock("x " + ManagedConfigFile.StartMarker + " y " + ManagedConfigFile.EndMarker), "两个标记都在应报告块");
            Check(!ManagedConfigFile.IsWellFormedManagedBlock(ManagedConfigFile.StartMarker + "\r\nbody\r\n"), "只有起始标记不应良构");
            Check(!ManagedConfigFile.IsWellFormedManagedBlock(ManagedConfigFile.EndMarker + "\r\n" + ManagedConfigFile.StartMarker), "结束标记在前的顺序不应良构");

            // 半个块 / 顺序反了：合并与删除都原样返回，调用方才敢拒绝写入。
            var half = "a\r\n" + ManagedConfigFile.StartMarker + "\r\n";
            CheckEqual(half, ManagedConfigFile.Merge(half, Recipe), "半个块时 Merge 应原样返回");
            CheckEqual(half, ManagedConfigFile.Remove(half), "半个块时 Remove 应原样返回");
            var reversed = ManagedConfigFile.EndMarker + "\r\n" + ManagedConfigFile.StartMarker + "\r\n";
            CheckEqual(reversed, ManagedConfigFile.Merge(reversed, Recipe), "顺序反了时 Merge 应原样返回");

            // 纯文本层：remove(merge(x)) == x（x 以换行结尾，这也是配置文件常见形态）。
            foreach (var newline in new[] { "\r\n", "\n" })
            {
                var user = "line1" + newline + "line2" + newline;
                CheckEqual(user, ManagedConfigFile.Remove(ManagedConfigFile.Merge(user, Recipe)), $"remove(merge(x)) 应等于 x（{(newline == "\r\n" ? "CRLF" : "LF")}）");
            }

            // 没有结尾换行的文件：合并时必须先补一个定界换行（否则标记会粘在最后一行代码后面），
            // 这个换行无法在删除时区分出来，因此 remove 会留下它。这是与 PS 版一致的行为，写进证据。
            var noTrailing = "line1\r\nline2";
            CheckEqual(
                noTrailing + "\r\n",
                ManagedConfigFile.Remove(ManagedConfigFile.Merge(noTrailing, Recipe)),
                "无结尾换行的文件：删除后保留块的定界换行（与 PS 版一致）");
        });
    }

    private static void RunNewFile(string work)
    {
        RunCase("new-file-created", () =>
        {
            var directory = Path.Combine(work, "new");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "autoexec.cfg");

            var result = ManagedConfigFile.Apply(path, Recipe);
            Check(result.Ok, "Apply 应成功：" + result.Message);
            Check(result.Changed, "新建文件应报告 Changed");
            Check(!result.Migrated, "新建文件不应报告迁移");
            Check(File.Exists(path), "文件应已创建");
            Check(!File.Exists(path + ManagedConfigFile.BackupSuffix), "新建文件不应产生备份");
            CheckEqual(string.Empty, result.Backup, "新建文件的 Backup 应为空");

            var bytes = File.ReadAllBytes(path);
            Check(!StartsWithBom(bytes), "新建文件应为无 BOM UTF-8");
            var text = ReadUtf8NoBom(bytes);
            Check(ManagedConfigFile.IsWellFormedManagedBlock(text), "写入后的块应良构");
            foreach (var line in Recipe)
            {
                Check(text.Contains(line, StringComparison.Ordinal), "块内应包含：" + line);
            }

            Check(text.StartsWith(ManagedConfigFile.StartMarker + "\r\n", StringComparison.Ordinal), "块首行应为标记 + CRLF（新建文件默认 CRLF）");
            Check(text.EndsWith(ManagedConfigFile.EndMarker + "\r\n", StringComparison.Ordinal), "块尾应为结束标记 + CRLF");
        });
    }

    private static void RunAppendPreservesUserBytes(string work)
    {
        RunCase("append-preserves-user-bytes", () =>
        {
            var directory = Path.Combine(work, "append");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "autoexec.cfg");

            const string userText = "// 用户自己的设置 —— 中文与 emoji 😀🎮 必须原样保留\r\n" +
                                    "cl_crosshair_dynamic_splitdist 7\r\n" +
                                    "volume 0.25\r\n";
            var originalBytes = Utf8.GetBytes(userText);
            File.WriteAllBytes(path, originalBytes);

            var result = ManagedConfigFile.Apply(path, Recipe);
            Check(result.Ok, "Apply 应成功：" + result.Message);
            Check(result.Changed, "追加块应报告 Changed");
            Check(!result.Migrated, "用户自己的内容不应被当作旧格式");
            Check(result.Message.Contains("原有内容保持不变", StringComparison.Ordinal), "成功文案应说明保留原内容：" + result.Message);

            var writtenBytes = File.ReadAllBytes(path);
            Check(writtenBytes.Length > originalBytes.Length, "写入后文件应变长");
            Check(writtenBytes.AsSpan(0, originalBytes.Length).SequenceEqual(originalBytes), "块外原始字节（中文/emoji/CRLF）必须逐字节不变");

            var text = ReadUtf8NoBom(writtenBytes);
            CheckEqual(userText, ManagedConfigFile.Remove(text), "删除块后应还原为原文");
            Check(ManagedConfigFile.IsWellFormedManagedBlock(text), "块应良构");

            var info = ManagedConfigFile.Inspect(path);
            Check(info.Ok && info.Exists && info.WellFormed, "Inspect 应报告良构块：" + info.Message);
            CheckEqual(Recipe.Length, info.LineCount, "块内行数");

            var backup = path + ManagedConfigFile.BackupSuffix;
            Check(File.Exists(backup), "首次写入应生成 .batterbabel.bak");
            Check(File.ReadAllBytes(backup).SequenceEqual(originalBytes), "备份必须是写入前的原始字节");
        });
    }

    private static void RunIdempotentAndUpdate(string work)
    {
        RunCase("apply-idempotent-and-update", () =>
        {
            var directory = Path.Combine(work, "idempotent");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "autoexec.cfg");
            const string userText = "host_writeconfig\r\n";
            var originalBytes = Utf8.GetBytes(userText);
            File.WriteAllBytes(path, originalBytes);

            var first = ManagedConfigFile.Apply(path, Recipe);
            Check(first.Ok, "第一次 Apply 应成功：" + first.Message);
            var afterFirst = File.ReadAllBytes(path);
            var backup = path + ManagedConfigFile.BackupSuffix;
            var backupAfterFirst = File.ReadAllBytes(backup);

            var second = ManagedConfigFile.Apply(path, Recipe);
            Check(second.Ok, "重复 Apply 应成功：" + second.Message);
            Check(!second.Changed, "内容未变时不应重复写盘");
            Check(second.Message.Contains("已是最新", StringComparison.Ordinal), "重复 Apply 文案应说明未改动：" + second.Message);
            Check(File.ReadAllBytes(path).SequenceEqual(afterFirst), "重复 Apply 后字节必须完全一致");
            Check(File.ReadAllBytes(backup).SequenceEqual(backupAfterFirst), "重复 Apply 不应覆盖已有备份");

            var updated = ManagedConfigFile.Apply(path, UpdatedRecipe);
            Check(updated.Ok, "更新块内容应成功：" + updated.Message);
            Check(updated.Changed, "内容变化应报告 Changed");
            var text = ReadUtf8NoBom(File.ReadAllBytes(path));
            foreach (var line in UpdatedRecipe) { Check(text.Contains(line, StringComparison.Ordinal), "更新后应包含：" + line); }
            Check(!text.Contains(Recipe[0], StringComparison.Ordinal), "旧块内容应被替换");
            CheckEqual(1, CountOccurrences(text, ManagedConfigFile.StartMarker), "文件中应只有一个起始标记");
            CheckEqual(1, CountOccurrences(text, ManagedConfigFile.EndMarker), "文件中应只有一个结束标记");
            CheckEqual(UpdatedRecipe.Length, ManagedConfigFile.CountBlockLines(text), "更新后的块内行数");
            Check(File.ReadAllBytes(backup).SequenceEqual(originalBytes), "更新块不应覆盖最初备份");

            var restored = ManagedConfigFile.Restore(path);
            Check(restored.Ok && restored.Changed, "还原应成功并改动文件：" + restored.Message);
            Check(File.ReadAllBytes(path).SequenceEqual(originalBytes), "还原后应回到最初原始字节");
            Check(!ManagedConfigFile.HasManagedBlock(ReadUtf8NoBom(File.ReadAllBytes(path))), "还原后不应再有标记");
        });
    }

    private static void RunRoundTripBytes(string work)
    {
        RunCase("encoding-roundtrip-restores-bytes", () =>
        {
            var cases = new (string Name, byte[] Bytes, string Text)[]
            {
                ("utf8-no-bom-crlf", Utf8.GetBytes("settings\r\ncl_x 1\r\n"), "settings\r\ncl_x 1\r\n"),
                ("utf8-no-bom-lf", Utf8.GetBytes("settings\ncl_x 1\n"), "settings\ncl_x 1\n"),
                ("utf8-bom-crlf", WithBom(new byte[] { 0xEF, 0xBB, 0xBF }, Utf8.GetBytes("中文设置\r\ncl_x 1\r\n")), "中文设置\r\ncl_x 1\r\n"),
                ("utf16le-bom-crlf", WithBom(new byte[] { 0xFF, 0xFE }, Utf16Le.GetBytes("中文设置\r\ncl_x 1\r\n")), "中文设置\r\ncl_x 1\r\n"),
                ("utf16be-bom-crlf", WithBom(new byte[] { 0xFE, 0xFF }, Utf16Be.GetBytes("中文设置\r\ncl_x 1\r\n")), "中文设置\r\ncl_x 1\r\n"),
            };

            foreach (var item in cases)
            {
                var directory = Path.Combine(work, "roundtrip", item.Name);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "autoexec.cfg");
                File.WriteAllBytes(path, item.Bytes);

                var applied = ManagedConfigFile.Apply(path, Recipe);
                Check(applied.Ok, $"[{item.Name}] Apply 应成功：{applied.Message}");
                Check(File.ReadAllBytes(path).Length > item.Bytes.Length, $"[{item.Name}] 写入后应变长");

                var restored = ManagedConfigFile.Restore(path);
                Check(restored.Ok, $"[{item.Name}] Restore 应成功：{restored.Message}");
                Check(restored.Changed, $"[{item.Name}] Restore 应报告 Changed");
                Check(File.ReadAllBytes(path).SequenceEqual(item.Bytes), $"[{item.Name}] 还原后必须与原字节完全一致");
            }
        });
    }

    private static void RunUtf16AndBom(string work)
    {
        RunCase("utf16le-and-utf8bom-roundtrip", () =>
        {
            // UTF-16LE：用户中文内容与 BOM 都要保住，块要能重复写而幂等。
            var directory = Path.Combine(work, "utf16");
            Directory.CreateDirectory(directory);
            var utf16Path = Path.Combine(directory, "autoexec.cfg");
            const string userText = "// 中文注释 😀\r\ncl_x 1\r\n";
            var utf16Bytes = WithBom(new byte[] { 0xFF, 0xFE }, Utf16Le.GetBytes(userText));
            File.WriteAllBytes(utf16Path, utf16Bytes);

            var first = ManagedConfigFile.Apply(utf16Path, Recipe);
            Check(first.Ok, "UTF-16LE Apply 应成功：" + first.Message);
            var written = File.ReadAllBytes(utf16Path);
            Check(written[0] == 0xFF && written[1] == 0xFE, "UTF-16LE BOM 必须保留");
            Check(written.AsSpan(2, utf16Bytes.Length - 2).SequenceEqual(utf16Bytes.AsSpan(2)), "UTF-16LE 块外字节必须原样保留");
            var text = Utf16Le.GetString(written, 2, written.Length - 2);
            CheckEqual(userText, ManagedConfigFile.Remove(text), "UTF-16LE 删块后应还原原文");

            var second = ManagedConfigFile.Apply(utf16Path, Recipe);
            Check(second.Ok && !second.Changed, "UTF-16LE 重复 Apply 应幂等：" + second.Message);
            Check(File.ReadAllBytes(utf16Path).SequenceEqual(written), "UTF-16LE 幂等后字节不变");

            var utf8BomPath = Path.Combine(directory, "bom.cfg");
            const string bomUserText = "// 用户中文 😀\r\nvolume 1\r\n";
            var bomBytes = WithBom(new byte[] { 0xEF, 0xBB, 0xBF }, Utf8.GetBytes(bomUserText));
            File.WriteAllBytes(utf8BomPath, bomBytes);

            var bomApply = ManagedConfigFile.Apply(utf8BomPath, Recipe);
            Check(bomApply.Ok, "UTF-8 BOM Apply 应成功：" + bomApply.Message);
            var bomWritten = File.ReadAllBytes(utf8BomPath);
            Check(bomWritten[0] == 0xEF && bomWritten[1] == 0xBB && bomWritten[2] == 0xBF, "UTF-8 BOM 必须保留");
            Check(bomWritten.AsSpan(3, bomBytes.Length - 3).SequenceEqual(bomBytes.AsSpan(3)), "UTF-8 BOM 块外字节必须原样保留");
            CheckEqual(bomUserText, ManagedConfigFile.Remove(Utf8Strict.GetString(bomWritten, 3, bomWritten.Length - 3)), "UTF-8 BOM 删块后应还原原文");

            var bomRestore = ManagedConfigFile.Restore(utf8BomPath);
            Check(bomRestore.Ok && bomRestore.Changed, "UTF-8 BOM Restore 应成功：" + bomRestore.Message);
            Check(File.ReadAllBytes(utf8BomPath).SequenceEqual(bomBytes), "UTF-8 BOM 还原后字节必须一致");
        });
    }

    private static void RunNewlineStylePreserved(string work)
    {
        RunCase("newline-style-preserved", () =>
        {
            var directory = Path.Combine(work, "newline");
            Directory.CreateDirectory(directory);

            var crlfPath = Path.Combine(directory, "crlf.cfg");
            File.WriteAllBytes(crlfPath, Utf8.GetBytes("a\r\nb\r\n"));
            var crlf = ManagedConfigFile.Apply(crlfPath, Recipe);
            Check(crlf.Ok, "CRLF 文件 Apply 应成功：" + crlf.Message);
            var crlfBytes = File.ReadAllBytes(crlfPath);
            Check(!HasLoneLf(crlfBytes), "CRLF 文件写入后不应出现孤立 LF（整文件不得被改成混合行尾）");
            CheckEqual(Recipe.Length + 4, CountCrlf(crlfBytes), "CRLF 数量（2 行用户内容 + 起始标记 + 块内 2 行 + 结束标记）");

            var lfPath = Path.Combine(directory, "lf.cfg");
            File.WriteAllBytes(lfPath, Utf8.GetBytes("a\nb\n"));
            var lf = ManagedConfigFile.Apply(lfPath, Recipe);
            Check(lf.Ok, "LF 文件 Apply 应成功：" + lf.Message);
            var lfBytes = File.ReadAllBytes(lfPath);
            Check(Array.IndexOf(lfBytes, (byte)'\r') < 0, "LF 文件的块必须继续用 LF，不得被改成 CRLF");
            var lfText = ReadUtf8NoBom(lfBytes);
            Check(lfText.StartsWith("a\nb\n" + ManagedConfigFile.StartMarker + "\n", StringComparison.Ordinal), "LF 文件应保留原有行尾并追加 LF 块");
            CheckEqual("a\nb\n", ManagedConfigFile.Remove(lfText), "LF 文件删块后应还原原文");

            var restored = ManagedConfigFile.Restore(lfPath);
            Check(restored.Ok && restored.Changed, "LF 文件 Restore 应成功：" + restored.Message);
            CheckEqual("a\nb\n", ReadUtf8NoBom(File.ReadAllBytes(lfPath)), "LF 文件还原后不应出现 CR");
        });
    }

    private static void RunIncompleteMarker(string work)
    {
        RunCase("incomplete-marker-refused", () =>
        {
            var directory = Path.Combine(work, "incomplete");
            Directory.CreateDirectory(directory);

            var onlyStart = Path.Combine(directory, "only-start.cfg");
            var onlyStartBytes = Utf8.GetBytes("bind \"x\"\r\n" + ManagedConfigFile.StartMarker + "\r\nuser after\r\n");
            File.WriteAllBytes(onlyStart, onlyStartBytes);
            var startResult = ManagedConfigFile.Apply(onlyStart, Recipe);
            Check(!startResult.Ok, "只有起始标记时必须拒绝写入");
            Check(startResult.Message.Contains("标记", StringComparison.Ordinal), "失败原因应提到标记：" + startResult.Message);
            Check(File.ReadAllBytes(onlyStart).SequenceEqual(onlyStartBytes), "拒绝写入时文件字节必须不变");
            Check(!File.Exists(onlyStart + ManagedConfigFile.BackupSuffix), "拒绝写入不应产生备份");

            var reversed = Path.Combine(directory, "reversed.cfg");
            var reversedBytes = Utf8.GetBytes(ManagedConfigFile.EndMarker + "\r\n" + ManagedConfigFile.StartMarker + "\r\n");
            File.WriteAllBytes(reversed, reversedBytes);
            var reversedResult = ManagedConfigFile.Apply(reversed, Recipe);
            Check(!reversedResult.Ok, "结束标记在前的顺序必须拒绝写入");
            Check(File.ReadAllBytes(reversed).SequenceEqual(reversedBytes), "顺序异常时文件字节必须不变");

            var duplicate = Path.Combine(directory, "duplicate.cfg");
            var duplicateText = ManagedConfigFile.Merge("user\r\n", Recipe) + ManagedConfigFile.Merge("", Recipe);
            var duplicateBytes = Utf8.GetBytes(duplicateText);
            File.WriteAllBytes(duplicate, duplicateBytes);
            Check(!ManagedConfigFile.IsWellFormedManagedBlock(duplicateText), "两个块应判定为不良构");
            var duplicateResult = ManagedConfigFile.Apply(duplicate, Recipe);
            Check(!duplicateResult.Ok, "出现两个块时必须拒绝写入（不猜哪个是真的）");
            Check(File.ReadAllBytes(duplicate).SequenceEqual(duplicateBytes), "双块文件字节必须不变");

            var restore = ManagedConfigFile.Restore(reversed);
            Check(!restore.Ok, "标记顺序异常时 Restore 应拒绝并报告");
            Check(File.ReadAllBytes(reversed).SequenceEqual(reversedBytes), "Restore 拒绝时文件字节必须不变");
        });
    }

    private static void RunReadOnly(string work)
    {
        RunCase("readonly-file-fails-with-message", () =>
        {
            var directory = Path.Combine(work, "readonly");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "autoexec.cfg");
            var originalBytes = Utf8.GetBytes("a\r\nb\r\n");
            File.WriteAllBytes(path, originalBytes);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            try
            {
                var result = ManagedConfigFile.Apply(path, Recipe);
                Check(!result.Ok, "只读文件 Apply 必须失败而不是假装成功");
                Check(result.Message.Contains("只读", StringComparison.Ordinal) || result.Message.Contains("权限", StringComparison.Ordinal), "失败原因应说明只读/权限：" + result.Message);
                Check(File.ReadAllBytes(path).SequenceEqual(originalBytes), "失败后文件字节必须不变");
                Check(!File.Exists(path + ManagedConfigFile.BackupSuffix), "失败时不应留下备份副作用");

                var restore = ManagedConfigFile.Restore(path);
                Check(!restore.Ok, "只读文件 Restore 也必须返回失败");
                Check(File.ReadAllBytes(path).SequenceEqual(originalBytes), "Restore 失败后文件字节必须不变");
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        });
    }

    private static void RunLegacyMigration(string work)
    {
        RunCase("legacy-migration-backup-and-replace", () =>
        {
            var directory = Path.Combine(work, "legacy");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "autoexec.cfg");
            var legacyText = string.Join("\r\n", ManagedConfigFile.LegacyLines) + "\r\n";
            var legacyBytes = Utf8.GetBytes(legacyText);
            File.WriteAllBytes(path, legacyBytes);

            var result = ManagedConfigFile.Apply(path, Recipe);
            Check(result.Ok, "旧格式迁移应成功：" + result.Message);
            Check(result.Migrated, "应报告 Migrated");
            Check(result.Changed, "应报告 Changed");
            Check(result.Message.Contains("迁移", StringComparison.Ordinal), "成功文案应说明迁移：" + result.Message);

            var text = ReadUtf8NoBom(File.ReadAllBytes(path));
            Check(text.StartsWith(ManagedConfigFile.StartMarker, StringComparison.Ordinal), "迁移后文件应以托管块开头");
            CheckEqual(string.Empty, ManagedConfigFile.Remove(text), "迁移后文件应只剩托管块（旧内容被整体替换）");
            foreach (var legacyLine in ManagedConfigFile.LegacyLines)
            {
                Check(!text.Contains(legacyLine, StringComparison.Ordinal), "迁移后不应残留旧行：" + legacyLine);
            }

            var backup = path + ManagedConfigFile.BackupSuffix;
            Check(File.Exists(backup), "迁移前必须备份");
            Check(File.ReadAllBytes(backup).SequenceEqual(legacyBytes), "迁移备份必须是旧文件的原始字节");

            // 用户自己写的、包含个别旧行的内容不是旧格式：只追加块，用户行原样保留。
            var userPath = Path.Combine(directory, "user.cfg");
            const string userText = "exec my.cfg\r\nrate 196608\r\n";
            File.WriteAllBytes(userPath, Utf8.GetBytes(userText));
            var userResult = ManagedConfigFile.Apply(userPath, Recipe);
            Check(userResult.Ok && !userResult.Migrated, "用户内容不应触发迁移：" + userResult.Message);
            var userApplied = ReadUtf8NoBom(File.ReadAllBytes(userPath));
            CheckEqual(userText, ManagedConfigFile.Remove(userApplied), "用户内容必须原样保留在块外");
            Check(userApplied.Contains("rate 196608", StringComparison.Ordinal), "用户自己的 rate 行不应被删");

            // 旧格式 + 额外一行 → 不迁移。
            Check(!ManagedConfigFile.IsNetLegacyBlock(legacyText + "exec extra.cfg\r\n"), "旧格式多一行不应识别为旧格式");
        });
    }

    private static void RunBlockInMiddle(string work)
    {
        RunCase("block-in-middle-keeps-trailing-content", () =>
        {
            var directory = Path.Combine(work, "middle");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "autoexec.cfg");
            var block = ManagedConfigFile.Merge(string.Empty, Recipe);
            var originalText = "before\r\n" + block + "after\r\n";
            File.WriteAllBytes(path, Utf8.GetBytes(originalText));

            var applied = ManagedConfigFile.Apply(path, UpdatedRecipe);
            Check(applied.Ok, "块在中间时 Apply 应成功：" + applied.Message);
            var text = ReadUtf8NoBom(File.ReadAllBytes(path));
            Check(text.StartsWith("before\r\n", StringComparison.Ordinal), "块前内容应原样保留");
            Check(text.EndsWith("after\r\n", StringComparison.Ordinal), "块后内容应原样保留");
            CheckEqual(1, CountOccurrences(text, ManagedConfigFile.StartMarker), "仍应只有一个块");
            foreach (var line in UpdatedRecipe) { Check(text.Contains(line, StringComparison.Ordinal), "块应更新为：" + line); }
            CheckEqual("before\r\nafter\r\n", ManagedConfigFile.Remove(text), "删块后应保留块前后内容");

            var restored = ManagedConfigFile.Restore(path);
            Check(restored.Ok && restored.Changed, "块在中间时 Restore 应成功：" + restored.Message);
            CheckEqual("before\r\nafter\r\n", ReadUtf8NoBom(File.ReadAllBytes(path)), "Restore 应只删块，保留块前后内容");
        });
    }

    private static void RunInvalidUtf8Preserved(string work)
    {
        RunCase("invalid-utf8-bytes-preserved", () =>
        {
            // GBK 编码的「中文」= D6 D0 CE C4，不是合法 UTF-8。按 UTF-8 解码会替换成 U+FFFD 并写坏
            // 用户内容；这里的契约是退回单字节编码（保字节），块外用原始字节逐字节回写。
            var directory = Path.Combine(work, "gbk");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "autoexec.cfg");
            var originalBytes = new byte[]
            {
                0xD6, 0xD0, 0xCE, 0xC4, (byte)'\r', (byte)'\n',
                (byte)'c', (byte)'l', (byte)'_', (byte)'x', (byte)' ', (byte)'1', (byte)'\r', (byte)'\n',
            };
            File.WriteAllBytes(path, originalBytes);

            var info = ManagedConfigFile.Inspect(path);
            Check(info.Ok, "Inspect 应成功：" + info.Message);
            CheckEqual("latin1", info.Encoding, "非 UTF-8 字节应退回单字节编码");

            // 非 Latin-1 的块内容（例如中文注释）写进单字节文件会变成 '?'，必须拒绝而不是写坏。
            var refused = ManagedConfigFile.Apply(path, Recipe);
            Check(!refused.Ok, "单字节文件 + 非 Latin-1 块内容必须拒绝写入");
            Check(refused.Message.Contains("Latin-1", StringComparison.Ordinal), "拒绝原因应说明 Latin-1：" + refused.Message);
            Check(File.ReadAllBytes(path).SequenceEqual(originalBytes), "拒绝时文件字节必须不变");

            var applied = ManagedConfigFile.Apply(path, AsciiRecipe);
            Check(applied.Ok, "非 UTF-8 文件 + ASCII 块 Apply 应成功：" + applied.Message);
            var writtenBytes = File.ReadAllBytes(path);
            Check(writtenBytes.AsSpan(0, originalBytes.Length).SequenceEqual(originalBytes), "块外非 UTF-8 字节必须逐字节保留");

            var restored = ManagedConfigFile.Restore(path);
            Check(restored.Ok && restored.Changed, "非 UTF-8 文件 Restore 应成功：" + restored.Message);
            Check(File.ReadAllBytes(path).SequenceEqual(originalBytes), "还原后必须与原字节完全一致");
        });
    }

    private static void RunMissingDirectoryAndNoOpRestore(string work)
    {
        RunCase("missing-directory-and-no-op-restore", () =>
        {
            var missingDirectory = Path.Combine(work, "definitely-missing");
            var missingPath = Path.Combine(missingDirectory, "autoexec.cfg");
            var result = ManagedConfigFile.Apply(missingPath, Recipe);
            Check(!result.Ok, "目录不存在时必须失败");
            Check(result.Message.Contains("目录不存在", StringComparison.Ordinal), "失败原因应说明目录不存在：" + result.Message);
            Check(!Directory.Exists(missingDirectory), "失败时不应凭空创建目录");

            var missingRestore = ManagedConfigFile.Restore(missingPath);
            Check(missingRestore.Ok && !missingRestore.Changed, "文件不存在时 Restore 应为无需还原");
            Check(missingRestore.Message.Contains("无需还原", StringComparison.Ordinal), "Restore 文案应说明无需还原：" + missingRestore.Message);

            var directory = Path.Combine(work, "noblock");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "autoexec.cfg");
            var originalBytes = Utf8.GetBytes("a\r\nb\r\n");
            File.WriteAllBytes(path, originalBytes);
            var noBlock = ManagedConfigFile.Restore(path);
            Check(noBlock.Ok && !noBlock.Changed, "没有块时 Restore 应为无需还原");
            Check(noBlock.Message.Contains("无需还原", StringComparison.Ordinal), "无块文案应说明无需还原：" + noBlock.Message);
            Check(File.ReadAllBytes(path).SequenceEqual(originalBytes), "无块时 Restore 不应改文件");
        });
    }

    private static void RunInspect(string work)
    {
        RunCase("inspect-reports-state", () =>
        {
            var directory = Path.Combine(work, "inspect");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "autoexec.cfg");

            var missing = ManagedConfigFile.Inspect(path);
            Check(missing.Ok && !missing.Exists, "文件不存在时 Inspect 应 Ok 且 Exists=false：" + missing.Message);
            Check(missing.Message.Contains("不存在", StringComparison.Ordinal), "缺失文案：" + missing.Message);

            File.WriteAllBytes(path, Utf8.GetBytes("a\r\nb\r\n"));
            var before = ManagedConfigFile.Inspect(path);
            Check(before.Ok && !before.Exists && !before.WellFormed, "无块时应 Exists=false");
            CheckEqual(0, before.LineCount, "无块时行数应为 0");

            var applied = ManagedConfigFile.Apply(path, UpdatedRecipe);
            Check(applied.Ok, "Apply 应成功：" + applied.Message);
            var after = ManagedConfigFile.Inspect(path);
            Check(after.Ok && after.Exists && after.WellFormed, "写入后应报告良构块：" + after.Message);
            CheckEqual(UpdatedRecipe.Length, after.LineCount, "Inspect 行数");
            CheckEqual("utf-8", after.Encoding, "Inspect 编码");

            var blank = ManagedConfigFile.Inspect(null);
            Check(!blank.Ok, "路径为空时 Inspect 应失败");
        });
    }

    // --- 断言 / 工具 -------------------------------------------------------------------------

    private static void RunCase(string name, Action body)
    {
        var before = _assertions;
        var watch = Stopwatch.StartNew();
        try
        {
            body();
            watch.Stop();
            Cases.Add(new CaseResult(name, true, _assertions - before, $"{watch.ElapsedMilliseconds} ms"));
            Console.WriteLine($"PASS {name}（{_assertions - before} 断言）");
        }
        catch (Exception ex)
        {
            watch.Stop();
            _failed++;
            Cases.Add(new CaseResult(name, false, _assertions - before, ex.Message));
            Console.WriteLine($"FAIL {name}：{ex.Message}");
        }
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static void CheckEqual<T>(T expected, T actual, string what)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{what}：期望 <{expected}>，实际 <{actual}>");
        }
    }

    private static bool StartsWithBom(byte[] bytes) =>
        (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        || (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)));

    private static byte[] WithBom(byte[] bom, byte[] body)
    {
        var bytes = new byte[bom.Length + body.Length];
        bom.CopyTo(bytes, 0);
        body.CopyTo(bytes, bom.Length);
        return bytes;
    }

    private static string ReadUtf8NoBom(byte[] bytes)
    {
        Check(!StartsWithBom(bytes), "预期无 BOM 的 UTF-8 文件，实际发现 BOM");
        return Utf8Strict.GetString(bytes);
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static int CountCrlf(byte[] bytes)
    {
        var count = 0;
        for (var i = 1; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n' && bytes[i - 1] == (byte)'\r') { count++; }
        }

        return count;
    }

    private static bool HasLoneLf(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r')) { return true; }
        }

        return false;
    }

    private static void DeleteTree(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) { return; }
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch (Exception) { /* 尽力而为 */ }
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("清理临时目录失败：" + ex.Message);
        }
    }
}
