namespace BatterBabel.App.ViewModels;

using System.Collections.ObjectModel;
using System.Windows.Input;
using BatterBabel.Core.Domain;
using BatterBabel.Core.Hosts;
using BatterBabel.Core.Services;

/// <summary>运行日志一行。时间与正文分开绑，界面上时间用暗色等宽体（对应 bb.css:252-261）。</summary>
public sealed class LogEntry
{
    public LogEntry(string text)
    {
        Time = DateTime.Now.ToString("HH:mm:ss");
        Text = text;
    }

    public string Time { get; }

    public string Text { get; }
}

/// <summary>
/// 主窗口 ViewModel。**不引用任何 System.Windows.* 类型**（除 ICommand 接口），
/// 测量与排序全部经 IRouteOptimizer / Core 完成 —— 这是「UI 与业务隔离」在原型阶段的证明。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IRouteOptimizer _optimizer;
    private readonly IHostsApplier _hosts;
    private readonly SynchronizationContext _ui;
    private readonly IProbeObserver _observer;

    private readonly AsyncRelayCommand _startCommand;
    private readonly AsyncRelayCommand _writeHostsCommand;
    private readonly AsyncRelayCommand _restoreHostsCommand;

    private GameProfile _selectedGame;
    private string _statusText = "就绪";
    private string _progressText = "等待开始 · 0%";
    private string _measuredText = "尚未测量";
    private double _progress;
    private bool _isBusy;
    private CancellationTokenSource? _cts;

    public MainViewModel(IRouteOptimizer optimizer, IHostsApplier hosts)
    {
        _optimizer = optimizer ?? throw new ArgumentNullException(nameof(optimizer));
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _observer = new UiObserver(this);

        _startCommand = new AsyncRelayCommand(
            StartAsync,
            () => !IsBusy && SelectedGame is not null,
            exception => HandleFailure("开始优选", exception));
        _writeHostsCommand = new AsyncRelayCommand(
            WriteHostsAsync,
            () => !IsBusy,
            exception => HandleFailure("写入 hosts", exception));
        _restoreHostsCommand = new AsyncRelayCommand(
            RestoreHostsAsync,
            () => !IsBusy,
            exception => HandleFailure("还原 hosts", exception));

        DetectGameCommand = new RelayCommand(_ => AddLog(
            "检测游戏：原型阶段不做注册表/Steam 库扫描，使用目录内固定画像（生产实现见 scripts/optimization.ps1 Get-InstalledGames）"));
        BoostCommand = new RelayCommand(_ => AddLog(
            "加速游戏：QoS/流量优先级在原型阶段未接线（生产实现见 scripts/optimization.ps1，需独立设计 C# 侧系统集成）"));
        TuneSystemCommand = new RelayCommand(_ => AddLog(
            "系统调优：原型阶段未接线。真实实现必须先持久化并校验快照，恢复失败保留原值（见 AGENTS.md 系统修改约定）"));
        RestoreSystemCommand = new RelayCommand(_ => AddLog(
            "还原系统调优：原型阶段未接线，且本进程不具备任何提权/系统写能力"));

        _selectedGame = Games[0];
        BuildCards();

        AddLog("就绪 · 选择游戏后开始优选。测量不会修改 hosts。");
        AddLog(_optimizer.IsSimulated
            ? $"模拟模式：候选地址与延迟是可复现的占位数据（{_optimizer.GetType().Name}），不代表真实网络质量。"
            : $"真实探测模式：{_optimizer.GetType().Name}");
        AddLog(_hosts.IsSimulationOnly
            ? "hosts 写入被禁用：本原型只有拒绝写入的实现，不会触碰系统 hosts。"
            : "hosts 写入可用：注意这是真实系统写入。");
    }

    public IReadOnlyList<GameProfile> Games { get; } = GameCatalog.Games;

    public ObservableCollection<RouteCardViewModel> Cards { get; } = new();

    public ObservableCollection<LogEntry> Logs { get; } = new();

    public ICommand StartCommand => _startCommand;

    public ICommand WriteHostsCommand => _writeHostsCommand;

    public ICommand RestoreHostsCommand => _restoreHostsCommand;

    public ICommand DetectGameCommand { get; }

    public ICommand BoostCommand { get; }

    public ICommand TuneSystemCommand { get; }

    public ICommand RestoreSystemCommand { get; }

    public string HostsPath => HostsTarget.DisplayPath;

    public GameProfile SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (value is null || !SetProperty(ref _selectedGame, value))
            {
                return;
            }

            BuildCards();
            AddLog(Cards.Count == 0
                ? $"已选择 {value.Name} · 该游戏没有可优选线路（仅系统调优/加速可用）"
                : $"已选择 {value.Name} · {Cards.Count} 张线路卡片");
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string MeasuredText
    {
        get => _measuredText;
        private set => SetProperty(ref _measuredText, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    private void BuildCards()
    {
        Cards.Clear();
        foreach (var group in GameCatalog.GroupsFor(SelectedGame))
        {
            Cards.Add(new RouteCardViewModel(group));
        }
    }

    private async Task StartAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        Progress = 0;
        ProgressText = "测量中 · 0%";
        MeasuredText = "测量中";
        StatusText = "测量中";
        foreach (var card in Cards)
        {
            card.Reset();
        }

        if (Cards.Count == 0)
        {
            AddLog($"{SelectedGame.Name} 没有可优选线路：不发起测量，只保留系统调优/加速入口。");
            StatusText = "就绪";
            ProgressText = "等待开始 · 0%";
            MeasuredText = "无可测量线路";
            IsBusy = false;
            return;
        }

        AddLog($"开始优选 · {SelectedGame.Name} · {Cards.Count} 张卡片");

        try
        {
            var results = await _optimizer.OptimizeAsync(SelectedGame, _observer, _cts.Token);
            var domains = results.Sum(result => result.Domains.Count);
            var usable = results.SelectMany(result => result.Domains).Count(domain => domain.Best is not null);
            AddLog($"测量结束 · {domains} 个域名，{usable} 个有可用地址");
        }
        catch (OperationCanceledException)
        {
            AddLog("测量已取消");
            StatusText = "已取消";
            MeasuredText = "已取消";
        }

        IsBusy = false;
    }

    private async Task WriteHostsAsync()
    {
        var bests = Cards.SelectMany(card => card.Results).Where(domain => domain.Best is not null).ToArray();
        if (bests.Length == 0)
        {
            AddLog("写入 hosts：还没有可用地址。请先完成优选（或该游戏没有可优选线路）。");
            return;
        }

        var result = await _hosts.ApplyAsync(bests);
        AddLog(result.Success
            ? $"写入 hosts 成功 · 备份 {result.BackupPath}"
            : $"写入 hosts 未执行（{result.Code}）：{result.Message}");
    }

    private async Task RestoreHostsAsync()
    {
        var result = await _hosts.RestoreAsync();
        AddLog(result.Success
            ? $"还原 hosts 成功 · 来源 {result.BackupPath}"
            : $"还原 hosts 未执行（{result.Code}）：{result.Message}");
    }

    private void HandleFailure(string action, Exception exception) =>
        AddLog($"{action} 失败：{exception.GetType().Name}: {exception.Message}");

    /// <summary>日志上限 100 条，与界面「最近 100 条」一致。</summary>
    private void AddLog(string text)
    {
        Logs.Add(new LogEntry(text));
        while (Logs.Count > 100)
        {
            Logs.RemoveAt(0);
        }
    }

    private void RaiseCommandStates()
    {
        _startCommand.RaiseCanExecuteChanged();
        _writeHostsCommand.RaiseCanExecuteChanged();
        _restoreHostsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>后台线程产生的回调统一 Post 回 UI 线程（模拟器内部 ConfigureAwait(false)）。</summary>
    private void Post(Action action)
    {
        if (SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    private void HandleStarted(GameProfile game, int workTotal) =>
        Post(() =>
        {
            StatusText = "测量中";
            ProgressText = workTotal > 0 ? "测量中 · 0%" : "等待开始 · 0%";
        });

    private void HandleWorkDone(int workDone, int workTotal, string message) =>
        Post(() =>
        {
            if (workTotal <= 0)
            {
                return;
            }

            Progress = Math.Min(100.0, workDone * 100.0 / workTotal);
            ProgressText = $"测量中 · {Progress:0}%";
        });

    private void HandleDomainCompleted(int groupIndex, DomainProbeResult result) =>
        Post(() =>
        {
            if (groupIndex >= 0 && groupIndex < Cards.Count)
            {
                Cards[groupIndex].Apply(result);
            }

            var best = result.Best;
            AddLog(best is null
                ? $"{result.Domain} → 无可用地址（{result.BestUnavailableReason}）"
                : $"{result.Domain} → {best.Address} · {best.MedianMs:0} ms"
                  + (best.DownloadMiBps is { } mib ? $" · {mib:0.0} MiB/s" : string.Empty));
        });

    private void HandleFinished() =>
        Post(() =>
        {
            StatusText = "就绪";
            ProgressText = "完成 · 100%";
            Progress = 100;
            MeasuredText = $"完成于 {DateTime.Now:HH:mm:ss}";
        });

    /// <summary>把 Core 的回调搬到 UI 线程；接口不变，将来换成真实探测器时这里零改动。</summary>
    private sealed class UiObserver : IProbeObserver
    {
        private readonly MainViewModel _owner;

        public UiObserver(MainViewModel owner) => _owner = owner;

        public void OnStarted(GameProfile game, int workTotal) => _owner.HandleStarted(game, workTotal);

        public void OnWorkDone(int workDone, int workTotal, string message) =>
            _owner.HandleWorkDone(workDone, workTotal, message);

        public void OnDomainCompleted(int groupIndex, DomainProbeResult result) =>
            _owner.HandleDomainCompleted(groupIndex, result);

        public void OnFinished() => _owner.HandleFinished();
    }
}
