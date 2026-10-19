namespace BatterBabel.Core.Services;

using BatterBabel.Core.Domain;

/// <summary>
/// 探测过程的观察者。ViewModel 只通过这个接口拿到进度与结果，
/// 因此"界面不直接测网、测网代码不知道界面"这条隔离边在原型阶段就成立了。
/// </summary>
public interface IProbeObserver
{
    /// <summary>开始探测：workTotal 是全部候选地址数量，用于算进度百分比。</summary>
    void OnStarted(GameProfile game, int workTotal);

    /// <summary>每测完一个候选地址回调一次（进度条据此推进）。</summary>
    void OnWorkDone(int workDone, int workTotal, string message);

    /// <summary>一个域名测完：groupIndex 对应 game 分组在界面卡片列表中的下标。</summary>
    void OnDomainCompleted(int groupIndex, DomainProbeResult result);

    /// <summary>全部结束（无论是否有赢家）。</summary>
    void OnFinished();
}

/// <summary>线路优选服务。真实实现将由 C# 重写的探测器提供；原型阶段为模拟实现。</summary>
public interface IRouteOptimizer
{
    /// <summary>是否只是模拟数据（界面必须如实告知用户，不允许伪装成真实测量）。</summary>
    bool IsSimulated { get; }

    Task<IReadOnlyList<RouteGroupResult>> OptimizeAsync(
        GameProfile game,
        IProbeObserver observer,
        CancellationToken cancellationToken = default);
}
