using MaaYys.Engine.Session;

namespace MaaYys.Engine.Execution;

/// <summary>
/// 队列执行引擎（P5.3）：按顺序投递队列项，负责启动 / 暂停 / 停止、状态回调、
/// 停滞超时（StallTimeout）、失败重试、单任务运行时长上限。
///
/// 设计要点：
/// <list type="bullet">
/// <item>只依赖 <see cref="IExecutionHost"/>，不碰原生句柄，可无框架单测。</item>
/// <item>暂停语义 = 中止当前任务并记住下标，恢复时**从该任务重新开始**（框架不支持真正的冻结）。</item>
/// <item>停滞检测靠宿主转发的任一框架事件做心跳；<see cref="CheckTimeouts"/> 可单独调用（测试用）。</item>
/// </list>
/// </summary>
public sealed class QueueExecutor : IDisposable
{
    private const int WatchdogIntervalMs = 500;

    private readonly IExecutionHost _host;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly Timer _watchdog;
    private readonly List<ExecutionItemResult> _results = [];

    private IReadOnlyList<ExecutionItem> _items = [];
    private CancellationTokenSource? _attemptCts;
    private ExecutionItem? _currentItem;
    private int _currentIndex = -1;
    private DateTimeOffset _itemStartedAt;
    private DateTimeOffset _lastActivity;
    private string? _attemptAbortReason;
    private volatile bool _pauseRequested;
    private volatile bool _stopRequested;
    private volatile ExecutionState _state = ExecutionState.Idle;
    private int _resumeIndex;
    private bool _disposed;

    public QueueExecutor(IExecutionHost host, ExecutionOptions? options = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        Options = (options ?? ExecutionOptions.Default).Sanitized();
        _now = clock ?? (() => DateTimeOffset.Now);

        _host.Activity += OnHostActivity;
        _watchdog = new Timer(_ => CheckTimeouts(), null, WatchdogIntervalMs, WatchdogIntervalMs);
    }

    /// <summary>当前生效的策略（已规整为合法值）。</summary>
    public ExecutionOptions Options { get; }

    public ExecutionState State => _state;

    /// <summary>正在运行的队列项（空闲时为 null）。</summary>
    public ExecutionItem? CurrentItem => _currentItem;

    /// <summary>状态变化（Running / Paused / Stopping / Idle）。</summary>
    public event EventHandler<ExecutionState>? StateChanged;

    /// <summary>队列项运行态变化（驱动 UI 着色）。</summary>
    public event EventHandler<QueueItemStatusChangedEventArgs>? ItemStatusChanged;

    /// <summary>
    /// 从头或从指定下标开始执行整个队列。返回时给出汇总（含暂停/停止原因与下次恢复下标）。
    /// </summary>
    /// <exception cref="InvalidOperationException">执行器正忙，或宿主未就绪。</exception>
    public Task<ExecutionSummary> RunAsync(
        IReadOnlyList<ExecutionItem> items,
        int startIndex = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_state is ExecutionState.Running or ExecutionState.Paused or ExecutionState.Stopping)
            {
                throw new InvalidOperationException($"执行器正忙（{_state}），请先停止当前运行");
            }

            if (!_host.IsReady)
            {
                throw new InvalidOperationException("引擎未就绪：请先连接设备并加载资源");
            }

            _pauseRequested = false;
            _stopRequested = false;
            _attemptAbortReason = null;
            _attemptCts = null;
            _currentItem = null;
            _currentIndex = -1;
            _items = items;
            _resumeIndex = Math.Clamp(startIndex, 0, items.Count);
            _results.Clear();
            _state = ExecutionState.Running;
        }

        RaiseStateChanged(ExecutionState.Running);
        return RunCoreAsync(items, Math.Clamp(startIndex, 0, items.Count), cancellationToken);
    }

    /// <summary>
    /// 暂停：中止当前任务并记住下标。<see cref="Resume"/> 后从该任务重新开始。
    /// 返回 false 表示当前不是运行态。
    /// </summary>
    public bool Pause()
    {
        if (_state != ExecutionState.Running)
        {
            return false;
        }

        _pauseRequested = true;
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            if (_attemptCts is not null)
            {
                _attemptAbortReason = "pause";
                cts = _attemptCts;
            }
        }

        CancelQuietly(cts);
        return true;
    }

    /// <summary>
    /// 从暂停点恢复执行（内部重新开一轮 <see cref="RunAsync"/>）。
    /// 返回 null 表示当前不是暂停态；否则返回本轮执行的汇总任务。
    /// </summary>
    public Task<ExecutionSummary>? Resume()
    {
        IReadOnlyList<ExecutionItem> items;
        int next;
        lock (_gate)
        {
            if (_state != ExecutionState.Paused)
            {
                return null;
            }

            items = _items;
            next = _resumeIndex;
            _state = ExecutionState.Idle;
            _pauseRequested = false;
        }

        return items.Count == 0 ? null : RunAsync(items, next);
    }

    /// <summary>停止整个队列（中止当前任务，不再继续后续项）。</summary>
    public bool Stop()
    {
        if (_state == ExecutionState.Idle)
        {
            return false;
        }

        _stopRequested = true;
        CancellationTokenSource? cts = null;
        bool hasAttempt;
        lock (_gate)
        {
            hasAttempt = _attemptCts is not null;
            if (hasAttempt)
            {
                _attemptAbortReason = "stop";
                cts = _attemptCts;
            }
        }

        if (!hasAttempt)
        {
            // 暂停态或任务间隙：没有可中止的任务，直接收尾
            _state = ExecutionState.Idle;
            RaiseStateChanged(ExecutionState.Idle);
            return true;
        }

        RaiseStateChanged(ExecutionState.Stopping);
        CancelQuietly(cts);

        try
        {
            _host.Stop();
        }
        catch
        {
            // 停止请求失败不应阻断状态收尾
        }

        return true;
    }

    /// <summary>
    /// 检查停滞超时与运行时长上限（看门狗每 500ms 调一次；测试可直接调用以避开真实计时）。
    /// 返回是否触发中止。
    /// </summary>
    public bool CheckTimeouts()
    {
        CancellationTokenSource? cts = null;
        ExecutionItem? item = null;
        int index = -1;
        string? message = null;

        lock (_gate)
        {
            if (_disposed || _state != ExecutionState.Running || _attemptCts is null || _currentItem is null)
            {
                return false;
            }

            DateTimeOffset now = _now();
            if (Options.MaxDurationSeconds > 0 &&
                (now - _itemStartedAt).TotalSeconds >= Options.MaxDurationSeconds)
            {
                _attemptAbortReason = "duration";
                message = $"超过运行时长上限（{Options.MaxDurationSeconds}s），已中止";
            }
            else if (Options.StallTimeoutSeconds > 0 &&
                     (now - _lastActivity).TotalSeconds >= Options.StallTimeoutSeconds)
            {
                _attemptAbortReason = "stall";
                message = $"停滞超时（{Options.StallTimeoutSeconds}s 无进展），已中止";
            }

            if (message is null)
            {
                return false;
            }

            cts = _attemptCts;
            item = _currentItem;
            index = _currentIndex;
        }

        CancelQuietly(cts);

        if (item is not null)
        {
            Report(item, index, TaskRunStatus.Running, message);
        }

        return true;
    }

    /// <summary>宿主每收到一条框架事件就调用一次，用于刷新停滞心跳。</summary>
    public void NotifyActivity()
    {
        lock (_gate)
        {
            _lastActivity = _now();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Activity -= OnHostActivity;
        _watchdog.Dispose();

        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _attemptCts;
        }

        CancelQuietly(cts);
    }

    // ---- 执行主循环 ----

    private async Task<ExecutionSummary> RunCoreAsync(
        IReadOnlyList<ExecutionItem> items,
        int startIndex,
        CancellationToken externalToken)
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        int index = startIndex;
        int succeeded = 0;
        int failed = 0;
        int skipped = 0;
        bool abort = false;
        ExecutionOutcome outcome = ExecutionOutcome.Completed;
        string? message = null;

        try
        {
            for (; index < items.Count && !abort; index++)
            {
                _resumeIndex = index;

                if (_stopRequested || runCts.IsCancellationRequested)
                {
                    outcome = ExecutionOutcome.Stopped;
                    message = "已停止";
                    abort = true;
                    break;
                }

                if (_pauseRequested)
                {
                    outcome = ExecutionOutcome.Paused;
                    message = "已暂停";
                    abort = true;
                    break;
                }

                ExecutionItem item = items[index];

                if (!item.Enabled)
                {
                    skipped++;
                    Report(item, index, TaskRunStatus.Skipped, "已禁用，跳过");
                    AddResult(item, TaskRunStatus.Skipped, "已禁用", null);
                    continue;
                }

                Report(item, index, TaskRunStatus.Pending, "排队中");

                AttemptResult? final = null;
                int maxAttempts = Options.MaxRetries + 1;

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    if (attempt > 0)
                    {
                        if (Options.RetryDelayMs > 0)
                        {
                            Report(item, index, TaskRunStatus.Pending, $"等待重试（{attempt}/{Options.MaxRetries}）");
                            try
                            {
                                await Task.Delay(Options.RetryDelayMs, runCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }
                    }

                    if (_stopRequested || _pauseRequested || runCts.IsCancellationRequested)
                    {
                        break;
                    }

                    Report(item, index, TaskRunStatus.Running, attempt == 0 ? "运行中" : $"重试第 {attempt} 次");
                    AttemptResult result = await RunOnceAsync(item, index, runCts.Token).ConfigureAwait(false);

                    if (result.Kind == AttemptKind.Success)
                    {
                        final = result;
                        break;
                    }

                    if (result.Kind is AttemptKind.Pause or AttemptKind.Stop)
                    {
                        final = result;
                        break;
                    }

                    // 失败：还有重试机会就继续，否则定案
                    final = result;
                    if (attempt < maxAttempts - 1 && !result.Fatal)
                    {
                        continue;
                    }

                    break;
                }

                if (final is null)
                {
                    // 未真正执行就被打断了（停止/暂停）
                    outcome = _stopRequested ? ExecutionOutcome.Stopped : ExecutionOutcome.Paused;
                    message = _stopRequested ? "已停止" : "已暂停";
                    abort = true;
                    break;
                }

                switch (final.Kind)
                {
                    case AttemptKind.Success:
                        succeeded++;
                        Report(item, index, TaskRunStatus.Done, "完成", final.Run);
                        AddResult(item, TaskRunStatus.Done, null, final.Run?.Duration);
                        break;

                    case AttemptKind.Pause:
                        Report(item, index, TaskRunStatus.Stopped, "已暂停，稍后从本任务重跑", final.Run);
                        AddResult(item, TaskRunStatus.Stopped, "已暂停", final.Run?.Duration);
                        outcome = ExecutionOutcome.Paused;
                        message = $"已暂停于「{item.DisplayName}」";
                        _resumeIndex = index;
                        abort = true;
                        break;

                    case AttemptKind.Stop:
                        Report(item, index, TaskRunStatus.Stopped, "已停止", final.Run);
                        AddResult(item, TaskRunStatus.Stopped, "已停止", final.Run?.Duration);
                        outcome = ExecutionOutcome.Stopped;
                        message = "已停止";
                        abort = true;
                        break;

                    default:
                        failed++;
                        Report(item, index, TaskRunStatus.Failed, final.Message, final.Run);
                        AddResult(item, TaskRunStatus.Failed, final.Message, final.Run?.Duration);
                        if (Options.StopOnFailure)
                        {
                            outcome = ExecutionOutcome.Failed;
                            message = $"「{item.DisplayName}」失败：{final.Message}";
                            abort = true;
                        }

                        break;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _attemptCts = null;
                _currentItem = null;
                _currentIndex = -1;
            }

            if (index > items.Count)
            {
                index = items.Count;
            }

            _resumeIndex = outcome == ExecutionOutcome.Paused
                ? Math.Min(_resumeIndex, Math.Max(0, items.Count - 1))
                : index;

            if (!abort && outcome == ExecutionOutcome.Completed)
            {
                _resumeIndex = items.Count;
                message ??= $"队列完成：成功 {succeeded} · 失败 {failed} · 跳过 {skipped}";
            }

            _state = outcome == ExecutionOutcome.Paused ? ExecutionState.Paused : ExecutionState.Idle;
            RaiseStateChanged(_state);
        }

        ExecutionItemResult[] results;
        lock (_gate)
        {
            results = [.. _results];
        }

        return new ExecutionSummary(outcome, succeeded, failed, skipped, _resumeIndex, message, results);
    }

    private async Task<AttemptResult> RunOnceAsync(ExecutionItem item, int index, CancellationToken runToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        lock (_gate)
        {
            _attemptCts = attemptCts;
            _currentItem = item;
            _currentIndex = index;
            _itemStartedAt = _now();
            _lastActivity = _now();
            _attemptAbortReason = null;
        }

        TaskRun run;
        try
        {
            run = await _host.RunTaskAsync(item.Entry, item.PipelineOverride, attemptCts.Token).ConfigureAwait(false)
                  ?? TaskRun.Rejected(item.Entry, "宿主未返回任务记录");
        }
        catch (OperationCanceledException)
        {
            run = TaskRun.Rejected(item.Entry, "已取消");
        }
        catch (Exception ex)
        {
            run = TaskRun.Rejected(item.Entry, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _attemptCts = null;
            }
        }

        string? reason;
        lock (_gate)
        {
            reason = _attemptAbortReason;
        }

        return reason switch
        {
            "pause" => AttemptResult.Paused(run),
            "stop" => AttemptResult.Stopped(run),
            "stall" => AttemptResult.Failed(run, $"停滞超时（{Options.StallTimeoutSeconds}s 无进展）", fatal: true),
            "duration" => AttemptResult.Failed(run, $"超过运行时长上限（{Options.MaxDurationSeconds}s）", fatal: true),
            _ => run.State switch
            {
                TaskState.Succeeded => AttemptResult.Succeeded(run),
                TaskState.Stopped => AttemptResult.Stopped(run),
                _ => AttemptResult.Failed(run, run.FailureMessage ?? $"任务失败（{run.State}）", fatal: false),
            },
        };
    }

    // ---- 基础设施 ----

    private void OnHostActivity(object? sender, EventArgs e) => NotifyActivity();

    private void Report(ExecutionItem item, int index, TaskRunStatus status, string? message, TaskRun? run = null)
    {
        try
        {
            ItemStatusChanged?.Invoke(this, new QueueItemStatusChangedEventArgs(item, index, status, message, run));
        }
        catch
        {
            // 订阅者异常不得打断执行
        }
    }

    private void AddResult(ExecutionItem item, TaskRunStatus status, string? message, TimeSpan? duration)
    {
        lock (_gate)
        {
            _results.Add(new ExecutionItemResult(item.Id, item.DisplayName, status, message, duration));
        }
    }

    private void RaiseStateChanged(ExecutionState state)
    {
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch
        {
            // ignored
        }
    }

    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已随任务结束释放
        }
        catch (AggregateException)
        {
            // 回调异常不影响中止
        }
    }

    private enum AttemptKind
    {
        Success,
        Failure,
        Pause,
        Stop,
    }

    private sealed record AttemptResult(AttemptKind Kind, TaskRun? Run, string? Message, bool Fatal)
    {
        public static AttemptResult Succeeded(TaskRun run) => new(AttemptKind.Success, run, null, false);

        public static AttemptResult Failed(TaskRun run, string message, bool fatal) =>
            new(AttemptKind.Failure, run, message, fatal);

        public static AttemptResult Paused(TaskRun run) => new(AttemptKind.Pause, run, "已暂停", false);

        public static AttemptResult Stopped(TaskRun run) => new(AttemptKind.Stop, run, "已停止", false);
    }
}
