using MaaYys.App.ViewModels;
using MaaYys.Engine;
using MaaYys.Engine.Config;
using MaaYys.Engine.Execution;
using MaaYys.Engine.Logging;

namespace MaaYys.App.Services;

/// <summary>
/// 队列执行入口（P5.3 落地 / P5.4 复用）：整个程序只有**一份**执行器，
/// 任务页的「开始运行」与定时器到点触发都走这里。
/// <para>
/// 为什么提到 workspace 层：定时触发时任务页可能根本没创建过，
/// 若执行器归页面所有，定时就得自己再起一份 —— 两个队列会同时向 Maafw 投递任务。
/// 统一到 <see cref="InstanceWorkspace"/> 后，天然互斥：正在跑就直接拒绝第二次启动。
/// </para>
/// </summary>
public sealed class QueueRunService : ViewModelBase, IDisposable
{
    private readonly InstanceWorkspace _workspace;
    private readonly ExecutionOptions _options;

    private QueueExecutor? _executor;
    private MaaEngineExecutionHost? _host;
    private MaaYysEngine? _engine;
    private bool _starting;
    private bool _disposed;

    public QueueRunService(InstanceWorkspace workspace, ExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _options = options ?? ExecutionOptions.Default;
    }

    /// <summary>队列项运行态变化（供任务页着色）。事件来自引擎线程，订阅方需自行切 UI 线程。</summary>
    public event EventHandler<QueueItemStatusChangedEventArgs>? ItemStatusChanged;

    /// <summary>执行器状态变化（Running / Paused / Stopping / Idle）。</summary>
    public event EventHandler<ExecutionState>? StateChanged;

    public ExecutionState State => _executor?.State ?? ExecutionState.Idle;

    /// <summary>是否正在占用引擎（运行 / 暂停 / 停止中）。</summary>
    public bool IsBusy => _starting ||
                          State is ExecutionState.Running or ExecutionState.Paused or ExecutionState.Stopping;

    /// <summary>正在运行的队列项（空闲为 null）。</summary>
    public ExecutionItem? CurrentItem => _executor?.CurrentItem;

    /// <summary>把配置组的队列编译成执行项（与参数面板预览同一条编译路径）。</summary>
    public List<ExecutionItem> BuildItems(TaskConfiguration group)
    {
        ArgumentNullException.ThrowIfNull(group);
        InterfaceConfig? config = _workspace.Resources.Config;
        return config is null
            ? []
            : ExecutionPlan.Build(config, group.Tasks, group.GlobalOptions);
    }

    /// <summary>
    /// 开始执行一个配置组的队列。
    /// </summary>
    /// <exception cref="InvalidOperationException">引擎未就绪、队列为空，或已有队列在跑。</exception>
    public Task<ExecutionSummary> RunAsync(TaskConfiguration group, int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(group);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsBusy)
        {
            throw new InvalidOperationException("已有队列在运行，请先停止");
        }

        MaaYysEngine? engine = _workspace.Current?.Engine;
        if (engine is null)
        {
            throw new InvalidOperationException("当前实例未连接：请先在实例页连接设备");
        }

        if (!engine.Ready)
        {
            throw new InvalidOperationException("引擎未就绪：请先连接设备并完成资源加载");
        }

        List<ExecutionItem> items = BuildItems(group);
        if (items.Count == 0)
        {
            throw new InvalidOperationException("队列为空：先添加任务");
        }

        if (!items.Any(i => i.Enabled))
        {
            throw new InvalidOperationException("队列里没有启用的任务");
        }

        QueueExecutor executor = EnsureExecutor(engine);
        _starting = true;
        OnPropertyChanged(nameof(IsBusy));

        _workspace.Log.Write(
            LogLevel.Info,
            "Runner",
            $"开始执行配置组「{group.Name}」：{items.Count(i => i.Enabled)} 项启用 / 共 {items.Count} 项"
                + (startIndex > 0 ? $"，从第 {startIndex + 1} 项开始" : string.Empty),
            "queue");

        Task<ExecutionSummary> task;
        try
        {
            task = executor.RunAsync(items, Math.Clamp(startIndex, 0, items.Count));
        }
        catch
        {
            _starting = false;
            OnPropertyChanged(nameof(IsBusy));
            throw;
        }

        _ = task.ContinueWith(
            t => LogSummary(group, t),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _ = task.ContinueWith(
            _ =>
            {
                _starting = false;
                OnPropertyChanged(nameof(IsBusy));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    /// <summary>停止整个队列。返回 false 表示当前没在跑。</summary>
    public bool Stop() => _executor?.Stop() ?? false;

    /// <summary>暂停：中止当前任务并记住下标。</summary>
    public bool Pause() => _executor?.Pause() ?? false;

    /// <summary>从暂停点继续；返回 null 表示当前不是暂停态。</summary>
    public Task<ExecutionSummary>? Resume() => _executor?.Resume();

    /// <summary>取（或重建）执行器：引擎换了（切实例/重连）必须换宿主。</summary>
    private QueueExecutor EnsureExecutor(MaaYysEngine engine)
    {
        if (_executor is not null && ReferenceEquals(_engine, engine))
        {
            return _executor;
        }

        DisposeExecutor();
        _engine = engine;
        _host = new MaaEngineExecutionHost(engine);
        _executor = new QueueExecutor(_host, _options);
        _executor.ItemStatusChanged += OnItemStatusChanged;
        _executor.StateChanged += OnExecutorStateChanged;
        return _executor;
    }

    private void DisposeExecutor()
    {
        if (_executor is not null)
        {
            _executor.ItemStatusChanged -= OnItemStatusChanged;
            _executor.StateChanged -= OnExecutorStateChanged;
            _executor.Dispose();
            _executor = null;
        }

        _host?.Dispose();
        _host = null;
        _engine = null;
    }

    private void OnItemStatusChanged(object? sender, QueueItemStatusChangedEventArgs e)
    {
        LogItem(e);
        ItemStatusChanged?.Invoke(this, e);
    }

    /// <summary>队列项的运行态变化 → 日志（P5.5：失败/跳过会自动带截图卡片）。</summary>
    private void LogItem(QueueItemStatusChangedEventArgs e)
    {
        string name = string.IsNullOrWhiteSpace(e.Item.DisplayName) ? e.Item.Entry : e.Item.DisplayName;
        string duration = e.Run?.Duration is { } span ? $"（{span.TotalSeconds:F1}s）" : string.Empty;

        switch (e.Status)
        {
            case TaskRunStatus.Running:
                _workspace.Log.Debug("Runner", $"开始执行：{name}", "queue");
                break;
            case TaskRunStatus.Done:
                _workspace.Log.Info("Runner", $"完成：{name}{duration}", "queue");
                break;
            case TaskRunStatus.Failed:
                _workspace.Log.Error(
                    "Runner",
                    $"失败：{name}{duration}{(string.IsNullOrWhiteSpace(e.Message) ? string.Empty : $" · {e.Message}")}",
                    "queue");
                break;
            case TaskRunStatus.Skipped:
                _workspace.Log.Warn(
                    "Runner",
                    $"跳过：{name}{(string.IsNullOrWhiteSpace(e.Message) ? string.Empty : $" · {e.Message}")}",
                    "queue");
                break;
            case TaskRunStatus.Stopped:
                _workspace.Log.Warn("Runner", $"已停止：{name}", "queue");
                break;
        }
    }

    /// <summary>整轮汇总（成功/失败/跳过 + 结束原因）。</summary>
    private void LogSummary(TaskConfiguration group, Task<ExecutionSummary> task)
    {
        try
        {
            ExecutionSummary summary = task.Result;
            LogLevel level = summary.Failed > 0 ? LogLevel.Warn : LogLevel.Info;
            _workspace.Log.Write(
                level,
                "Runner",
                $"配置组「{group.Name}」结束：{summary.Outcome} · 成功 {summary.Succeeded} · 失败 {summary.Failed} · 跳过 {summary.Skipped}"
                    + (string.IsNullOrWhiteSpace(summary.Message) ? string.Empty : $" · {summary.Message}"),
                "queue");
        }
        catch (AggregateException ex)
        {
            _workspace.Log.Error("Runner", $"配置组「{group.Name}」执行异常：{ex.InnerException?.Message ?? ex.Message}", "queue");
        }
    }

    private void OnExecutorStateChanged(object? sender, ExecutionState state)
    {
        switch (state)
        {
            case ExecutionState.Paused:
                _workspace.Log.Warn("Runner", "队列已暂停（继续时从当前任务重跑）", "queue");
                break;
            case ExecutionState.Stopping:
                _workspace.Log.Info("Runner", "正在停止队列…", "queue");
                break;
            case ExecutionState.Idle:
                _workspace.Log.Debug("Runner", "队列回到空闲", "queue");
                break;
        }

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsBusy));
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeExecutor();
    }
}
