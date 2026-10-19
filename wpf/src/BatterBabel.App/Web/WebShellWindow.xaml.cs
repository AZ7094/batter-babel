using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BatterBabel.App.Web;

/// <summary>
/// 正式 App 的 Web 壳：一块 WebView2 + 一个 <see cref="BbRpcHost"/>，页面就是 Tauri 版的 src/public。
///
/// 除了正常显示，还带一个可自动化的自检模式（<c>--web-check</c>）：它用真实的页面、真实的桥、
/// 真实的宿主消息往返来证明「最小双向通信」和「只读业务闭环」，并把截图/DOM/哈希写到 evidence。
/// 自检不引入任何测试专用 RPC —— 用的就是页面本来会发的三个命令和一个事件推送。
/// </summary>
public partial class WebShellWindow : Window
{
    private readonly string _pageRoot;
    private readonly string _evidenceDir;
    private readonly bool _checkMode;
    private readonly bool _shotMode;
    private readonly bool _liveShot;
    private readonly string _liveGame;
    private readonly bool _applyAfter;
    private readonly List<(string Name, bool Pass, string Detail)> _assertions = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private BbRpcHost? _rpc;
    private bool _started;
    private string? _bridgePath;

    public WebShellWindow(string pageRoot, string evidenceDir, bool checkMode, bool shotMode = false, bool liveShot = false, bool applyAfter = false, string liveGame = "limbus")
    {
        InitializeComponent();
        _pageRoot = pageRoot;
        _evidenceDir = evidenceDir;
        _checkMode = checkMode;
        _shotMode = shotMode;
        _liveShot = liveShot;
        _liveGame = liveGame;
        _applyAfter = applyAfter;
        Title = checkMode ? "Batter Babel · Web 壳自检" : "Batter Babel";
        Loaded += OnLoadedAsync;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        if (_started) { return; }
        _started = true;

        try
        {
            await InitAsync();
        }
        catch (Exception ex)
        {
            Fail($"初始化异常：{ex.GetType().Name}: {ex.Message}");
            if (_checkMode || _shotMode || _liveShot)
            {
                FinishChecks();
                return;
            }

            // 最常见的一类「下载了却打不开」：机器上没有 WebView2 运行时。
            // 不弹通用异常框，直接告诉用户装什么，然后退出。
            var missingRuntime = ex is WebView2RuntimeNotFoundException
                || (ex.Message.Contains("WebView2", StringComparison.OrdinalIgnoreCase)
                    && ex.Message.Contains("runtime", StringComparison.OrdinalIgnoreCase));
            if (missingRuntime)
            {
                MessageBox.Show(
                    "缺少 Microsoft Edge WebView2 运行时，界面无法启动。" + Environment.NewLine + Environment.NewLine
                    + "请安装后重新打开 Batter Babel：" + Environment.NewLine
                    + "https://go.microsoft.com/fwlink/p/?LinkId=2124703" + Environment.NewLine + Environment.NewLine
                    + "Microsoft Edge WebView2 Runtime is required." + Environment.NewLine
                    + "Install it from the link above, then start Batter Babel again.",
                    "Batter Babel",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Environment.Exit(3);
            }

            MessageBox.Show(
                "Batter Babel 启动失败：" + ex.Message + Environment.NewLine + Environment.NewLine
                + "Batter Babel failed to start: " + ex.Message,
                "Batter Babel", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(4);
        }
    }

    private async Task InitAsync()
    {
        // UserDataFolder 放 LocalAppData（产品语义）；测试可用 BB_WEBVIEW_UDF 指向隔离目录。
        var userData = Environment.GetEnvironmentVariable("BB_WEBVIEW_UDF")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatterBabel", "WebView2");
        Directory.CreateDirectory(userData);

        Directory.CreateDirectory(_evidenceDir);
        Log($"pageRoot={_pageRoot}");
        Log($"userDataFolder={userData}");

        var environment = await CoreWebView2Environment.CreateAsync(null, userData);
        await View.EnsureCoreWebView2Async(environment);

        var core = View.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;

        // 只把页面目录映射成一个虚拟 https 源：页面里没有 file://，也不允许导航到别处。
        core.SetVirtualHostNameToFolderMapping("bb.local", _pageRoot, CoreWebView2HostResourceAccessKind.Allow);
        core.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith(BbRpcHost.HostOrigin, StringComparison.Ordinal))
            {
                Log($"BLOCK_NAV {args.Uri}");
                args.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            Log($"BLOCK_NEW_WINDOW {args.Uri}");
            args.Handled = true;
        };

        // 单一桥源：src/public/bridge.js 是唯一源码，构建目标 CopyBridgeToOriginalPage
        // 页面里的 bridge.js 就是源文件。宿主不拦截静态资源。
        // 请求改内存响应——虚拟主机映射的静态文件请求不会稳定触发 WebResourceRequested，
        // 拦截会让页面拿不到桥（已实测：page-ready 超时）。
        _bridgePath = Path.Combine(_pageRoot, "bridge.js");

        _rpc = new BbRpcHost(core, Log, fixture: _checkMode || _shotMode || _liveShot ? !_liveShot : false);
        core.NavigationCompleted += async (_, args) =>
        {
            if (!args.IsSuccess)
            {
                Fail($"导航失败：{args.WebErrorStatus}");
                if (_checkMode) { FinishChecks(); }
                return;
            }

            // 宿主只通知一次 ready：之后的所有请求都必须由桥自己发出（旧桥正是在这里断掉的）。
            await _rpc.SignalReadyAsync();
            Log("navigation-completed, ready signalled once");

            if (_checkMode) { await RunChecksAsync(); }
            else if (_shotMode) { await RunShotAsync(); }
            else if (_liveShot) { await RunLiveShotAsync(); }
        };

        // 页面是随包分发的静态资源，文件名不变（app.css/app.js/icon.png）。WebView2 默认
        // 把它们缓存在 UDF 里，替换安装包后仍会拿旧字节。启动时清一次磁盘缓存，保证界面更新生效。
        try { await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache); }
        catch (Exception ex) { Log($"cache-clear skipped: {ex.GetType().Name}"); }

        core.Navigate("https://bb.local/index.html");
    }

    // ---------------------------------------------------------------- 自检（真实页面 + 真实桥）

    private async Task RunChecksAsync()
    {
        try
        {
            var ready = await WaitForAsync("!!window.__TAURI__ && document.readyState === 'complete'", 20000, "page-ready");
            Add("page-ready", ready, "页面加载出 __TAURI__ 适配层");
            if (!ready) { return; }

            // 1) 页面 → 宿主 → 页面：一次真实往返。
            await Eval("window.__r1=null;window.__TAURI__.core.invoke('start_job',{action:'scan'}).then(r=>{window.__r1={jobId:(r&&r.jobId)||'',v:(r&&r.protocolVersion)||0};},e=>{window.__r1={err:String(e)};});");
            var roundtrip = await WaitForAsync("!!window.__r1", 8000, "roundtrip");
            var job1 = roundtrip ? await ReadStringAsync("(window.__r1&&window.__r1.jobId)||''") : "";
            var version = roundtrip ? await ReadNumberAsync("(window.__r1&&window.__r1.v)||0") : 0;
            Add("page-host-page-roundtrip", roundtrip && job1.StartsWith("web-job-") && version == 1, $"jobId={job1}, protocolVersion={version}");

            // 2) 只通知一次 ready，第二次请求仍要发得出去（旧桥在这里静默丢失）。
            await Eval("window.__r2=null;window.__TAURI__.core.invoke('get_progress',{jobId:'" + job1 + "'}).then(r=>{window.__r2={jobId:(r&&r.jobId)||'',status:(r&&r.status)||''};},e=>{window.__r2={err:String(e)};});");
            var second = await WaitForAsync("!!window.__r2", 8000, "second-request");
            var job2 = second ? await ReadStringAsync("(window.__r2&&window.__r2.jobId)||''") : "";
            Add(
                "second-request-after-single-ready",
                second && job2 == job1 && _rpc is { ReceivedCount: >= 2, ReadyCalls: 1 },
                $"jobId={job2}, received={_rpc?.ReceivedCount}, readyCalls={_rpc?.ReadyCalls}");

            // 3) 宿主 → 页面的事件通道（页面用 event.listen 注册）。
            await Eval("window.__events=[];window.__TAURI__.event.listen('bb-progress',e=>window.__events.push(e&&e.payload&&e.payload.jobId));");
            await _rpc!.EmitAsync("bb-progress", new { jobId = "check-event", sequence = 1, percent = 1, message = "自检事件", status = "RUNNING" });
            var emitted = await WaitForAsync("(window.__events||[]).length>0 && window.__events[0]==='check-event'", 5000, "host-event");
            Add("host-to-page-event", emitted, "宿主 __bbEmit → 页面 event.listen");

            // 4) 只读业务闭环：游戏列表来自 Core 目录，点「开始优选」渲染固定样本。
            var listed = await WaitForAsync("document.querySelectorAll('#game-select option').length>=3", 20000, "game-list");
            Add("game-list-from-catalog", listed, await ReadStringAsync("(document.querySelectorAll('#game-select option').length)+' options'"));

            // 页面现在把全部已安装游戏按名称排序，默认会选中第一条；固定样本的断言针对 Limbus，先显式选中它。
            await Eval(@"(() => {
  const select = document.getElementById('game-select');
  if (!select) { return; }
  select.value = 'limbus';
  select.dispatchEvent(new Event('change'));
})()");
            await Task.Delay(400);
            await Eval("document.getElementById('optimize') && document.getElementById('optimize').click();");
            // 0.10.3 原版界面是「一个分组一张卡」：cloudflare 卡里是分组赢家，赢家 IP 随样本而定，
            // 所以这里只要求渲染出「优选 IP + 下载速率」，不再钉死某个具体地址。
            var rendered = await WaitForAsync(
                "/(104\\.16\\.\\d+\\.\\d+)/.test(document.body.innerText) && document.body.innerText.indexOf('MB/s')>=0",
                30000,
                "optimize-rendered");
            Add("business-optimize-rendered", rendered, "Limbus 线路卡片渲染出优选 IP 与 MB/s（固定样本）");
            try
            {
                var checkDom = await ReadStringAsync("document.body.innerText");
                await File.WriteAllTextAsync(Path.Combine(_evidenceDir, "app-web-check-dom.txt"), checkDom);
            }
            catch (Exception ex) { Log("check dom dump failed: " + ex.Message); }

            // 5) 写操作必须被拒绝，且是业务拒绝（resolve 出 ok:false），不是通道错误。
            await Eval("window.__deny=null;window.__TAURI__.core.invoke('run_action',{action:'cf-apply',gameId:'limbus'}).then(r=>{window.__deny=r;},e=>{window.__deny={err:String(e)};});");
            var denied = await WaitForAsync("!!window.__deny && window.__deny.ok===false && window.__deny.status==='FAILED'", 5000, "write-refused");
            Add("write-actions-refused", denied, "cf-apply → {ok:false,status:'FAILED'}");

            // 6) 入站边界（R1）：超限/畸形请求在业务分派前被拒；认得出的 callId 回结构化失败。
            await Eval("window.__raw=[];window.__postRaw=(s)=>window.chrome.webview.postMessage(s);"
                + "window.chrome.webview.addEventListener('message',e=>window.__raw.push(e.data));");
            var businessBefore = _rpc!.BusinessCalls;
            var droppedBefore = _rpc.DroppedEnvelopes;

            var oversizeArgs = JsonSerializer.Serialize(new { action = "scan", pad = new string('x', 70000) });
            var oversize = JsonSerializer.Serialize(new { callId = "boundary-oversize", command = "start_job", args = oversizeArgs });
            await Eval("window.__postRaw(" + JsonSerializer.Serialize(oversize) + ")");
            var dropped = await WaitUntilAsync(() => _rpc.DroppedEnvelopes > droppedBefore, 3000, "boundary-oversize");
            Add(
                "boundary-oversize-dropped",
                dropped && _rpc.BusinessCalls == businessBefore,
                $"{oversize.Length}B 信封被丢弃，businessCalls={_rpc.BusinessCalls}（分派前拒绝）");

            var malformedArgs = JsonSerializer.Serialize(new { callId = "boundary-malformed", command = "run_action", args = "{" });
            await Eval("window.__postRaw(" + JsonSerializer.Serialize(malformedArgs) + ")");
            var malformed = await WaitForAsync(
                "window.__raw.some(r=>r&&r.callId==='boundary-malformed'&&r.ok===false&&r.payload&&r.payload.error==='INVALID_ARGS')",
                3000, "boundary-malformed");
            Add("boundary-malformed-args-rejected", malformed && _rpc.BusinessCalls == businessBefore, "畸形 args → INVALID_ARGS");

            const string unknownField = "{\"callId\":\"boundary-unknown\",\"command\":\"get_progress\",\"extra\":1}";
            await Eval("window.__postRaw(" + JsonSerializer.Serialize(unknownField) + ")");
            var unknown = await WaitForAsync(
                "window.__raw.some(r=>r&&r.callId==='boundary-unknown'&&r.ok===false&&r.payload&&r.payload.error==='INVALID_ENVELOPE')",
                3000, "boundary-unknown");
            Add("boundary-unknown-field-rejected", unknown, "未知信封字段 → INVALID_ENVELOPE");

            var nonObjectArgs = JsonSerializer.Serialize(new { callId = "boundary-nonobject", command = "get_progress", args = "5" });
            await Eval("window.__postRaw(" + JsonSerializer.Serialize(nonObjectArgs) + ")");
            var nonObject = await WaitForAsync(
                "window.__raw.some(r=>r&&r.callId==='boundary-nonobject'&&r.ok===false&&r.payload&&r.payload.error==='INVALID_ARGS')",
                3000, "boundary-nonobject");
            Add("boundary-nonobject-args-rejected", nonObject, "args 不是对象 → INVALID_ARGS");

            var deepArgs = "{\"a\":" + new string('[', 16) + new string(']', 16) + "}";
            var deep = JsonSerializer.Serialize(new { callId = "boundary-deep", command = "get_progress", args = deepArgs });
            await Eval("window.__postRaw(" + JsonSerializer.Serialize(deep) + ")");
            var deepRejected = await WaitForAsync(
                "window.__raw.some(r=>r&&r.callId==='boundary-deep'&&r.ok===false&&r.payload&&r.payload.error==='INVALID_ARGS')",
                3000, "boundary-deep");
            Add("boundary-deep-args-rejected", deepRejected, "args 深度超过 16 → INVALID_ARGS");

            await Eval("window.__r3=null;window.__TAURI__.core.invoke('start_job',{action:'scan'}).then(r=>{window.__r3={jobId:(r&&r.jobId)||''};},e=>{window.__r3={err:String(e)};});");
            var afterBoundary = await WaitForAsync(
                "!!window.__r3 && typeof window.__r3.jobId==='string' && window.__r3.jobId.indexOf('web-job-')===0",
                5000, "post-boundary");
            Add("post-boundary-request-works", afterBoundary, "边界测试后正常请求仍能完成");
        }
        finally
        {
            await CaptureEvidenceAsync();
            FinishChecks();
        }
    }

    private async Task CaptureEvidenceAsync()
    {
        try
        {
            var dom = await ReadStringAsync("document.body.innerText");
            var domPath = Path.Combine(_evidenceDir, "app-web-dom.txt");
            await File.WriteAllTextAsync(domPath, dom);

            var pngPath = Path.Combine(_evidenceDir, "app-web-check.png");
            await using (var stream = File.Create(pngPath))
            {
                await View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            }

            var report = new
            {
                mode = "--web-check",
                startedAt = DateTimeOffset.Now.AddMilliseconds(-_clock.ElapsedMilliseconds).ToString("o"),
                durationMs = _clock.ElapsedMilliseconds,
                fixture = true,
                pageRoot = _pageRoot,
                received = _rpc?.ReceivedCount ?? 0,
                readyCalls = _rpc?.ReadyCalls ?? 0,
                assertions = _assertions.Select(a => new { name = a.Name, pass = a.Pass, detail = a.Detail }).ToArray(),
                passed = _assertions.Count > 0 && _assertions.All(a => a.Pass),
                hashes = new
                {
                    appExe = HashFile(Environment.ProcessPath),
                    appDll = HashFile(Path.Combine(AppContext.BaseDirectory, "BatterBabel.dll")),
                    bridgeJs = HashFile(_bridgePath),
                    png = HashFile(pngPath),
                    domText = HashFile(domPath),
                },
            };

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(_evidenceDir, "app-web-check.json"), json);
            Log($"evidence written: {_evidenceDir}");
        }
        catch (Exception ex)
        {
            Fail($"写证据失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>--web-shot</c>：只截「刚打开、还没点开始优选」的首屏，用来和 0.10.3 原版截图对照。
    /// 同样走真实宿主 + 真实桥（页面自己发 scan），不注入演示脚本。
    /// </summary>
    /// <summary>真实测速的端到端证据：点「开始优选」，等真实结果渲染，截图 + DOM + 退出码。</summary>
    private async Task RunLiveShotAsync()
    {
        try
        {
            var ready = await WaitForAsync("!!window.__TAURI__ && document.readyState === 'complete'", 20000, "page-ready");
            Add("page-ready", ready, "页面加载出 __TAURI__ 适配层");
            if (!ready) { return; }

            var listed = await WaitForAsync("document.querySelectorAll('#game-select option').length > 0", 15000, "game-list");
            Add("game-list", listed, "真实扫描填出游戏下拉");

            var gameLiteral = JsonSerializer.Serialize(_liveGame);
            var gameSelectable = await WaitForAsync(
                $"[...document.querySelectorAll('#game-select option')].some(o => o.value === {gameLiteral})",
                5000,
                "live-game-option");
            Add("live-game-selectable", gameSelectable, $"下拉框里有 {_liveGame} 选项");

            // 页面会按扫描结果自动选中第一个已安装游戏（可能是 apex 等），所以每次都显式切到目标游戏；
            // 页面的 scan 渲染可能晚于本脚本，重试到下拉值稳定为目标游戏为止。
            var selected = "";
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await Eval($@"(() => {{
  const select = document.getElementById('game-select');
  if (!select) {{ return; }}
  select.value = {gameLiteral};
  select.dispatchEvent(new Event('change'));
}})()");
                await Task.Delay(500);
                selected = await ReadStringAsync("(document.getElementById('game-select')||{}).value || ''");
                if (string.Equals(selected, _liveGame, StringComparison.OrdinalIgnoreCase)) { break; }
            }

            Add("live-game-selected", string.Equals(selected, _liveGame, StringComparison.OrdinalIgnoreCase),
                $"下拉当前值={selected}，期望={_liveGame}");

            await Eval("document.getElementById('optimize').click();");
            Log("live: clicked optimize, probing…");

            // CS2 没有可优选域名（optGroups 为空）：cf-optimize 返回 groups=[]，但会顺带维护
            // autoexec.cfg 托管块，页面该出现的是「游戏配置」日志而不是 MB/s 卡片。两条断言不能共用。
            if (string.Equals(_liveGame, "cs2", StringComparison.OrdinalIgnoreCase))
            {
                // 页面 0.10.3 文案：没有可优选线路时只写游戏配置，不再给出「100%」进度；断言按真实文案放宽。
                var configRendered = await WaitForAsync(
                    "document.body.innerText.includes('游戏配置') && document.body.innerText.includes('autoexec.cfg')",
                    30000,
                    "live-config");
                var configDom = await ReadStringAsync("document.body.innerText");
                var configLine = configDom.Split('\n')
                    .FirstOrDefault(line => line.Contains("autoexec.cfg") && line.Contains("游戏配置"))
                    ?? configDom.Split('\n').FirstOrDefault(line => line.Contains("游戏配置")) ?? "";
                Add("live-config-rendered", configRendered && configLine.Contains("autoexec.cfg"),
                    configLine.Length > 0 ? configLine.Trim() : "页面日志没有「游戏配置」行");
            }
            else
            {
                var done = await WaitForAsync(
                    @"/MB\/s/.test(document.body.innerText) && document.querySelectorAll('.card .lines').length > 0",
                    240000,
                    "live-result");
                Add("live-result-rendered", done, "页面出现真实 MB/s 与 ms 结果");
            }

            var dom = await ReadStringAsync("document.body.innerText");
            var domPath = Path.Combine(_evidenceDir, "app-web-live-dom.txt");
            await File.WriteAllTextAsync(domPath, dom);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var png = Path.Combine(_evidenceDir, $"app-web-live-{stamp}.png");
            await using (var stream = File.Create(png))
            {
                await View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            }

            Log("live: screenshot " + png + " bytes=" + new FileInfo(png).Length);
            Add("live-screenshot", File.Exists(png) && new FileInfo(png).Length > 20000, "真实测速窗口截图非空");

            if (_applyAfter)
            {
                // 真实写入：点「写入 hosts」→ 等宿主回报 → 读回系统 hosts → 再点「还原 hosts」→ 再读回。
                await Eval("document.getElementById('btn-apply').click();");
                var applied = await WaitForAsync("document.body.innerText.includes('已写入')", 120000, "hosts-apply");
                var afterApply = File.Exists(LiveRoutes.HostsPath) ? await File.ReadAllTextAsync(LiveRoutes.HostsPath) : "";
                var entries = afterApply.Contains(BatterBabel.Core.Hosts.HostsFileWriter.BeginMarker)
                    ? afterApply.Split('\n').Count(line => { var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries); return parts.Length >= 2 && !line.Trim().StartsWith('#'); })
                    : 0;
                Add("hosts-apply-reports", applied, "页面出现「已写入」日志");
                Add("hosts-apply-written", afterApply.Contains(BatterBabel.Core.Hosts.HostsFileWriter.BeginMarker) && entries > 0, $"{entries} 条映射在系统 hosts 里");

                var appliedShot = Path.Combine(_evidenceDir, $"app-web-applied-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                await using (var stream = File.Create(appliedShot))
                {
                    await View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                }

                Add("hosts-applied-screenshot", File.Exists(appliedShot) && new FileInfo(appliedShot).Length > 20000, appliedShot);

                await Eval("document.getElementById('btn-restore').click();");
                var restored = await WaitForAsync("document.body.innerText.includes('已还原') || document.body.innerText.includes('无需还原')", 120000, "hosts-restore");
                var afterRestore = File.Exists(LiveRoutes.HostsPath) ? await File.ReadAllTextAsync(LiveRoutes.HostsPath) : "";
                Add("hosts-restore-reports", restored, "页面出现还原结果");
                Add("hosts-restore-clean", !afterRestore.Contains(BatterBabel.Core.Hosts.HostsFileWriter.BeginMarker), "标记块已从系统 hosts 移除");

                // S7/S9 走同一条页面链路：加速游戏（进程优先级 + QoS 策略）、系统调优、还原系统调优。
                // 发布版把「加速游戏」做成交替按钮：未加速 → optimize，已加速 → restore。
                // 页面正在并行改成这套文案（旧版仍是 boost），三种前缀都接受，但都要求 inline-note 隐藏。
                var standIn = StartStandInProcess(_liveGame);
                try
                {
                    await Eval("document.getElementById('btn-boost').click();");
                    var boosted = await WaitForAsync(
                        "/(optimize：|restore：|boost：)/.test(document.body.innerText) && document.getElementById('inline-note').hidden === true",
                        90000, "optimize-restore");
                    Add("boost-reports", boosted, "页面出现加速/还原结果（optimize：/restore：）");

                    // 自检机不保留 QoS 策略：真实删一次（发布版 restore 语义），不让证据留下系统残留。
                    await Eval("window.__TAURI__.core.invoke('run_action',{action:'restore',gameId:" + gameLiteral + "});");
                }
                finally
                {
                    StopStandInProcess(standIn);
                }

                await Eval("document.getElementById('btn-tune').click();");
                var tuned = await WaitForAsync(
                    "document.body.innerText.includes('tune-system：')",
                    90000, "tune-system");
                Add("tune-reports", tuned, "页面出现系统调优结果");

                await Eval("document.getElementById('btn-restore-tune').click();");
                var tuneRestored = await WaitForAsync(
                    "document.body.innerText.includes('restore-tune：')",
                    90000, "restore-tune");
                Add("restore-tune-reports", tuneRestored, "页面出现还原调优结果");

                var actionsShot = Path.Combine(_evidenceDir, $"app-web-actions-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                await using (var stream = File.Create(actionsShot))
                {
                    await View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                }
                Add("actions-screenshot", File.Exists(actionsShot) && new FileInfo(actionsShot).Length > 20000, actionsShot);
            }
        }
        catch (Exception ex)
        {
            Add("live-failed", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            FinishChecks();
        }
    }

    private async Task RunShotAsync()
    {
        try
        {
            await Task.Delay(1500); // 等页面拿到 scan、卡片渲染完
            var pngPath = Path.Combine(_evidenceDir, "app-web-start.png");
            await using (var stream = File.Create(pngPath))
            {
                await View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            }

            var domPath = Path.Combine(_evidenceDir, "app-web-start-dom.txt");
            await File.WriteAllTextAsync(domPath, await ReadStringAsync("document.body.innerText"));

            var size = new FileInfo(pngPath).Length;
            Add("startup-shot", size > 5000, $"{Path.GetFileName(pngPath)} {size} B, pageRoot={_pageRoot}");
            Console.WriteLine($"SHOT {pngPath}");
        }
        catch (Exception ex)
        {
            Add("fatal", false, $"截图失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            FinishChecks();
        }
    }

    private void FinishChecks()
    {
        var pass = _assertions.Count > 0 && _assertions.All(a => a.Pass);
        foreach (var assertion in _assertions)
        {
            Console.WriteLine($"{(assertion.Pass ? "PASS" : "FAIL")} {assertion.Name} :: {assertion.Detail}");
            Log($"{(assertion.Pass ? "PASS" : "FAIL")} {assertion.Name} :: {assertion.Detail}");
        }

        var banner = _liveShot ? "WEB_LIVE" : _shotMode ? "WEB_SHOT" : "WEB_CHECK";
        Console.WriteLine(pass ? $"{banner} PASS" : $"{banner} FAIL");
        Log(pass ? $"{banner} PASS" : $"{banner} FAIL");
        Application.Current?.Shutdown(pass ? 0 : 1);
    }

    /// <summary>
    /// 发布版 boost 的语义是「把**已在运行**的同名进程优先级提到 AboveNormal」，没有进程就拒绝。
    /// 自检机通常没装/没开游戏，这里用同名可执行副本起一个真实进程，让「运行中」这条路径被真实走一遍；
    /// 进程仍然是真实进程（不是伪造宿主返回值），退出时删除。
    /// </summary>
    private Process? StartStandInProcess(string gameId)
    {
        try
        {
            var game = BatterBabel.Core.Domain.GameCatalog.Find(gameId);
            if (game is null || string.IsNullOrWhiteSpace(game.Executable)) { return null; }
            var dir = Path.Combine(Path.GetTempPath(), "bb-standin");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, game.Executable);
            File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), target, true);
            var started = Process.Start(new ProcessStartInfo(target) { UseShellExecute = false });
            if (started is null) { return null; }
            var name = Path.GetFileNameWithoutExtension(game.Executable);
            for (var i = 0; i < 20; i++)
            {
                if (Process.GetProcessesByName(name).Length > 0) { return started; }
                Thread.Sleep(150);
            }
            return started;
        }
        catch (Exception ex)
        {
            Log($"stand-in process failed: {ex.Message}");
            return null;
        }
    }

    private void StopStandInProcess(Process? process)
    {
        if (process is null) { return; }
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        catch (Exception ex) { Log($"stand-in cleanup failed: {ex.Message}"); }
        finally { process.Dispose(); }
    }

    private async Task<bool> WaitForAsync(string expression, int timeoutMs, string label)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var raw = await View.CoreWebView2.ExecuteScriptAsync(expression);
                if (raw == "true") { return true; }
            }
            catch (Exception ex)
            {
                Log($"wait[{label}] error {ex.GetType().Name}: {ex.Message}");
            }

            await Task.Delay(100);
        }

        Log($"wait[{label}] TIMEOUT after {timeoutMs}ms");
        return false;
    }

    private async Task<bool> WaitUntilAsync(Func<bool> predicate, int timeoutMs, string label)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate()) { return true; }
            await Task.Delay(50);
        }

        Log($"wait[{label}] TIMEOUT after {timeoutMs}ms");
        return false;
    }

    private async Task<string> ReadStringAsync(string expression)
    {
        var raw = await View.CoreWebView2.ExecuteScriptAsync(expression);
        try { return JsonSerializer.Deserialize<string>(raw) ?? ""; }
        catch (JsonException) { return raw; }
    }

    private async Task<double> ReadNumberAsync(string expression)
    {
        var raw = await View.CoreWebView2.ExecuteScriptAsync(expression);
        return double.TryParse(raw, out var value) ? value : 0;
    }

    private Task Eval(string js) => View.CoreWebView2.ExecuteScriptAsync(js);

    private void Add(string name, bool pass, string detail)
    {
        _assertions.Add((name, pass, detail));
        Log($"{(pass ? "PASS" : "FAIL")} {name} :: {detail}");
    }

    private void Fail(string detail) => Add("fatal", false, detail);

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Console.WriteLine(line);
        try
        {
            Directory.CreateDirectory(_evidenceDir);
            File.AppendAllText(Path.Combine(_evidenceDir, "app-web-run.log"), line + Environment.NewLine);
        }
        catch (IOException)
        {
            // 日志写不进去不能影响主流程。
        }
    }

    private static string HashFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return ""; }
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
