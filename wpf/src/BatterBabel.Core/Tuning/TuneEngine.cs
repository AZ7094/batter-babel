namespace BatterBabel.Core.Tuning;

using System.Globalization;
using BatterBabel.Core.GameConfig;

/// <summary>游戏配置文件的读写边界（生产实现包 <see cref="ManagedConfigFile"/>；自检注入内存实现）。</summary>
public interface ITuneConfigStore
{
    ManagedConfigResult Apply(string path, IReadOnlyList<string> lines);

    ManagedConfigResult Restore(string path);

    bool Exists(string path);
}

public sealed class ManagedConfigStore : ITuneConfigStore
{
    public ManagedConfigResult Apply(string path, IReadOnlyList<string> lines) => ManagedConfigFile.Apply(path, lines);

    public ManagedConfigResult Restore(string path) => ManagedConfigFile.Restore(path);

    public bool Exists(string path) => File.Exists(path);
}

/// <summary>
/// 调优编排：快照 → 按档位差异写系统 → 记账；以及按快照固定顺序还原。
/// 整个类对注册表/netsh/powercfg 一无所知，只经 <see cref="ITuneBoundary"/> 触达系统，
/// 因此 <c>BatterBabel.TuneCheck --selftest</c> 可以用内存假实现在不改变系统的前提下跑通全链路。
///
/// 与发布版的有意差异（写进 evidence/dsh-migration/TUNE/README.md）：
///   1) <b>快照优先、fail-closed</b>：快照没落盘/读回不一致就返回 BLOCKED，一个系统值都不写。
///      发布版只记一行 "Snapshot failed" 继续写（optimization.ps1:2360-2363），
///      按 AGENTS.md「每次系统修改必须先持久化并校验快照」，这里选择更严的一侧。
///   2) item.ok 同时要求"写入本身成功"（发布版 adapter 面只看有没有网卡暴露该 keyword）。
///   3) 后台占网进程只进 items/log，不参与 APPLIED/PARTIAL 判定（发布版返回体 ok 恒 true）。
/// </summary>
public sealed class TuneEngine
{
    private readonly ITuneBoundary _boundary;
    private readonly ITuneConfigStore _configs;
    private readonly string? _statePath;
    private readonly CancellationToken _ct;

    public TuneEngine(ITuneBoundary boundary, ITuneConfigStore? configs = null, string? statePath = null, CancellationToken ct = default)
    {
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        _configs = configs ?? new ManagedConfigStore();
        _statePath = statePath;
        _ct = ct;
    }

    public string SnapshotPath => TuneStateStore.ResolvePath(_statePath);

    // ───────────────────────────────── 调优 ─────────────────────────────────

    public TuningResult Apply(TuneRequest? request = null)
    {
        request ??= TuneRequest.Default;
        var items = new List<TuneItem>();
        var log = new List<string>();
        var changes = new List<TuningChange>();

        TuneState state;
        string snapshotNote;
        try
        {
            state = CaptureOrLoad(out snapshotNote);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
        {
            // fail-closed：没有可信快照就不动系统，否则用户失去唯一回滚依据。
            log.Add($"快照写入失败，已中止（未改动任何系统设置）：{ex.Message}");
            return new TuningResult(
                Ok: false,
                Message: $"无法保存原始值快照，已中止系统调优：{ex.Message}",
                SnapshotPath: SnapshotPath,
                Changes: changes,
                RebootRequired: false,
                Items: items,
                Log: log,
                Status: TuneStatus.Blocked);
        }

        log.Add(snapshotNote);

        var plan = NetProfiles.Plan(request.GameId, state.AppliedTier, request.InstallRoot, request.ConfigTargets);
        log.Add($"档位：{NetProfiles.GetTier(plan.Tier).Label}（{plan.Tier}）");
        foreach (var note in plan.Notes)
        {
            log.Add($"说明：{note}");
        }

        // 固定顺序：Set 与 Restore 各自按键序执行（发布版先做 Set 列表，再做 Restore 列表）。
        foreach (var key in NetProfiles.TuneOrder)
        {
            _ct.ThrowIfCancellationRequested();
            if (!plan.Set.Contains(key))
            {
                continue;
            }

            var (item, change) = ApplyKey(key, plan.Tier, state);
            items.Add(item);
            if (change is not null)
            {
                changes.Add(change);
            }

            log.Add($"{key}：{item.Detail}");
        }

        foreach (var key in NetProfiles.TuneOrder)
        {
            _ct.ThrowIfCancellationRequested();
            if (!plan.Restore.Contains(key))
            {
                continue;
            }

            var (item, change) = RestoreKey(key, state, "net-restore-", $"还原 {key}");
            items.Add(item);
            if (change is not null)
            {
                changes.Add(change);
            }

            log.Add($"还原 {key}：{item.Detail}");
        }

        if (plan.ConfigWrites.Count > 0)
        {
            var writes = new List<string>();
            foreach (var target in plan.ConfigWrites)
            {
                _ct.ThrowIfCancellationRequested();
                var (item, written) = ApplyConfig(target);
                items.Add(item);
                if (written)
                {
                    writes.Add(target.Path);
                }

                log.Add(item.Detail);
            }

            if (writes.Count > 0)
            {
                // 并集去重：上一次没还原的文件也要留在快照里，否则以后永远清不掉那块托管内容。
                var merged = new List<string>(state.ManagedConfigFiles);
                foreach (var path in writes)
                {
                    if (!merged.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        merged.Add(path);
                    }
                }

                state.ManagedConfigFiles = merged;
            }
        }

        if (request.ScanBackground)
        {
            var (item, lines) = ScanBackground();
            items.Add(item);
            log.AddRange(lines);
        }

        var blocking = items.Where(i => IsBlockingItem(i.Id) && !i.Ok).ToList();
        var tierApplied = blocking.Count == 0;
        state.AppliedTier = tierApplied ? plan.Tier : string.Empty;
        if (!tierApplied)
        {
            log.Add("部分设置未应用成功，已把档位记为\"未应用\"：下次调优会重算全部差异。");
        }
        else
        {
            log.Add($"档位已记录：{plan.Tier}");
        }

        SaveStateBestEffort(state, log);

        var status = tierApplied ? TuneStatus.Applied : TuneStatus.Partial;
        log.Add(status == TuneStatus.Applied ? "=== 系统调优完成 ===" : "=== 系统调优部分完成 ===");
        return new TuningResult(
            Ok: status == TuneStatus.Applied,
            Message: status == TuneStatus.Applied
                ? "系统调优已应用。"
                : $"系统调优未全部应用（{blocking.Count} 项失败）：" + string.Join("；", blocking.Select(i => $"{i.Title} {i.Detail}")),
            SnapshotPath: SnapshotPath,
            Changes: changes,
            RebootRequired: false,
            Items: items,
            Log: log,
            Status: status);
    }

    // ───────────────────────────────── 还原 ─────────────────────────────────

    /// <summary>
    /// 按发布版固定顺序还原：①Nagle ②tcp-global ③网卡高级属性 ④网卡电源 ⑤多媒体限流
    /// ⑥Wi-Fi 电源 ⑦游戏配置块 ⑧清快照。第二次调用找不到快照 → NO_SNAPSHOT。
    /// </summary>
    public TuningResult Restore()
    {
        var items = new List<TuneItem>();
        var log = new List<string>();

        TuneState? state;
        try
        {
            state = TuneStateStore.Load(_statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Add($"快照读取失败：{ex.Message}");
            return new TuningResult(false, $"快照读取失败：{ex.Message}", SnapshotPath, Array.Empty<TuningChange>(), false,
                items, log, TuneStatus.Blocked);
        }

        if (state is null)
        {
            log.Add("未找到快照文件。");
            return new TuningResult(
                Ok: false,
                Message: "没有可还原的快照，请先执行系统调优。",
                SnapshotPath: SnapshotPath,
                Changes: Array.Empty<TuningChange>(),
                RebootRequired: false,
                Items: items,
                Log: log,
                Status: TuneStatus.NoSnapshot);
        }

        items.Add(RestoreNagle(state, log));
        items.Add(RestoreTcpGlobal(state, log));
        items.Add(RestoreAdapterProps(state, log));
        items.Add(RestoreAdapterPower(state, log));
        items.Add(RestoreThrottle(state, log));
        items.Add(RestoreWifiPower(state, log));
        items.Add(RestoreConfigs(state, log));

        // 清快照：先记回"没有应用过档位"，再删文件；两步都失败不改变"已尝试还原"的事实。
        try
        {
            state.AppliedTier = null;
            state.ManagedConfigFiles = new List<string>();
            TuneStateStore.Save(state, _statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Add($"清除快照记账失败：{ex.Message}");
        }

        try
        {
            TuneStateStore.Delete(_statePath);
            log.Add("快照已清除。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Add($"删除快照失败：{ex.Message}");
        }

        var failed = items.Where(i => !i.Ok).ToList();
        var status = failed.Count == 0 ? TuneStatus.Restored : TuneStatus.Partial;
        log.Add(status == TuneStatus.Restored ? "=== 系统还原完成 ===" : "=== 系统还原部分完成 ===");
        return new TuningResult(
            Ok: status == TuneStatus.Restored,
            Message: status == TuneStatus.Restored
                ? "系统调优已还原。"
                : $"有 {failed.Count} 项未能还原：" + string.Join("；", failed.Select(i => $"{i.Title} {i.Detail}")),
            SnapshotPath: SnapshotPath,
            Changes: Array.Empty<TuningChange>(),
            RebootRequired: false,
            Items: items,
            Log: log,
            Status: status);
    }

    // ───────────────────────────────── 快照 ─────────────────────────────────

    private TuneState CaptureOrLoad(out string note)
    {
        var existing = TuneStateStore.Load(_statePath);
        if (existing is not null)
        {
            // 只抓一次、永不覆盖：originals 必须是"第一次调优之前"的值。
            note = $"已存在原始值快照，沿用不覆盖：{SnapshotPath}";
            return existing;
        }

        var state = TuneState.CreateEmpty();
        var adapters = _boundary.Adapters();

        foreach (var adapter in adapters)
        {
            if (string.IsNullOrEmpty(adapter.Guid) || !adapter.HasTcpipKey)
            {
                continue;
            }

            var values = new Dictionary<string, TuneRawValue>(StringComparer.Ordinal);
            foreach (var name in NetProfiles.NagleValueNames)
            {
                values[name] = _boundary.ReadRegistry(InterfaceTarget(adapter.Guid, name));
            }

            state.Nagle[adapter.Guid] = values;
        }

        // 解析失败兜底与发布版一致（optimization.ps1:2323-2333）：normal / enabled。
        var autoTuning = _boundary.ReadTcpGlobal("autotuninglevel");
        var rss = _boundary.ReadTcpGlobal("rss");
        state.TcpGlobal["autotuninglevel"] = TuneRawValue.OfText(string.IsNullOrWhiteSpace(autoTuning) ? "normal" : autoTuning);
        state.TcpGlobal["rss"] = TuneRawValue.OfText(string.IsNullOrWhiteSpace(rss) ? "enabled" : rss);

        foreach (var adapter in adapters)
        {
            var props = new Dictionary<string, TuneRawValue>(StringComparer.Ordinal);
            foreach (var keyword in NetProfiles.AdapterKeywords.Values)
            {
                props[keyword] = adapter.Properties.TryGetValue(keyword, out var value) ? value : TuneRawValue.Empty;
            }

            state.AdapterProps[adapter.Name] = props;
            state.AdapterPower[adapter.Name] = adapter.PowerTurnOff;
        }

        state.Throttle[ThrottleKeys.NetworkThrottlingIndex] =
            _boundary.ReadRegistry(ThrottleTarget(ThrottleKeys.NetworkThrottlingIndex));
        state.Throttle[ThrottleKeys.SystemResponsiveness] =
            _boundary.ReadRegistry(ThrottleTarget(ThrottleKeys.SystemResponsiveness));

        state.WifiPower = new TuneWifiPower(
            TuneRawValue.OfText(_boundary.ReadWifiPowerIndex(ac: true)),
            TuneRawValue.OfText(_boundary.ReadWifiPowerIndex(ac: false)));

        TuneStateStore.Save(state, _statePath);
        note = $"原始值快照已写入：{SnapshotPath}";
        return state;
    }

    private void SaveStateBestEffort(TuneState state, List<string> log)
    {
        try
        {
            TuneStateStore.Save(state, _statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Add($"档位记账写入失败（不影响已应用的设置）：{ex.Message}");
        }
    }

    // ───────────────────────────────── 单面应用 ─────────────────────────────────

    private (TuneItem Item, TuningChange? Change) ApplyKey(string key, string tierId, TuneState state)
    {
        var tier = NetProfiles.GetTier(tierId);
        if (tier.Get(key) is not { } value || value.IsRestore)
        {
            return (new TuneItem($"net-set-{key}", $"{key} ({tierId})", false, "该档位没有可应用的值"), null);
        }

        return key switch
        {
            "Nagle" => ApplyNagle(tierId, value),
            "InterruptModeration" or "Eee" or "FlowControl" => ApplyAdapterProperty(key, tierId, value),
            "AutoTuning" or "Rss" => ApplyTcpGlobal(key, tierId, value),
            "Throttling" => ApplyThrottle(tierId),
            "AdapterPower" => ApplyAdapterPower(tierId),
            "WifiPower" => ApplyWifiPower(tierId),
            _ => (new TuneItem($"net-set-{key}", $"{key} ({tierId})", false, $"未实现 {key} 的应用逻辑，已跳过"), null),
        };
    }

    private (TuneItem Item, TuningChange? Change) ApplyNagle(string tierId, TuneValue value)
    {
        var on = value.Kind == TuneValueKind.Bool ? value.BoolValue : value.IntValue != 0;
        var applied = 0;
        var failures = new List<string>();
        TuningChange? change = null;

        foreach (var adapter in _boundary.Adapters())
        {
            if (string.IsNullOrEmpty(adapter.Guid) || !adapter.HasTcpipKey)
            {
                continue;
            }

            var before = new List<string>();
            var wrote = true;
            foreach (var name in NetProfiles.NagleValueNames)
            {
                var nameTarget = on ? NetProfiles.NagleValueFor(name) : 0;
                var path = InterfaceTarget(adapter.Guid, name);
                var current = _boundary.ReadRegistry(path);
                if (current.Number == nameTarget)
                {
                    before.Add($"{name}={current.Display}");
                    continue;
                }

                var outcome = _boundary.WriteRegistryDword(path, nameTarget);
                if (outcome.Ok)
                {
                    before.Add($"{name}={current.Display}→{nameTarget}");
                }
                else
                {
                    wrote = false;
                    failures.Add($"{adapter.Name}/{name}: {outcome.Detail}");
                }
            }

            if (wrote)
            {
                applied++;
            }

            change ??= new TuningChange(
                $"Nagle@{adapter.Name}",
                string.Join(", ", before),
                on ? "TcpAckFrequency=1,TCPNoDelay=1,TcpDelAckTicks=0" : "全部 0",
                before.Count > 0);
        }

        var ok = applied > 0 && failures.Count == 0;
        var detail = (applied > 0 ? $"applied to {applied} interface(s)" : "no writable interface found")
                     + (failures.Count > 0 ? $"；失败：{string.Join("；", failures)}" : string.Empty);
        return (new TuneItem($"net-set-Nagle", $"Nagle ({tierId})", ok, detail), change);
    }

    private (TuneItem Item, TuningChange? Change) ApplyAdapterProperty(string key, string tierId, TuneValue value)
    {
        var keyword = NetProfiles.AdapterKeywords[key];
        var wanted = value.IntValue;
        var exposed = 0;
        var changed = new List<string>();
        var failures = new List<string>();
        var already = 0;

        foreach (var adapter in _boundary.Adapters())
        {
            if (!adapter.Properties.TryGetValue(keyword, out var current))
            {
                continue;
            }

            exposed++;
            var valid = adapter.ValidValues.TryGetValue(keyword, out var list) ? list : Array.Empty<string>();
            if (valid.Count == 0)
            {
                failures.Add($"{adapter.Name}: 驱动未公布 ValidRegistryValues");
                continue;
            }

            var target = wanted == 0 ? valid[0] : valid[^1];
            if (string.Equals(current.Display, target, StringComparison.Ordinal))
            {
                already++;
                continue;
            }

            var outcome = _boundary.SetAdapterProperty(adapter.Name, keyword, target);
            if (outcome.Ok)
            {
                changed.Add($"{adapter.Name}={target}");
            }
            else
            {
                failures.Add($"{adapter.Name}: {outcome.Detail}");
            }
        }

        var ok = exposed > 0 && failures.Count == 0;
        var detail = exposed == 0
            ? $"skipped: no adapter exposes {keyword}"
            : changed.Count > 0
                ? $"changed: {string.Join(", ", changed)}" + (failures.Count > 0 ? $"；失败：{string.Join("；", failures)}" : string.Empty)
                : failures.Count > 0
                    ? $"失败：{string.Join("；", failures)}"
                    : $"already at the tier value on {already} adapter(s)";

        return (new TuneItem($"net-set-{key}", $"{key} ({tierId})", ok, detail),
            changed.Count > 0 ? new TuningChange(keyword, "驱动当前值", string.Join(", ", changed), true) : null);
    }

    private (TuneItem Item, TuningChange? Change) ApplyTcpGlobal(string key, string tierId, TuneValue value)
    {
        var mapping = NetProfiles.TcpGlobalFor(key, NetProfiles.GetTier(tierId));
        if (mapping is not { } m)
        {
            return (new TuneItem($"net-set-{key}", $"{key} ({tierId})", false, $"未实现 {key} 的应用逻辑，已跳过"), null);
        }

        var before = _boundary.ReadTcpGlobal(m.Name);
        var outcome = _boundary.SetTcpGlobal(m.Name, m.Value);
        var detail = $"{m.Name}={m.Value} (netsh exit {outcome.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"})"
                     + (outcome.Ok ? string.Empty : $"；{outcome.Detail}");
        return (new TuneItem($"net-set-{key}", $"{key} ({tierId})", outcome.Ok, detail),
            new TuningChange(m.Name, before ?? "(未知)", m.Value, !string.Equals(before, m.Value, StringComparison.OrdinalIgnoreCase)));
    }

    private (TuneItem Item, TuningChange? Change) ApplyThrottle(string tierId)
    {
        var index = _boundary.WriteRegistryDword(ThrottleTarget(ThrottleKeys.NetworkThrottlingIndex), unchecked((int)0xFFFFFFFF));
        var responsiveness = _boundary.WriteRegistryDword(ThrottleTarget(ThrottleKeys.SystemResponsiveness), 10);
        var ok = index.Ok && responsiveness.Ok;
        var detail = ok
            ? "NetworkThrottlingIndex disabled, SystemResponsiveness=10"
            : $"写入失败：NetworkThrottlingIndex={index.Detail}；SystemResponsiveness={responsiveness.Detail}";
        return (new TuneItem($"net-set-Throttling", $"Throttling ({tierId})", ok, detail),
            new TuningChange("NetworkThrottlingIndex", "(原值)", "0xFFFFFFFF", index.Ok));
    }

    private (TuneItem Item, TuningChange? Change) ApplyAdapterPower(string tierId)
    {
        var applied = 0;
        var failures = new List<string>();
        foreach (var adapter in _boundary.Adapters())
        {
            var outcome = _boundary.SetAdapterPower(adapter.Name, "Disabled");
            if (outcome.Ok)
            {
                applied++;
            }
            else
            {
                failures.Add($"{adapter.Name}: {outcome.Detail}");
            }
        }

        var ok = applied > 0 && failures.Count == 0;
        var detail = applied > 0
            ? $"device power saving disabled on {applied} adapter(s)" + (failures.Count > 0 ? $"；失败：{string.Join("；", failures)}" : string.Empty)
            : "no adapter power setting could be changed";
        return (new TuneItem($"net-set-AdapterPower", $"AdapterPower ({tierId})", ok, detail),
            new TuningChange("AllowComputerToTurnOffDevice", "Enabled", "Disabled", applied > 0));
    }

    private (TuneItem Item, TuningChange? Change) ApplyWifiPower(string tierId)
    {
        var wifi = _boundary.Adapters().Count(a => a.IsWifi);
        if (wifi == 0)
        {
            // 发布版同样把"没有无线网卡"当作成功项，detail 写明原因。
            return (new TuneItem($"net-set-WifiPower", $"WifiPower ({tierId})", true, "maximum performance (no Wi-Fi adapter present)"), null);
        }

        var outcome = _boundary.SetWifiPowerMax();
        var detail = outcome.Ok
            ? $"maximum performance ({wifi} Wi-Fi adapter(s))"
            : $"failed: {outcome.Detail}";
        return (new TuneItem($"net-set-WifiPower", $"WifiPower ({tierId})", outcome.Ok, detail),
            new TuningChange("WLAN 省电模式", "(原值)", "最高性能", outcome.Ok));
    }

    private (TuneItem Item, bool Written) ApplyConfig(TuneConfigTarget target)
    {
        var id = "game-config";
        var title = $"游戏配置：{Path.GetFileName(target.Path)}";
        var result = _configs.Apply(target.Path, target.Lines);
        var item = new TuneItem(
            id,
            title,
            result.Ok,
            result.Ok ? $"已写入：{result.Path}" + (result.Changed ? string.Empty : "（内容未变化）") : result.Message);
        return (item, result.Ok);
    }

    // ───────────────────────────────── 单面还原 ─────────────────────────────────

    private (TuneItem Item, TuningChange? Change) RestoreKey(string key, TuneState state, string idPrefix, string titlePrefix)
    {
        var item = key switch
        {
            "Nagle" => RestoreNagleForTune(state),
            "InterruptModeration" or "Eee" or "FlowControl" => RestoreAdapterPropForTune(key, state),
            "AutoTuning" or "Rss" => RestoreTcpGlobalForTune(key, state),
            "Throttling" => RestoreThrottleForTune(state),
            "AdapterPower" => RestoreAdapterPowerForTune(state),
            "WifiPower" => RestoreWifiPowerForTune(state),
            _ => new TuneItem(string.Empty, string.Empty, false, $"未实现 {key} 的还原逻辑"),
        };

        return (item with { Id = idPrefix + key, Title = $"{titlePrefix} ({key})" }, null);
    }

    private TuneItem RestoreNagleForTune(TuneState state)
    {
        var restored = 0;
        foreach (var (guid, values) in state.Nagle)
        {
            foreach (var (name, old) in values)
            {
                if (RestoreRegistryValue(InterfaceTarget(guid, name), old))
                {
                    restored++;
                }
            }
        }

        return new TuneItem("net-restore-Nagle", "还原 Nagle", true, restored > 0 ? $"restored {restored} value(s)" : "nothing to restore");
    }

    private TuneItem RestoreAdapterPropForTune(string key, TuneState state)
    {
        var keyword = NetProfiles.AdapterKeywords[key];
        var restored = 0;
        foreach (var (adapter, props) in state.AdapterProps)
        {
            if (!props.TryGetValue(keyword, out var old) || old.IsEmpty)
            {
                continue;
            }

            if (_boundary.SetAdapterProperty(adapter, keyword, old.Display).Ok)
            {
                restored++;
            }
        }

        return new TuneItem("net-restore-" + key, $"还原 {key}", true, restored > 0 ? $"restored {restored} setting(s)" : "nothing to restore");
    }

    private TuneItem RestoreTcpGlobalForTune(string key, TuneState state)
    {
        var name = key == "Rss" ? "rss" : "autotuninglevel";
        var old = state.TcpGlobalValue(name);
        if (old.IsEmpty)
        {
            return new TuneItem("net-restore-" + key, $"还原 {key}", true, "nothing to restore");
        }

        var outcome = _boundary.SetTcpGlobal(name, old.Display);
        return new TuneItem("net-restore-" + key, $"还原 {key}", outcome.Ok, $"{name}={old.Display} (netsh exit {outcome.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"})");
    }

    private TuneItem RestoreThrottleForTune(TuneState state)
    {
        var restored = 0;
        foreach (var (name, old) in state.Throttle)
        {
            if (RestoreRegistryValue(ThrottleTarget(name), old))
            {
                restored++;
            }
        }

        return new TuneItem("net-restore-Throttling", "还原 多媒体限流", true, restored > 0 ? $"restored {restored} value(s)" : "nothing to restore");
    }

    private TuneItem RestoreAdapterPowerForTune(TuneState state)
    {
        var restored = new List<string>();
        foreach (var (adapter, old) in state.AdapterPower)
        {
            if (old.IsEmpty)
            {
                continue;
            }

            if (_boundary.SetAdapterPower(adapter, old.Display).Ok)
            {
                restored.Add(adapter);
            }
        }

        return new TuneItem("net-restore-AdapterPower", "还原 网卡电源", true,
            restored.Count > 0 ? $"restored: {string.Join(", ", restored)}" : "nothing to restore");
    }

    private TuneItem RestoreWifiPowerForTune(TuneState state)
    {
        if (!_boundary.Adapters().Any(a => a.IsWifi))
        {
            return new TuneItem("net-restore-WifiPower", "还原 Wi-Fi 电源", true, "no Wi-Fi adapter found");
        }

        var ac = state.WifiPower.Ac.IsEmpty ? null : state.WifiPower.Ac.Display;
        var dc = state.WifiPower.Dc.IsEmpty ? null : state.WifiPower.Dc.Display;
        if (ac is null && dc is null)
        {
            return new TuneItem("net-restore-WifiPower", "还原 Wi-Fi 电源", true, "nothing to restore");
        }

        var outcome = _boundary.SetWifiPowerIndex(ac, dc);
        return new TuneItem("net-restore-WifiPower", "还原 Wi-Fi 电源", outcome.Ok,
            outcome.Ok ? $"AC={ac ?? "-"}, DC={dc ?? "-"}" : $"failed: {outcome.Detail}");
    }

    // 全量还原用的 7 个面（id 与发布版一致）

    private TuneItem RestoreNagle(TuneState state, List<string> log)
    {
        var restored = 0;
        foreach (var (guid, values) in state.Nagle)
        {
            foreach (var (name, old) in values)
            {
                if (RestoreRegistryValue(InterfaceTarget(guid, name), old))
                {
                    restored++;
                }
            }
        }

        log.Add($"Nagle 设置已还原（{restored} 个值）。");
        return new TuneItem("tcp-nagle", "TCP 低延迟（Nagle）已还原", true,
            restored > 0 ? $"restored {restored} value(s)" : "无 Nagle 值需要还原");
    }

    private TuneItem RestoreTcpGlobal(TuneState state, List<string> log)
    {
        var notes = new List<string>();
        foreach (var name in new[] { "autotuninglevel", "rss" })
        {
            var old = state.TcpGlobalValue(name);
            if (old.IsEmpty)
            {
                continue;
            }

            if (_boundary.SetTcpGlobal(name, old.Display).Ok)
            {
                notes.Add($"{name}={old.Display}");
            }
        }

        log.Add(notes.Count > 0 ? $"TCP 全局参数已还原：{string.Join("，", notes)}" : "TCP 全局参数：没有需要还原的值。");
        return new TuneItem("tcp-global", "TCP 全局参数已还原", true,
            notes.Count > 0 ? string.Join("，", notes) : "nothing to restore");
    }

    private TuneItem RestoreAdapterProps(TuneState state, List<string> log)
    {
        var restored = 0;
        foreach (var (adapter, props) in state.AdapterProps)
        {
            foreach (var (keyword, old) in props)
            {
                if (old.IsEmpty)
                {
                    continue;
                }

                if (_boundary.SetAdapterProperty(adapter, keyword, old.Display).Ok)
                {
                    restored++;
                }
            }
        }

        log.Add($"网卡高级属性已还原（{restored} 项）。");
        return new TuneItem("adapter", "网卡高级属性已还原", true,
            restored > 0 ? $"restored {restored} setting(s)" : "nothing to restore");
    }

    private TuneItem RestoreAdapterPower(TuneState state, List<string> log)
    {
        var restored = new List<string>();
        foreach (var (adapter, old) in state.AdapterPower)
        {
            if (old.IsEmpty)
            {
                continue;
            }

            if (_boundary.SetAdapterPower(adapter, old.Display).Ok)
            {
                restored.Add(adapter);
            }
        }

        log.Add(restored.Count > 0 ? $"网卡电源已还原：{string.Join("，", restored)}" : "网卡电源：没有需要还原的值。");
        return new TuneItem("adapter-power", "网卡电源已还原", true,
            restored.Count > 0 ? $"restored: {string.Join(", ", restored)}" : "nothing to restore");
    }

    private TuneItem RestoreThrottle(TuneState state, List<string> log)
    {
        var restored = new List<string>();
        foreach (var (name, old) in state.Throttle)
        {
            if (RestoreRegistryValue(ThrottleTarget(name), old))
            {
                restored.Add($"{name}={old.Display}");
            }
        }

        log.Add(restored.Count > 0 ? $"多媒体限流已还原：{string.Join("，", restored)}" : "多媒体限流：没有需要还原的值。");
        return new TuneItem("throttle", "多媒体限流已还原", true,
            restored.Count > 0 ? string.Join(", ", restored) : "nothing to restore");
    }

    private TuneItem RestoreWifiPower(TuneState state, List<string> log)
    {
        if (!_boundary.Adapters().Any(a => a.IsWifi))
        {
            log.Add("Wi-Fi 还原：未找到无线网卡。");
            return new TuneItem("wifi-power", "Wi-Fi 电源已还原", true, "no Wi-Fi adapter found");
        }

        var ac = state.WifiPower.Ac.IsEmpty ? null : state.WifiPower.Ac.Display;
        var dc = state.WifiPower.Dc.IsEmpty ? null : state.WifiPower.Dc.Display;
        var outcome = _boundary.SetWifiPowerIndex(ac, dc);
        var detail = outcome.Ok ? $"AC={(ac is null ? "-" : "0x" + ac)}, DC={(dc is null ? "-" : "0x" + dc)}" : $"failed: {outcome.Detail}";
        log.Add($"Wi-Fi 电源已还原：{detail}。");
        return new TuneItem("wifi-power", "Wi-Fi 电源已还原", outcome.Ok, detail);
    }

    private TuneItem RestoreConfigs(TuneState state, List<string> log)
    {
        if (state.ManagedConfigFiles.Count == 0)
        {
            return new TuneItem("game-configs", "游戏配置文件", true, "no game config file was recorded");
        }

        var handled = new List<string>();
        var ok = true;
        foreach (var path in state.ManagedConfigFiles)
        {
            try
            {
                if (!_configs.Exists(path))
                {
                    handled.Add($"{path}（已不存在）");
                    log.Add($"游戏配置已不存在：{path}");
                    continue;
                }

                var result = _configs.Restore(path);
                if (!result.Ok)
                {
                    ok = false;
                    handled.Add($"{path}（{result.Message}）");
                    log.Add($"游戏配置还原失败：{path}：{result.Message}");
                    continue;
                }

                handled.Add(result.Changed ? $"{path}（已移除托管块）" : $"{path}（没有 Batter Babel 托管块）");
                log.Add(result.Changed ? $"已移除托管块：{path}" : $"没有托管块：{path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                ok = false;
                handled.Add($"{path}（{ex.Message}）");
                log.Add($"游戏配置还原异常：{path}：{ex.Message}");
            }
        }

        return new TuneItem("game-configs", "游戏配置文件", ok, $"{handled.Count} 个文件已处理");
    }

    // ───────────────────────────────── 后台占网 ─────────────────────────────────

    private (TuneItem Item, List<string> Log) ScanBackground()
    {
        var labels = new List<string>();
        foreach (var process in _boundary.ProcessNames())
        {
            if (string.IsNullOrWhiteSpace(process))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(process.Trim());
            if (NetProfiles.HogNames.TryGetValue(name, out var label) && !labels.Contains(label, StringComparer.Ordinal))
            {
                labels.Add(label);
            }
        }

        labels.Sort(StringComparer.CurrentCulture);
        if (labels.Count == 0)
        {
            return (new TuneItem("bg-programs", "后台占网检查", true, "none found"),
                new List<string> { "未发现后台占网程序。" });
        }

        var detail = $"found: {string.Join(", ", labels)}";
        return (new TuneItem("bg-programs", "后台占网检查", false, detail, Found: labels),
            new List<string> { $"发现后台占网程序：{string.Join("，", labels)}（只提示，不会结束这些程序）。" });
    }

    // ───────────────────────────────── 小工具 ─────────────────────────────────

    private static bool IsBlockingItem(string id) =>
        id.StartsWith("net-", StringComparison.Ordinal) || id.StartsWith("game-config", StringComparison.Ordinal);

    /// <summary>null/空 → 删值；能当 DWORD → 写 DWORD；否则写字符串（驱动写 REG_SZ 的情况）。</summary>
    private bool RestoreRegistryValue(RegistryValueTarget target, TuneRawValue old)
    {
        if (old.IsEmpty)
        {
            return _boundary.DeleteRegistryValue(target).Ok;
        }

        if (old.TryNumber(out var number))
        {
            return _boundary.WriteRegistryDword(target, number).Ok;
        }

        return _boundary.WriteRegistryString(target, old.Display).Ok;
    }

    private static RegistryValueTarget InterfaceTarget(string guid, string name) =>
        RegistryValueTarget.LocalMachine($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}", name);

    private static RegistryValueTarget ThrottleTarget(string name) =>
        RegistryValueTarget.LocalMachine(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", name);
}

/// <summary>多媒体限流面的两个值名。</summary>
public static class ThrottleKeys
{
    public const string NetworkThrottlingIndex = "NetworkThrottlingIndex";
    public const string SystemResponsiveness = "SystemResponsiveness";
}
