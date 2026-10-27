using MaaYys.Engine.Config;
using MaaYys.Engine.Execution;
using MaaYys.Engine.Scheduling;

namespace MaaYys.App.Services;

/// <summary>一条定时计划的运行时消息（定时页展示用）。</summary>
public sealed class TimerRunEventArgs(TimerEntry entry, string message, bool fired) : EventArgs
{
    public TimerEntry Entry { get; } = entry;

    public string Message { get; } = message;

    /// <summary>是否真的启动了队列（false = 被跳过 / 失败）。</summary>
    public bool Fired { get; } = fired;
}

/// <summary>
/// 定时任务执行器（P5.4）：把 <see cref="TimerScheduler"/> 的到点事件接到
/// <see cref="QueueRunService"/>，并在「已有队列在跑」时按 <c>force_start</c> 决定抢占还是跳过。
/// <para>
/// 生命周期跟着 workspace：页面没打开也照常到点触发；窗口关闭时由 <see cref="Stop"/> 收尾。
/// </para>
/// </summary>
public sealed class TimerRunner : IDisposable
{
    /// <summary>强制定时启动时，等待旧队列停下的最长时间。</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);

    private readonly InstanceWorkspace _workspace;
    private readonly TimerScheduler _scheduler;
    private readonly SynchronizationContext _uiContext;
    private bool _disposed;

    public TimerRunner(InstanceWorkspace workspace, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _scheduler = new TimerScheduler(() => workspace.Configurations.Timers, clock);
        _scheduler.Fired += OnFired;
        _scheduler.Ticked += OnTicked;
    }

    /// <summary>每次检查结束（定时页刷新「下次触发」）。</summary>
    public event EventHandler? Ticked;

    /// <summary>定时触发结果（启动 / 跳过 / 失败）。</summary>
    public event EventHandler<TimerRunEventArgs>? Reported;

    /// <summary>最近一条消息（定时页顶部提示）。</summary>
    public string LastMessage { get; private set; } = string.Empty;

    public bool Running => _scheduler.Running;

    /// <summary>开始轮询（重复调用无副作用）。</summary>
    public void Start() => _scheduler.Start();

    public void Stop() => _scheduler.Stop();

    /// <summary>立即检查一次（「立即检查」按钮 / 测试用）。</summary>
    public IReadOnlyList<TimerEntry> PollNow() => _scheduler.Poll();

    /// <summary>
    /// 手动试跑一条计划（不等时间到），走与到点触发完全相同的路径。
    /// </summary>
    public Task TryRunNowAsync(TimerEntry entry) => ExecuteAsync(entry, DateTimeOffset.Now);

    private void OnFired(object? sender, TimerFiredEventArgs e)
    {
        // 写 last_fired：重启后同一分钟不会补触发
        try
        {
            _workspace.Configurations.MarkTimerFired(e.Entry.Id, TimerRules.MinuteKey(e.When));
        }
        catch
        {
            // 落盘失败不该阻断本次触发
        }

        _ = ExecuteAsync(e.Entry, e.When);
    }

    private async Task ExecuteAsync(TimerEntry entry, DateTimeOffset when)
    {
        string label = string.IsNullOrWhiteSpace(entry.Label) ? $"{entry.Hour:00}:{entry.Minute:00}" : entry.Label!;

        TaskConfiguration? group = _workspace.Configurations.Find(entry.Configuration);
        if (group is null)
        {
            Report(entry, $"「{label}」绑定的配置组「{entry.Configuration}」不存在，已跳过", fired: false);
            return;
        }

        QueueRunService runner = _workspace.Runner;
        if (runner.IsBusy)
        {
            if (!entry.ForceStart)
            {
                Report(entry, $"「{label}」到点，但已有队列在运行，已跳过（未开启强制定时启动）", fired: false);
                return;
            }

            Report(entry, $"「{label}」到点，正在停止当前队列以强制定时启动…", fired: false);
            runner.Stop();
            if (!await WaitIdleAsync().ConfigureAwait(false))
            {
                Report(entry, $"「{label}」强制启动失败：旧队列 30 秒内未能停止", fired: false);
                return;
            }
        }

        try
        {
            Task<ExecutionSummary> task = runner.RunAsync(group);
            Report(entry, $"「{label}」已启动配置组「{group.Name}」（{when:HH:mm:ss}）", fired: true);

            ExecutionSummary summary = await task.ConfigureAwait(false);
            Report(
                entry,
                $"「{label}」结束：{summary.Outcome} · 成功 {summary.Succeeded} · 失败 {summary.Failed} · 跳过 {summary.Skipped}",
                fired: true);
        }
        catch (Exception ex)
        {
            Report(entry, $"「{label}」启动失败：{ex.Message}", fired: false);
        }
    }

    private async Task<bool> WaitIdleAsync()
    {
        DateTime deadline = DateTime.UtcNow + StopWait;
        while (DateTime.UtcNow < deadline)
        {
            if (!_workspace.Runner.IsBusy)
            {
                return true;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        return !_workspace.Runner.IsBusy;
    }

    private void OnTicked(object? sender, EventArgs e) => Post(() => Ticked?.Invoke(this, EventArgs.Empty));

    private void Report(TimerEntry entry, string message, bool fired)
    {
        LastMessage = message;
        Post(() => Reported?.Invoke(this, new TimerRunEventArgs(entry, message, fired)));
    }

    private void Post(Action action)
    {
        if (SynchronizationContext.Current == _uiContext)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.Fired -= OnFired;
        _scheduler.Ticked -= OnTicked;
        _scheduler.Dispose();
    }
}
