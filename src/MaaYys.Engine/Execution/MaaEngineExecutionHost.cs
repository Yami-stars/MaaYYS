using MaaYys.Engine.Session;

namespace MaaYys.Engine.Execution;

/// <summary>
/// 把 <see cref="MaaYysEngine"/> 适配成 <see cref="IExecutionHost"/>：
/// 投递走 <see cref="MaaYysEngine.RunTaskAsync"/>，心跳取 <see cref="MaaYysEngine.Events"/>（节点级事件也算进展）。
/// </summary>
public sealed class MaaEngineExecutionHost : IExecutionHost, IDisposable
{
    private readonly MaaYysEngine _engine;
    private readonly IDisposable _activitySubscription;

    public MaaEngineExecutionHost(MaaYysEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _activitySubscription = engine.Events.Subscribe(_ => Activity?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? Activity;

    /// <summary>引擎已连接且资源/控制器已绑定到任务器。</summary>
    public bool IsReady => _engine.Ready;

    public Task<TaskRun> RunTaskAsync(string entry, string? pipelineOverride, CancellationToken cancellationToken)
        => _engine.RunTaskAsync(entry, pipelineOverride, cancellationToken);

    public bool Stop() => _engine.Stop();

    public void Dispose() => _activitySubscription.Dispose();
}
