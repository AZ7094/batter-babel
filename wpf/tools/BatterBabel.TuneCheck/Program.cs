namespace BatterBabel.TuneCheck;

using System.Globalization;
using System.Text;
using System.Text.Json;
using BatterBabel.Core.GameConfig;
using BatterBabel.Core.Tuning;
using Microsoft.Win32;

/// <summary>
/// 系统调优自检工具（对齐 Tauri 0.10.3 语义）。
///
///   --inspect          只读：打印 9 个调优面的当前值 + 快照状态，不改任何东西。
///   --selftest         离线：用内存假系统跑通「抓快照 → 应用 → 读回 → 还原 → 清快照」全链路，
///                      以及 delta / items / log / status / null-vs-0 断言。不碰真实系统。
///   --write-selftest   显式开关的真实写入自检：只在可回滚的探针键
///                      HKLM\SOFTWARE\BatterBabel\TuneCheckProbe 上写 DWORD → 读回 → 删除 → 删键，
///                      并只读探测 netsh/powercfg。需要管理员；不做整机调优再还原（那会短暂改动真实系统设置）。
///
/// 退出码：0 通过 / 1 失败 / 2 需要管理员（未执行写自检）。
/// </summary>
internal static class Program
{
    private static readonly List<string> Results = new();
    private static int _failures;
    private static string? _reportPath;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // 控制台不支持 UTF-8 时继续，中文可能显示为问号但不影响退出码。
        }

        var mode = args.Length == 0 ? "--help" : args[0].ToLowerInvariant();
        _reportPath = ArgValue(args, "--out");

        switch (mode)
        {
            case "--inspect":
                return RunInspectWithReport();
            case "--selftest":
                return await RunSelfTest().ConfigureAwait(false);
            case "--write-selftest":
                return RunWriteSelfTest();
            case "--help" or "-h" or "/?":
                PrintHelp();
                return 0;
            default:
                Console.Error.WriteLine($"未知参数：{args[0]}");
                PrintHelp();
                return 1;
        }
    }

    // ───────────────────────────────── --inspect ─────────────────────────────────

    /// <summary>--inspect 的 --out 版本：把只读报告同时落成 JSON（不改变任何系统设置）。</summary>
    private static int RunInspectWithReport()
    {
        if (_reportPath is null)
        {
            return RunInspect();
        }

        var buffer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(buffer);
        int code;
        try
        {
            code = RunInspect();
        }
        finally
        {
            Console.SetOut(original);
        }

        var text = buffer.ToString();
        Console.Write(text);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_reportPath))!);
            var payload = new
            {
                generatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                mode = "inspect",
                exitCode = code,
                lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            };
            File.WriteAllText(_reportPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine($"报告：{_reportPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"报告写入失败：{ex.Message}");
        }

        return code;
    }

    private static int RunInspect()
    {
        Console.WriteLine("== 系统调优当前值（只读） ==");
        Console.WriteLine($"管理员：{(SystemTuning.IsAdministrator() ? "是" : "否")}（{(SystemTuning.NeedsElevation ? SystemTuning.ElevationMessage : "无需提权即可读")}）");
        Console.WriteLine($"快照文件：{SystemTuning.DefaultSnapshotPath}");

        try
        {
            var snapshot = TuneStateStore.Load();
            Console.WriteLine(snapshot is null
                ? "快照状态：不存在（尚未调优，或上次还原已消费）"
                : $"快照状态：存在（savedAt={snapshot.SavedAt}，appliedTier={(string.IsNullOrEmpty(snapshot.AppliedTier) ? "(未记录)" : snapshot.AppliedTier)}，管理配置 {snapshot.ManagedConfigFiles.Count} 个）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.WriteLine($"快照状态：读取失败（{ex.Message}）");
        }

        var boundary = new WindowsTuneBoundary();
        IReadOnlyList<TuneAdapterInfo> adapters;
        try
        {
            adapters = boundary.Adapters();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"读取网卡失败：{ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"物理网卡：{adapters.Count} 块");
        foreach (var adapter in adapters)
        {
            Console.WriteLine($"  - {adapter.Name}  guid={(adapter.Guid ?? "(无)")}  media={adapter.MediaType}  wifi={(adapter.IsWifi ? "是" : "否")}");
            foreach (var (keyword, value) in adapter.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var valid = adapter.ValidValues.TryGetValue(keyword, out var list) ? string.Join("/", list) : "(无)";
                Console.WriteLine($"      {keyword} = {value.Display}  （合法值 {valid}）");
            }

            Console.WriteLine($"      AllowComputerToTurnOffDevice = {adapter.PowerTurnOff.Display}");
        }

        Console.WriteLine();
        Console.WriteLine("① Nagle（每网卡三个值）");
        foreach (var adapter in adapters)
        {
            if (string.IsNullOrEmpty(adapter.Guid) || !adapter.HasTcpipKey)
            {
                continue;
            }

            var values = new List<string>();
            foreach (var name in NetProfiles.NagleValueNames)
            {
                var target = RegistryValueTarget.LocalMachine(
                    $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapter.Guid}", name);
                values.Add($"{name}={boundary.ReadRegistry(target).Display}");
            }

            Console.WriteLine($"  {adapter.Name}：{string.Join("，", values)}");
        }

        Console.WriteLine("② 网卡高级属性 / ⑦ Wi-Fi 电源 / ⑧ 后台占网：见上面网卡清单与下方扫描");
        Console.WriteLine($"③ autotuninglevel = {boundary.ReadTcpGlobal("autotuninglevel") ?? "(读取失败)"}");
        Console.WriteLine($"④ rss = {boundary.ReadTcpGlobal("rss") ?? "(读取失败)"}");

        var throttleRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        var throttleIndex = boundary.ReadRegistry(RegistryValueTarget.LocalMachine(throttleRoot, ThrottleKeys.NetworkThrottlingIndex));
        var responsiveness = boundary.ReadRegistry(RegistryValueTarget.LocalMachine(throttleRoot, ThrottleKeys.SystemResponsiveness));
        Console.WriteLine($"⑤ NetworkThrottlingIndex = {throttleIndex.Display}（0xFFFFFFFF/4294967295 = 关闭限流）");
        Console.WriteLine($"   SystemResponsiveness = {responsiveness.Display}");
        Console.WriteLine($"⑥ Wi-Fi 电源：AC={boundary.ReadWifiPowerIndex(ac: true) ?? "(读取失败)"}  DC={boundary.ReadWifiPowerIndex(ac: false) ?? "(读取失败)"}");

        var hogs = new List<string>();
        foreach (var process in boundary.ProcessNames())
        {
            if (NetProfiles.HogNames.TryGetValue(process, out var label) && !hogs.Contains(label))
            {
                hogs.Add(label);
            }
        }

        Console.WriteLine($"⑧ 后台占网程序：{(hogs.Count == 0 ? "未发现" : string.Join("，", hogs))}（只报告，不会结束进程）");
        Console.WriteLine("⑨ 游戏配置托管块：由 App 按游戏安装路径写入，--inspect 不枚举磁盘");
        Console.WriteLine($"提示：{NetProfiles.TierIds.Count} 个档位（{string.Join(" / ", NetProfiles.TierIds)}），latency=低延迟，throughput=吞吐优先");
        return 0;
    }

    // ───────────────────────────────── --selftest（离线） ─────────────────────────────────

    private static async Task<int> RunSelfTest()
    {
        Console.WriteLine("== 离线自检（内存假系统，不碰真实注册表/netsh/powercfg） ==");
        var root = Path.Combine(Path.GetTempPath(), "batterbabel-tunecheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        try
        {
            CheckStatusCodes();
            CheckTierCatalog();
            CheckDelta();
            await CheckFullRoundTrip(root).ConfigureAwait(false);
            await CheckSameTierIsNoOp(root).ConfigureAwait(false);
            await CheckRestoreTwice(root).ConfigureAwait(false);
            await CheckThroughputRestoreNagle(root).ConfigureAwait(false);
            await CheckMissingAdapterKeyword(root).ConfigureAwait(false);
            await CheckSnapshotFailClosed(root).ConfigureAwait(false);
            await CheckGameConfigRoundTrip(root).ConfigureAwait(false);
            await CheckWriteFailureBecomesPartial(root).ConfigureAwait(false);
            await CheckItemsUniqueAndLogShape(root).ConfigureAwait(false);
            CheckElevationText();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"（临时目录清理失败，不影响结果：{root}）");
            }
        }

        return Finish();
    }

    private static void CheckStatusCodes()
    {
        Equal("status APPLIED", "APPLIED", TuneStatus.Applied);
        Equal("status PARTIAL", "PARTIAL", TuneStatus.Partial);
        Equal("status RESTORED", "RESTORED", TuneStatus.Restored);
        Equal("status NO_SNAPSHOT", "NO_SNAPSHOT", TuneStatus.NoSnapshot);
        Equal("status BLOCKED", "BLOCKED", TuneStatus.Blocked);
    }

    private static void CheckTierCatalog()
    {
        Equal("档位数", 2, NetProfiles.TierIds.Count);
        Equal("latency Nagle", "true", NetProfiles.GetTier("latency").Get("Nagle")!.Value.ToString());
        True("throughput Nagle 是 Restore", NetProfiles.GetTier("throughput").Get("Nagle")!.Value.IsRestore);
        Equal("Nagle 值名", "TcpAckFrequency,TCPNoDelay,TcpDelAckTicks", string.Join(",", NetProfiles.NagleValueNames));
        Equal("TcpDelAckTicks 写 0", 0, NetProfiles.NagleValueFor("TcpDelAckTicks"));
        Equal("TcpAckFrequency 写 1", 1, NetProfiles.NagleValueFor("TcpAckFrequency"));
        Equal("网卡关键字映射", "*InterruptModeration,*EEE,*FlowControl",
            string.Join(",", new[] { "InterruptModeration", "Eee", "FlowControl" }.Select(k => NetProfiles.AdapterKeywords[k])));
        True("CS2 配方有配置文件", NetProfiles.Recipe("cs2").ConfigFiles.Count == 1);
        True("limbus 走 throughput", NetProfiles.Recipe("limbus").Tier == "throughput");
        True("未知游戏走 latency 且无配置", NetProfiles.Recipe("steam-999").Tier == "latency" && NetProfiles.Recipe("steam-999").ConfigFiles.Count == 0);
    }

    private static void CheckDelta()
    {
        var latency = NetProfiles.Delta(null, "latency");
        Equal("从零到 latency：9 个 Set", 9, latency.Set.Count);
        Equal("从零到 latency：0 个 Restore", 0, latency.Restore.Count);

        var same = NetProfiles.Delta("latency", "latency");
        True("同档位 delta 为空", same.Set.Count == 0 && same.Restore.Count == 0);

        var throughput = NetProfiles.Delta(null, "throughput");
        Equal("throughput 的 Nagle 进 Restore", "Nagle", string.Join(",", throughput.Restore));
        True("throughput 的 Set 不含 Nagle", !throughput.Set.Contains("Nagle"));
    }

    private static async Task CheckFullRoundTrip(string root)
    {
        var fake = FakeBoundary.Standard();
        var statePath = Path.Combine(root, "roundtrip", "tune-state.json");
        var engine = new TuneEngine(fake, statePath: statePath);

        var applied = engine.Apply(new TuneRequest(ScanBackground: false));
        Equal("apply 状态", TuneStatus.Applied, applied.Status);
        True("apply Ok", applied.Ok);
        True("apply 不要求重启", !applied.RebootRequired);
        True("快照已写入", File.Exists(statePath));

        var expectedIds = NetProfiles.TuneOrder.Select(k => "net-set-" + k).ToArray();
        Equal("apply item ids", string.Join(",", expectedIds), string.Join(",", applied.ItemList.Select(i => i.Id)));
        True("apply items 全 ok", applied.ItemList.All(i => i.Ok));
        True("apply log 以完成行结尾", applied.LogLines.Any(l => l.Contains("系统调优完成")));
        True("apply 有 changes", applied.Changes.Count > 0);

        var state = TuneStateStore.Load(statePath);
        True("快照可读回", state is not null);
        Equal("快照 appliedTier", "latency", state!.AppliedTier);
        Equal("快照 savedAt 非空", true, !string.IsNullOrWhiteSpace(state.SavedAt));
        Equal("快照 Nagle 原值 TcpAckFrequency", "2", state.Nagle["{G1}"]["TcpAckFrequency"].Display);
        True("快照 Nagle 原值 TCPNoDelay 为 null", state.Nagle["{G1}"]["TCPNoDelay"].IsEmpty);
        Equal("快照 tcpGlobal.autotuninglevel", "normal", state.TcpGlobalValue("autotuninglevel").Display);
        Equal("快照 throttle 原值", "10", state.Throttle[ThrottleKeys.NetworkThrottlingIndex].Display);
        Equal("快照 Wi-Fi AC 原值", "1", state.WifiPower.Ac.Display);
        Equal("快照 adapterProps 原值", "1", state.AdapterProps["Ethernet"]["*InterruptModeration"].Display);
        True("快照 adapterPower 原值 Enabled", state.AdapterPower["Ethernet"].Display == "Enabled");

        // 写后读回：目标值真的落到"系统"上了。
        Equal("Nagle TcpAckFrequency=1", "1", fake.Registry[FakeBoundary.InterfaceKey("{G1}", "TcpAckFrequency")].Display);
        Equal("Nagle TCPNoDelay=1", "1", fake.Registry[FakeBoundary.InterfaceKey("{G1}", "TCPNoDelay")].Display);
        Equal("Nagle TcpDelAckTicks=0", "0", fake.Registry[FakeBoundary.InterfaceKey("{G1}", "TcpDelAckTicks")].Display);
        Equal("网卡 InterruptModeration 取合法值端点[0]", "0", fake.AdapterProps("Ethernet", "*InterruptModeration"));
        Equal("网卡 EEE 取端点[0]", "0", fake.AdapterProps("Ethernet", "*EEE"));
        Equal("网卡 FlowControl 取端点[0]", "0", fake.AdapterProps("Ethernet", "*FlowControl"));
        Equal("tcp global autotuninglevel", "normal", fake.Tcp["autotuninglevel"]);
        Equal("tcp global rss", "enabled", fake.Tcp["rss"]);
        Equal("throttle NetworkThrottlingIndex", "4294967295", fake.Registry[FakeBoundary.ThrottleKey(ThrottleKeys.NetworkThrottlingIndex)].Display);
        Equal("throttle SystemResponsiveness", "10", fake.Registry[FakeBoundary.ThrottleKey(ThrottleKeys.SystemResponsiveness)].Display);
        Equal("网卡电源", "Disabled", fake.AdapterPowerValue("Ethernet"));
        Equal("Wi-Fi 电源 AC", "0", fake.Wifi["ac"]);
        Equal("Wi-Fi 电源 DC", "0", fake.Wifi["dc"]);

        var restored = engine.Restore();
        Equal("restore 状态", TuneStatus.Restored, restored.Status);
        True("restore Ok", restored.Ok);
        True("restore 不要求重启", !restored.RebootRequired);
        Equal("restore item ids", "tcp-nagle,tcp-global,adapter,adapter-power,throttle,wifi-power,game-configs",
            string.Join(",", restored.ItemList.Select(i => i.Id)));
        True("restore items 全 ok", restored.ItemList.All(i => i.Ok));
        True("restore 后快照已删除", !File.Exists(statePath));

        // null → 删值；0 → 写回 0；数字 → 写回数字。
        Equal("还原 TcpAckFrequency=2", "2", fake.Registry[FakeBoundary.InterfaceKey("{G1}", "TcpAckFrequency")].Display);
        True("还原 TCPNoDelay：原值缺失 → 删值", !fake.Registry.ContainsKey(FakeBoundary.InterfaceKey("{G1}", "TCPNoDelay")));
        Equal("还原 TcpDelAckTicks=0（0 写回 0，不删值）", "0", fake.Registry[FakeBoundary.InterfaceKey("{G1}", "TcpDelAckTicks")].Display);
        Equal("还原网卡属性", "1", fake.AdapterProps("Ethernet", "*InterruptModeration"));
        Equal("还原网卡电源", "Enabled", fake.AdapterPowerValue("Ethernet"));
        Equal("还原 throttle index", "10", fake.Registry[FakeBoundary.ThrottleKey(ThrottleKeys.NetworkThrottlingIndex)].Display);
        Equal("还原 throttle responsiveness", "20", fake.Registry[FakeBoundary.ThrottleKey(ThrottleKeys.SystemResponsiveness)].Display);
        Equal("还原 Wi-Fi AC", "1", fake.Wifi["ac"]);
        Equal("还原 Wi-Fi DC", "2", fake.Wifi["dc"]);
        True("restore log 含清理行", restored.LogLines.Any(l => l.Contains("快照已清除")));
    }

    private static async Task CheckSameTierIsNoOp(string root)
    {
        var fake = FakeBoundary.Standard();
        var statePath = Path.Combine(root, "same-tier", "tune-state.json");
        var engine = new TuneEngine(fake, statePath: statePath);

        var first = engine.Apply(new TuneRequest(ScanBackground: false));
        True("首次 apply 有 net-set 项", first.ItemList.Any(i => i.Id.StartsWith("net-set-", StringComparison.Ordinal)));

        var writesBefore = fake.RegistryWriteCount;
        var second = engine.Apply(new TuneRequest(ScanBackground: false));
        Equal("同档位重跑：不再有 net-set 项", 0, second.ItemList.Count(i => i.Id.StartsWith("net-set-", StringComparison.Ordinal)));
        Equal("同档位重跑：零写入", writesBefore, fake.RegistryWriteCount);
        Equal("同档位重跑：APPLIED", TuneStatus.Applied, second.Status);
    }

    private static Task CheckRestoreTwice(string root)
    {
        var fake = FakeBoundary.Standard();
        var statePath = Path.Combine(root, "twice", "tune-state.json");
        var engine = new TuneEngine(fake, statePath: statePath);
        engine.Apply(new TuneRequest(ScanBackground: false));

        var first = engine.Restore();
        Equal("第一次还原", TuneStatus.Restored, first.Status);

        var second = engine.Restore();
        Equal("第二次还原状态", TuneStatus.NoSnapshot, second.Status);
        True("第二次还原 Ok=false", !second.Ok);
        Equal("第二次还原 items 为空", 0, second.ItemList.Count);
        True("第二次还原 log 说明没有快照", second.LogLines.Any(l => l.Contains("未找到快照")));
        return Task.CompletedTask;
    }

    private static Task CheckThroughputRestoreNagle(string root)
    {
        var fake = FakeBoundary.Standard();
        var statePath = Path.Combine(root, "throughput", "tune-state.json");
        var engine = new TuneEngine(fake, statePath: statePath);

        var applied = engine.Apply(new TuneRequest(GameId: "limbus", ScanBackground: false));
        Equal("throughput 状态", TuneStatus.Applied, applied.Status);
        True("throughput 有 net-restore-Nagle", applied.ItemList.Any(i => i.Id == "net-restore-Nagle"));
        True("throughput 没有 net-set-Nagle", applied.ItemList.All(i => i.Id != "net-set-Nagle"));
        Equal("throughput Nagle 原值 2 未被覆盖", "2", fake.Registry[FakeBoundary.InterfaceKey("{G1}", "TcpAckFrequency")].Display);
        Equal("throughput InterruptModeration 取端点[last]", "1", fake.AdapterProps("Ethernet", "*InterruptModeration"));
        Equal("快照 appliedTier 记录 throughput", "throughput", TuneStateStore.Load(statePath)!.AppliedTier);
        return Task.CompletedTask;
    }

    private static Task CheckMissingAdapterKeyword(string root)
    {
        var fake = FakeBoundary.Standard();
        fake.Cards[0].Properties.Remove("*EEE");
        fake.Cards[0].Valid.Remove("*EEE");
        var engine = new TuneEngine(fake, statePath: Path.Combine(root, "missing-eee", "tune-state.json"));

        var applied = engine.Apply(new TuneRequest(ScanBackground: false));
        var eee = applied.ItemList.First(i => i.Id == "net-set-Eee");
        True("缺 *EEE 时该项 ok=false", !eee.Ok);
        True("缺 *EEE 时 detail 说明 skipped", eee.Detail.StartsWith("skipped: no adapter exposes *EEE", StringComparison.Ordinal));
        Equal("有失败项 → PARTIAL", TuneStatus.Partial, applied.Status);
        True("有失败项 → Ok=false", !applied.Ok);
        Equal("快照 appliedTier 记空串（下次重算全量 delta）", string.Empty, TuneStateStore.Load(engine.SnapshotPath)!.AppliedTier);
        return Task.CompletedTask;
    }

    private static Task CheckSnapshotFailClosed(string root)
    {
        var fake = FakeBoundary.Standard();
        // 把"目录"位置先放一个文件：Directory.CreateDirectory 必然失败 → 模拟快照写不进去。
        var blocker = Path.Combine(root, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var engine = new TuneEngine(fake, statePath: Path.Combine(blocker, "tune-state.json"));

        var applied = engine.Apply(new TuneRequest(ScanBackground: false));
        Equal("快照失败状态", TuneStatus.Blocked, applied.Status);
        True("快照失败 Ok=false", !applied.Ok);
        Equal("快照失败：零注册表写入", 0, fake.RegistryWriteCount);
        True("快照失败 log 说明已中止", applied.LogLines.Any(l => l.Contains("已中止")));
        return Task.CompletedTask;
    }

    private static Task CheckGameConfigRoundTrip(string root)
    {
        var fake = FakeBoundary.Standard();
        var store = new FakeConfigStore();
        var gamePath = Path.Combine(root, "cs2", "game", "csgo", "cfg", "autoexec.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(gamePath)!);
        var engine = new TuneEngine(fake, store, statePath: Path.Combine(root, "game", "tune-state.json"));

        var applied = engine.Apply(new TuneRequest(
            GameId: "cs2",
            ConfigTargets: new[] { new TuneConfigTarget(gamePath, NetProfiles.Cs2ConfigLines) },
            ScanBackground: false));
        var item = applied.ItemList.First(i => i.Id == "game-config");
        True("游戏配置写入成功", item.Ok);
        True("游戏配置 detail 含路径", item.Detail.Contains(gamePath, StringComparison.OrdinalIgnoreCase));
        True("配置内容进 fake store", store.Files.TryGetValue(gamePath, out var content) && content == string.Join("\n", NetProfiles.Cs2ConfigLines));
        True("managedConfigFiles 已记账", TuneStateStore.Load(engine.SnapshotPath)!.ManagedConfigFiles.Contains(gamePath));

        var restored = engine.Restore();
        True("还原含 game-configs", restored.ItemList.Any(i => i.Id == "game-configs" && i.Ok));
        True("托管块已移除", !store.Files.ContainsKey(gamePath));
        return Task.CompletedTask;
    }

    private static Task CheckWriteFailureBecomesPartial(string root)
    {
        var fake = FakeBoundary.Standard();
        fake.FailRegistry = target => target.Name == "TcpAckFrequency";
        var engine = new TuneEngine(fake, statePath: Path.Combine(root, "fail-write", "tune-state.json"));

        var applied = engine.Apply(new TuneRequest(ScanBackground: false));
        Equal("写失败 → PARTIAL", TuneStatus.Partial, applied.Status);
        var nagle = applied.ItemList.First(i => i.Id == "net-set-Nagle");
        True("Nagle item ok=false", !nagle.Ok);
        True("Nagle detail 带失败原因", nagle.Detail.Contains("失败", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static Task CheckItemsUniqueAndLogShape(string root)
    {
        var fake = FakeBoundary.Standard();
        fake.Processes.AddRange(new[] { "Thunder", "qbittorrent", "notepad" });
        var engine = new TuneEngine(fake, statePath: Path.Combine(root, "bg", "tune-state.json"));

        var applied = engine.Apply(new TuneRequest(ScanBackground: true));
        var bg = applied.ItemList.First(i => i.Id == "bg-programs");
        True("后台占网找到迅雷/qBittorrent", bg.FoundList.Contains("迅雷") && bg.FoundList.Contains("qBittorrent"));
        True("后台占网 ok=false（只提示）", !bg.Ok);
        Equal("后台占网不影响 APPLIED", TuneStatus.Applied, applied.Status);
        Equal("item id 不重复", applied.ItemList.Count, applied.ItemList.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count());
        True("log 每行非空", applied.LogLines.All(l => !string.IsNullOrWhiteSpace(l)));
        return Task.CompletedTask;
    }

    private static void CheckElevationText()
    {
        True("提权文案含“需要管理员”", SystemTuning.ElevationMessage.Contains("需要管理员", StringComparison.Ordinal));
        Equal("NeedsElevation 与 IsAdministrator 互斥", !SystemTuning.NeedsElevation, SystemTuning.IsAdministrator());
    }

    // ───────────────────────────────── --write-selftest ─────────────────────────────────

    private static int RunWriteSelfTest()
    {
        Console.WriteLine("== 写入自检（仅探针键，写读回后删除；不改任何调优面） ==");
        if (!SystemTuning.IsAdministrator())
        {
            Console.WriteLine($"跳过：{SystemTuning.ElevationMessage}");
            Results.Add("SKIP 写入自检：需要管理员");
            _failures++;
            return Finish(2);
        }

        const string probeKey = @"SOFTWARE\BatterBabel\TuneCheckProbe";
        RegistryKey? key = null;
        try
        {
            key = Registry.LocalMachine.CreateSubKey(probeKey, writable: true);
            True("探针键已创建", key is not null);
            if (key is null)
            {
                return Finish();
            }

            key.SetValue("Probe", unchecked((int)0x5A5A), RegistryValueKind.DWord);
            var read = key.GetValue("Probe", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            Equal("DWORD 写后读回", (uint)0x5A5A, (uint)(int)read!);

            key.DeleteValue("Probe", throwOnMissingValue: false);
            True("删除后读回为空", key.GetValue("Probe") is null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Fail("探针键写入", ex.Message);
        }
        finally
        {
            key?.Dispose();
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(probeKey, throwOnMissingSubKey: false);
                True("探针键已清理", Registry.LocalMachine.OpenSubKey(probeKey) is null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Fail("探针键清理", ex.Message);
            }
        }

        // 只读面：确认 netsh / powercfg 能读（写回路径由离线自检覆盖）。
        var boundary = new WindowsTuneBoundary();
        var autoTuning = boundary.ReadTcpGlobal("autotuninglevel");
        var rss = boundary.ReadTcpGlobal("rss");
        Console.WriteLine($"（只读）autotuninglevel={autoTuning ?? "(读取失败)"} rss={rss ?? "(读取失败)"} AC={boundary.ReadWifiPowerIndex(true) ?? "(读取失败)"} DC={boundary.ReadWifiPowerIndex(false) ?? "(读取失败)"}");
        True("netsh 可读 autotuninglevel", autoTuning is not null);
        True("netsh 可读 rss", rss is not null);
        return Finish();
    }

    // ───────────────────────────────── 断言与报告 ─────────────────────────────────

    private static void Equal<T>(string name, T expected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Pass(name, $"{actual}");
        }
        else
        {
            Fail(name, $"期望 {expected}，实际 {actual}");
        }
    }

    private static void True(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            Pass(name, detail.Length == 0 ? "ok" : detail);
        }
        else
        {
            Fail(name, detail.Length == 0 ? "断言为 false" : detail);
        }
    }

    private static void Pass(string name, string detail)
    {
        Results.Add($"PASS {name}");
        Console.WriteLine($"PASS  {name}" + (detail == "ok" ? string.Empty : $"  （{detail}）"));
    }

    private static void Fail(string name, string detail)
    {
        _failures++;
        Results.Add($"FAIL {name}: {detail}");
        Console.Error.WriteLine($"FAIL  {name}：{detail}");
    }

    private static int Finish(int? forceCode = null)
    {
        var code = forceCode ?? (_failures == 0 ? 0 : 1);
        Console.WriteLine();
        Console.WriteLine($"结果：{Results.Count(r => r.StartsWith("PASS", StringComparison.Ordinal))} 通过，{_failures} 失败/跳过 → 退出码 {code}");
        if (_reportPath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_reportPath))!);
                var payload = new
                {
                    generatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                    exitCode = code,
                    failures = _failures,
                    results = Results,
                };
                File.WriteAllText(_reportPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                Console.WriteLine($"报告：{_reportPath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"报告写入失败：{ex.Message}");
            }
        }

        return code;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            用法：
              BatterBabel.TuneCheck --inspect        只读打印 9 个调优面的当前值与快照状态
              BatterBabel.TuneCheck --selftest       离线自检（内存假系统，不改变本机任何设置）
              BatterBabel.TuneCheck --write-selftest 显式开启的真实写入自检（仅探针键，需管理员）
              可选：--out <path>  把 PASS/FAIL 结果写成 JSON
            """);
    }
}

// ───────────────────────────────── 内存假系统 ─────────────────────────────────

internal sealed class FakeAdapter
{
    public string Name { get; init; } = "Ethernet";
    public string? Guid { get; init; } = "{G1}";
    public string MediaType { get; init; } = "802.3";
    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Valid { get; } = new(StringComparer.Ordinal);
    public string Power { get; set; } = "Enabled";

    public FakeAdapter Clone() => new()
    {
        Name = Name,
        Guid = Guid,
        MediaType = MediaType,
        Power = Power,
    };
}

internal sealed class FakeBoundary : ITuneBoundary
{
    public List<FakeAdapter> Cards { get; } = new();

    public Dictionary<string, TuneRawValue> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Tcp { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["autotuninglevel"] = "normal",
        ["rss"] = "enabled",
    };

    public List<string> Processes { get; } = new();

    public Dictionary<string, string> Wifi { get; } = new() { ["ac"] = "1", ["dc"] = "2" };

    public Func<RegistryValueTarget, bool>? FailRegistry { get; set; }

    public int RegistryWriteCount { get; private set; }

    public static string InterfaceKey(string guid, string name) =>
        $@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}\{name}";

    public static string ThrottleKey(string name) =>
        $@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\{name}";

    /// <summary>一块有全部三个高级属性的网卡 + 发布版会遇到的原始值组合（含缺失值）。</summary>
    public static FakeBoundary Standard()
    {
        var fake = new FakeBoundary();
        var adapter = new FakeAdapter();
        adapter.Properties["*InterruptModeration"] = "1";
        adapter.Properties["*EEE"] = "0";
        adapter.Properties["*FlowControl"] = "1";
        foreach (var keyword in new[] { "*InterruptModeration", "*EEE", "*FlowControl" })
        {
            adapter.Valid[keyword] = new List<string> { "0", "1" };
        }

        fake.Cards.Add(adapter);
        fake.Cards.Add(new FakeAdapter
        {
            Name = "Wi-Fi",
            Guid = "{W1}",
            MediaType = "Native 802.11",
            Power = "Enabled",
        });
        fake.Registry[InterfaceKey("{G1}", "TcpAckFrequency")] = TuneRawValue.OfNumber(2);
        fake.Registry[InterfaceKey("{G1}", "TcpDelAckTicks")] = TuneRawValue.OfNumber(0);
        fake.Registry[ThrottleKey(ThrottleKeys.NetworkThrottlingIndex)] = TuneRawValue.OfNumber(10);
        fake.Registry[ThrottleKey(ThrottleKeys.SystemResponsiveness)] = TuneRawValue.OfNumber(20);
        return fake;
    }

    public string AdapterProps(string adapter, string keyword)
    {
        var target = Cards.First(a => string.Equals(a.Name, adapter, StringComparison.Ordinal));
        return target.Properties.TryGetValue(keyword, out var value) ? value : "(缺失)";
    }

    public string AdapterPowerValue(string adapter) =>
        Cards.First(a => string.Equals(a.Name, adapter, StringComparison.Ordinal)).Power;

    public IReadOnlyList<TuneAdapterInfo> Adapters()
    {
        var list = new List<TuneAdapterInfo>();
        foreach (var adapter in Cards)
        {
            var props = new Dictionary<string, TuneRawValue>(StringComparer.Ordinal);
            foreach (var (keyword, value) in adapter.Properties)
            {
                props[keyword] = TuneRawValue.OfText(value);
            }

            var valid = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (keyword, values) in adapter.Valid)
            {
                valid[keyword] = values.OrderBy(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            }

            list.Add(new TuneAdapterInfo(
                adapter.Name,
                adapter.Guid,
                adapter.MediaType,
                adapter.Guid is not null,
                props,
                valid,
                TuneRawValue.OfText(adapter.Power)));
        }

        return list;
    }

    public IReadOnlyList<string> ProcessNames() => Processes;

    public TuneRawValue ReadRegistry(RegistryValueTarget target) =>
        Registry.TryGetValue(target.Display, out var value) ? value : TuneRawValue.Empty;

    public TuneWriteOutcome WriteRegistryDword(RegistryValueTarget target, int value)
    {
        if (FailRegistry?.Invoke(target) == true)
        {
            return TuneWriteOutcome.Failure("模拟写入失败");
        }

        RegistryWriteCount++;
        Registry[target.Display] = TuneRawValue.OfNumber(unchecked((uint)value));
        return TuneWriteOutcome.Success();
    }

    public TuneWriteOutcome WriteRegistryString(RegistryValueTarget target, string value)
    {
        RegistryWriteCount++;
        Registry[target.Display] = TuneRawValue.OfText(value);
        return TuneWriteOutcome.Success();
    }

    public TuneWriteOutcome DeleteRegistryValue(RegistryValueTarget target)
    {
        RegistryWriteCount++;
        Registry.Remove(target.Display);
        return TuneWriteOutcome.Success();
    }

    public string? ReadTcpGlobal(string name) => Tcp.TryGetValue(name, out var value) ? value : null;

    public TuneWriteOutcome SetTcpGlobal(string name, string value)
    {
        Tcp[name] = value;
        return TuneWriteOutcome.Success(exitCode: 0);
    }

    public TuneWriteOutcome SetAdapterProperty(string adapterName, string keyword, string rawValue)
    {
        var adapter = Cards.First(a => string.Equals(a.Name, adapterName, StringComparison.Ordinal));
        adapter.Properties[keyword] = rawValue;
        return TuneWriteOutcome.Success();
    }

    public TuneWriteOutcome SetAdapterPower(string adapterName, string value)
    {
        var adapter = Cards.First(a => string.Equals(a.Name, adapterName, StringComparison.Ordinal));
        adapter.Power = value;
        return TuneWriteOutcome.Success();
    }

    public string? ReadWifiPowerIndex(bool ac) => Wifi.TryGetValue(ac ? "ac" : "dc", out var value) ? value : null;

    public TuneWriteOutcome SetWifiPowerIndex(string? acHex, string? dcHex)
    {
        if (acHex is not null)
        {
            Wifi["ac"] = acHex;
        }

        if (dcHex is not null)
        {
            Wifi["dc"] = dcHex;
        }

        return TuneWriteOutcome.Success(exitCode: 0);
    }

    public TuneWriteOutcome SetWifiPowerMax() => SetWifiPowerIndex("0", "0");
}

internal sealed class FakeConfigStore : ITuneConfigStore
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ManagedConfigResult Apply(string path, IReadOnlyList<string> lines)
    {
        if (!Directory.Exists(Path.GetDirectoryName(path)!))
        {
            return new ManagedConfigResult(false, $"游戏配置目录不存在：{Path.GetDirectoryName(path)}", path);
        }

        var changed = !Files.TryGetValue(path, out var before) || before != string.Join("\n", lines);
        Files[path] = string.Join("\n", lines);
        return new ManagedConfigResult(true, "已写入", path, Changed: changed);
    }

    public ManagedConfigResult Restore(string path)
    {
        var changed = Files.Remove(path);
        return new ManagedConfigResult(true, changed ? "已移除" : "无托管块", path, Changed: changed);
    }

    public bool Exists(string path) => Files.ContainsKey(path);
}
