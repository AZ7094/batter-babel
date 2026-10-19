namespace BatterBabel.App.ViewModels;

using BatterBabel.Core.Domain;
using BatterBabel.Core.Optimization;

/// <summary>卡片配色语义，与 bb.css 的 .cdn-card / .api-card 对应。</summary>
public enum CardAccent
{
    Cdn,
    Api,
}

/// <summary>一张线路卡片里的一行：一个域名 + 它的结论。</summary>
public sealed class RouteRowViewModel
{
    public RouteRowViewModel(string domain, string value, bool isBest, string detail)
    {
        Domain = domain;
        Value = value;
        IsBest = isBest;
        Mark = isBest ? "●" : string.Empty;
        Detail = detail;
    }

    public string Domain { get; }

    /// <summary>右侧结论，例如 "38 ms · 12.4 MiB/s" 或 "无可用地址（样本不足）"。</summary>
    public string Value { get; }

    public bool IsBest { get; }

    /// <summary>最优行的前缀圆点（绑定给界面，非最优为空串）。</summary>
    public string Mark { get; }

    /// <summary>悬停提示：完整候选列表与排序原因。</summary>
    public string Detail { get; }
}

/// <summary>
/// 一张线路卡片（一个 OptGroup）的 ViewModel。只做展示投影，不做任何测量/排序：
/// 排序由 Core 的 ProbeRanking 完成后把 DomainProbeResult 交进来。
/// </summary>
public sealed class RouteCardViewModel : ObservableObject
{
    private readonly List<DomainProbeResult> _results = new();
    private string _summaryText = "暂无结果";
    private string _stateText = "等待优选";

    public RouteCardViewModel(OptGroup group)
    {
        Label = group.Label;
        Mode = group.Mode;
        Accent = group.Mode == ProbeMode.Cloudflare ? CardAccent.Cdn : CardAccent.Api;
        BadgeText = Accent == CardAccent.Cdn ? "CDN" : "API";
        Caption = Accent == CardAccent.Cdn ? "资源下载与更新" : "登录、公告与服务";
        Glyph = Accent == CardAccent.Cdn ? "\uE896" : "\uE774";
        DomainsText = string.Join("  /  ", group.Domains);
        Group = group;
    }

    public OptGroup Group { get; }

    public string Label { get; }

    public string Caption { get; }

    public string BadgeText { get; }

    public string Glyph { get; }

    public string DomainsText { get; }

    public ProbeMode Mode { get; }

    public CardAccent Accent { get; }

    public string Placeholder => "等待优选 · 仅展示已验证结果";

    public System.Collections.ObjectModel.ObservableCollection<RouteRowViewModel> Items { get; } = new();

    public IReadOnlyList<DomainProbeResult> Results => _results;

    public bool HasResults => Items.Count > 0;

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public void Reset()
    {
        Items.Clear();
        _results.Clear();
        RaisePropertyChanged(nameof(HasResults));
        SummaryText = "暂无结果";
        StateText = "等待优选";
    }

    /// <summary>一个域名测完：追加一行、刷新汇总。</summary>
    public void Apply(DomainProbeResult result)
    {
        // 同名域名重测时替换而不是追加，避免重复行。
        var existingIndex = _results.FindIndex(item => item.Domain == result.Domain);
        if (existingIndex >= 0)
        {
            _results[existingIndex] = result;
            Items.RemoveAt(existingIndex);
            Items.Insert(existingIndex, BuildRow(result));
        }
        else
        {
            _results.Add(result);
            Items.Add(BuildRow(result));
        }

        RaisePropertyChanged(nameof(HasResults));
        var usable = _results.Count(item => item.Best is not null);
        // 下载速度放在卡片汇总里（每行只留 IP + 延迟）：域名很长，行内塞 MiB/s 会把域名截断。
        var fastest = _results
            .Where(item => item.Best?.DownloadMiBps is > 0)
            .Select(item => item.Best!.DownloadMiBps!.Value)
            .DefaultIfEmpty(0)
            .Max();
        SummaryText = fastest > 0
            ? $"{_results.Count} 个域名 · {usable} 可用 · {fastest:0.0} MiB/s"
            : $"{_results.Count} 个域名 · {usable} 个可用";
        StateText = "已优选";
    }

    private RouteRowViewModel BuildRow(DomainProbeResult result)
    {
        var best = result.Best;
        var value = best is null
            ? $"无可用地址（{Explain(result.BestUnavailableReason)}）"
            : $"{best.Address} · {best.MedianMs:0} ms";

        var detail = string.Join(Environment.NewLine, result.Candidates.Select(c =>
            $"{c.Address}  {c.Verdict}  {c.MedianMs?.ToString("0") ?? "-"} ms  samples={c.Samples}  {(c.Rankable ? "rankable" : c.RankReason.ToString())}"));

        return new RouteRowViewModel(result.Domain, value, best is not null, detail);
    }

    /// <summary>把生产里的英文 rankReason 转成界面中文；未识别的保留原文，避免吞掉新信息。</summary>
    private static string Explain(string reason) => reason switch
    {
        "NoValidLatencySamples" => "无有效延迟样本",
        "InsufficientSamples" => "样本不足",
        "NotUsable" => "不可用",
        "AntiBotPage" => "反机器人页",
        "Timeout" => "超时",
        "NoCandidate" => "无候选地址",
        "None" => "无",
        _ => reason,
    };
}
