using BatterBabel.Core.Discovery;
using BatterBabel.Core.Domain;
using BatterBabel.Core.Hosts;
using BatterBabel.Core.Optimization;
using BatterBabel.Core.Services;
using BatterBabel.Core.Simulation;

// BatterBabel 原型自检：只守原型阶段最容易错的契约，不是完整测试套件。
// 全部断言对齐 scripts/cf-probe.ps1 的生产语义；失败即退出码 1。

var check = new Checker();

// ---------------------------------------------------------------- 1. 中位数
check.Eq("median/奇数样本取中间值", 31.0, ProbeRanking.Median(new double[] { 40, 12, 31 }));
check.Eq("median/偶数样本取中间两值平均", 15.0, ProbeRanking.Median(new double[] { 10, 20 }));
check.True("median/无有效样本返回 null（不是 0）", ProbeRanking.Median(Array.Empty<double>()) is null);
check.Eq("median/过滤 0 与负数", 40.0, ProbeRanking.Median(new double[] { 0, -5, 40 }));
check.Eq("median/过滤 NaN 与 Infinity", 33.0, ProbeRanking.Median(new double[] { double.NaN, 33, double.PositiveInfinity }));

// ------------------------------------------------------------- 2. 排名注解
var usable = Candidate("u", ProbeVerdict.Usable, 20, 3);
var usableRank = ProbeRanking.RankOf(usable);
check.True("rank/Usable 有效样本可排名", usableRank.Rankable && usableRank.Group == 0);
check.Eq("rank/可排名原因 None", RankReason.None, usableRank.Reason);

var noSamples = ProbeRanking.RankOf(Candidate("n", ProbeVerdict.Usable, null, 3));
check.True("rank/无中位数不可排名", !noSamples.Rankable);
check.Eq("rank/无中位数原因 NoValidLatencySamples", RankReason.NoValidLatencySamples, noSamples.Reason);
check.Eq("rank/无中位数排序值 MaxValue（绝不退化成 0ms）", double.MaxValue, noSamples.SortValue);

var fewSamples = ProbeRanking.RankOf(Candidate("f", ProbeVerdict.Usable, 20, 1));
check.Eq("rank/样本不足原因 InsufficientSamples", RankReason.InsufficientSamples, fewSamples.Reason);

var keep = ProbeRanking.RankOf(Candidate("k", ProbeVerdict.Keep, 18, 1));
check.True("rank/Keep 永不排名且分组为 1", !keep.Rankable && keep.Group == 1);
var unusable = ProbeRanking.RankOf(Candidate("x", ProbeVerdict.Unusable, 9, 1));
check.True("rank/Unusable 分组为 2", !unusable.Rankable && unusable.Group == 2);

// --------------------------------------------------------------- 3. 排序顺序
var mixed = ProbeRanking.Ranked(new[]
{
    Candidate("keep-18ms", ProbeVerdict.Keep, 18, 1),
    Candidate("usable-42ms", ProbeVerdict.Usable, 42, 3),
    Candidate("unusable-9ms", ProbeVerdict.Unusable, 9, 1),
    Candidate("usable-27ms", ProbeVerdict.Usable, 27, 3),
    Candidate("usable-nosample", ProbeVerdict.Usable, null, 3),
});
check.Eq(
    "ranked/Usable(按中位数) → 不可排名 Usable → Keep → Unusable",
    "usable-27ms,usable-42ms,usable-nosample,keep-18ms,unusable-9ms",
    string.Join(",", mixed.Select(item => item.Address)));

// ------------------------------------------------------------- 4. 赢家选择
check.Eq("best/取可排名里中位数最小者", "usable-27ms", ProbeRanking.SelectBest(mixed)?.Address);
check.True(
    "best/不可排名的低延迟地址不能当赢家",
    ProbeRanking.SelectBest(new[] { Candidate("unusable-9ms", ProbeVerdict.Unusable, 9, 1) }) is null);
check.True(
    "best/全部不合格返回 null（调用方因此不写 hosts）",
    ProbeRanking.SelectBest(new[] { Candidate("keep", ProbeVerdict.Keep, 18, 1) }) is null);

var tie = ProbeRanking.SelectBest(new[]
{
    Candidate("first", ProbeVerdict.Usable, 30, 3),
    Candidate("second", ProbeVerdict.Usable, 30, 3),
});
check.Eq("best/中位数相同保留第一个", "first", tie?.Address);

// ------------------------------------------------------------- 5. 汇总与原因
var summary = ProbeRanking.Summarize("example.com", new[] { Candidate("none", ProbeVerdict.Usable, null, 3) });
check.True("summarize/无赢家时 Best 为 null", summary.Best is null);
check.Eq("summarize/无赢家原因来自排名注解", "NoValidLatencySamples", summary.BestUnavailableReason);

// ---------------------------------------------------------------- 6. 游戏目录
check.Eq("catalog/画像数量", 7, GameCatalog.Games.Count);
var limbus = GameCatalog.Find("limbus");
check.True("catalog/能找到 Limbus Company", limbus is not null);
check.Eq("catalog/Limbus 两张卡片", 2, GameCatalog.GroupsFor(limbus!).Count);
check.Eq("catalog/Limbus 第一张是 Cloudflare 下载（tag CF）", RouteTag.Cf, GameCatalog.GroupsFor(limbus!)[0].Tag);
check.Eq("catalog/Limbus 第一张走 cloudflare 测速模式", ProbeMode.Cloudflare, GameCatalog.GroupsFor(limbus!)[0].Mode);
check.Eq("catalog/Limbus 第一张标签", "Cloudflare (download CDN)", GameCatalog.GroupsFor(limbus!)[0].Label);
check.Eq("catalog/Limbus 第二张是 API（tag AMAZON）", RouteTag.Amazon, GameCatalog.GroupsFor(limbus!)[1].Tag);
check.Eq("catalog/Limbus 第二张走 probe 模式", ProbeMode.Probe, GameCatalog.GroupsFor(limbus!)[1].Mode);
check.Eq("catalog/Limbus API 首个域名", "www.limbuscompanyapi.com", GameCatalog.GroupsFor(limbus!)[1].Domains[0]);
check.True("catalog/未知 id 返回 null", GameCatalog.Find("no-such-game") is null);
check.Eq("catalog/CS2 没有可优选线路（生产同样排除 Steam 平台）", 0, GameCatalog.GroupsFor(GameCatalog.Find("cs2")!).Count);
check.Eq("catalog/R6 追加 Ubisoft 分组", "Ubisoft", GameCatalog.PlatformForApp("359550"));
check.Eq("catalog/未知 appId 不推断平台", string.Empty, GameCatalog.PlatformForApp("999999"));

// --------------------------------------------------- 7. 模拟器：确定性 + 自洽
var observer = new NullObserver();
var first = await new SimulatedRouteOptimizer(0).OptimizeAsync(limbus!, observer);
var second = await new SimulatedRouteOptimizer(0).OptimizeAsync(limbus!, observer);
check.Eq("sim/两次运行候选完全一致（确定性）", Fingerprint(first), Fingerprint(second));
check.Eq("sim/分组数量与目录一致", 2, first.Count);
check.True("sim/每个域名都有结果", first.SelectMany(group => group.Domains).All(domain => domain.Candidates.Count > 0));
check.True(
    "sim/赢家必须是可排名候选里的最小中位数",
    first.SelectMany(group => group.Domains).All(domain =>
        domain.Best is null || domain.Candidates
            .Where(candidate => candidate.Verdict == ProbeVerdict.Usable && candidate.Samples >= 2 && candidate.MedianMs is > 0)
            .All(candidate => candidate.MedianMs >= domain.Best.MedianMs)));
check.True("sim/明确标记为模拟实现", new SimulatedRouteOptimizer(0).IsSimulated);

// --------------------------------------------------------- 8. hosts 拒绝写入
var hostsBefore = SnapshotHosts();
var applier = new RefusingHostsApplier();
var applyResult = await applier.ApplyAsync(first.SelectMany(group => group.Domains).ToArray());
check.True("hosts/ApplyAsync 不得报告成功", !applyResult.Success);
check.Eq("hosts/ApplyAsync 错误码 WriteFailed", HostsErrorCode.WriteFailed, applyResult.Code);
check.True("hosts/拒绝原因非空", applyResult.Message.Length > 0);

var restoreResult = await applier.RestoreAsync();
check.True("hosts/RestoreAsync 不得报告成功", !restoreResult.Success);
check.Eq("hosts/RestoreAsync 错误码 FileNotFound", HostsErrorCode.FileNotFound, restoreResult.Code);
check.True("hosts/实现自称仅模拟", applier.IsSimulationOnly);
check.True("hosts/路径以 \\drivers\\etc\\hosts 结尾", HostsTarget.Path.EndsWith(@"\drivers\etc\hosts", StringComparison.OrdinalIgnoreCase));
check.Eq("hosts/真实 hosts 文件未被改动（长度+时间戳）", hostsBefore, SnapshotHosts());

// --------------------------------------------------- 9. Steam/发现解析（S1）
const string vdf = "\"libraryfolders\"\n{\n \"0\"\n {\n  \"path\" \"C:\\Program Files (x86)\\Steam\"\n }\n \"1\"\n {\n  \"path\" \"E:\\SteamLibrary\"\n }\n}";
var libraries = SteamLibraryParser.ParseLibraryPaths(vdf);
check.Eq("steam/解析 libraryfolders 的两个库根", 2, libraries.Count);
check.Eq("steam/保留库根原样（含反斜杠）", @"C:\Program Files (x86)\Steam", libraries[0]);

const string acf = "\"AppState\"\n{\n \"appid\" \"1973530\"\n \"name\" \"Limbus Company\"\n \"installdir\" \"Limbus Company\"\n}";
var manifest = SteamLibraryParser.ParseAppManifest("appmanifest_1973530.acf", acf);
check.Eq("steam/解析 appid", "1973530", manifest?.AppId ?? "");
check.Eq("steam/解析 installdir", "Limbus Company", manifest?.InstallDir ?? "");
check.True("steam/清单缺 appid 返回 null", SteamLibraryParser.ParseAppManifest("x.acf", "\"AppState\" { }") is null);

check.True("discovery/卸载器不是游戏主程序", ExecutableSelector.IsBlocked("unins000.exe"));
check.True("discovery/反作弊安装器被过滤", ExecutableSelector.IsBlocked("EasyAntiCheat_Setup.exe"));
check.True("discovery/正常 exe 不在黑名单", !ExecutableSelector.IsBlocked("LimbusCompany.exe"));

var tempDir = Path.Combine(Path.GetTempPath(), "bb-selfcheck-" + Guid.NewGuid().ToString("n"));
Directory.CreateDirectory(tempDir);
try
{
    File.WriteAllText(Path.Combine(tempDir, "r5apex_dx12.exe"), "");
    check.Eq(
        "discovery/按候选顺序挑到真实存在的 exe（Apex 别名）",
        Path.Combine(tempDir, "r5apex_dx12.exe"),
        ExecutableSelector.Select(tempDir, "r5apex.exe", "r5apex_dx12.exe") ?? "");
    check.True(
        "discovery/目录不存在时返回 null（不抛异常）",
        ExecutableSelector.Select(Path.Combine(tempDir, "no-such"), "x.exe") is null);
}
finally
{
    Directory.Delete(tempDir, true);
}

return check.Report();

// ---------------------------------------------------------------- 工具函数
static RouteCandidate Candidate(string address, ProbeVerdict verdict, double? median, int samples) =>
    new(address, verdict, median, samples, 200, null, RankReason.None, false);

static string Fingerprint(IReadOnlyList<RouteGroupResult> results) =>
    string.Join(";", results.SelectMany(group => group.Domains).Select(domain =>
        domain.Domain + "=" + (domain.Best?.Address ?? "-") + "@" + (domain.Best?.MedianMs?.ToString() ?? "-")));

static string SnapshotHosts()
{
    var path = HostsTarget.Path;
    if (!File.Exists(path)) { return "missing"; }
    var info = new FileInfo(path);
    return info.Length + "|" + info.LastWriteTimeUtc.Ticks;
}

internal sealed class Checker
{
    private int _passed;
    private int _failed;

    internal void Eq(string name, object? expected, object? actual)
    {
        var ok = Equals(expected, actual);
        Record(ok, name, ok ? "" : $"期望 {Format(expected)}，实际 {Format(actual)}");
    }

    internal void True(string name, bool ok, string? detail = null) =>
        Record(ok, name, ok ? "" : detail ?? "断言为 false");

    internal int Report()
    {
        Console.WriteLine();
        Console.WriteLine($"通过 {_passed}，失败 {_failed}。");
        if (_failed == 0)
        {
            Console.WriteLine("SELF-CHECK OK");
            return 0;
        }

        Console.WriteLine("SELF-CHECK FAILED");
        return 1;
    }

    private void Record(bool ok, string name, string detail)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  PASS  " + name);
            return;
        }

        _failed++;
        Console.WriteLine("  FAIL  " + name + (detail.Length > 0 ? " :: " + detail : ""));
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        double number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

internal sealed class NullObserver : IProbeObserver
{
    public void OnStarted(GameProfile game, int workTotal) { }
    public void OnWorkDone(int workDone, int workTotal, string message) { }
    public void OnDomainCompleted(int groupIndex, DomainProbeResult result) { }
    public void OnFinished() { }
}
