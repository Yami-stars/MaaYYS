using MaaYys.Engine;
using MaaYys.Engine.Execution;
using MaaYys.Engine.Native;
using MaaYys.Engine.Session;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// P5.3 执行引擎：队列顺序、跳过、重试、失败即停、停止、暂停/恢复、停滞超时、时长上限。
/// 全部用假宿主驱动，不依赖 MaaFramework / 设备。
/// </summary>
public sealed class QueueExecutorTests
{
    private static int _runId;

    private static ExecutionItem Item(string id, string entry, bool enabled = true)
        => new(id, entry, $"{{\"{entry}\":{{}}}}", entry, enabled);

    private static TaskRun Succeed(string entry)
    {
        TaskStateMachine machine = new();
        TaskRun run = machine.Start(Interlocked.Increment(ref _runId), entry);
        machine.EnsureFinal(run, MaaStatus.Succeeded);
        return run;
    }

    private static TaskRun Fail(string entry, string message = "boom")
    {
        TaskStateMachine machine = new();
        TaskRun run = machine.Start(Interlocked.Increment(ref _runId), entry);
        machine.EnsureFinal(run, MaaStatus.Failed, message);
        return run;
    }

    private static TaskRun StopRun(string entry)
    {
        TaskStateMachine machine = new();
        TaskRun run = machine.Start(Interlocked.Increment(ref _runId), entry);
        machine.MarkStopped("cancelled");
        return run;
    }

    [Fact]
    public async Task RunAsync_ExecutesAllItemsInOrder()
    {
        var host = new FakeHost();
        using var executor = new QueueExecutor(host, new ExecutionOptions { StallTimeoutSeconds = 0 });
        List<(string Id, TaskRunStatus Status)> log = [];
        executor.ItemStatusChanged += (_, e) => log.Add((e.Item.Id, e.Status));

        ExecutionSummary summary = await executor.RunAsync(
            [Item("a", "A"), Item("b", "B"), Item("c", "C")]);

        Assert.Equal(ExecutionOutcome.Completed, summary.Outcome);
        Assert.Equal(3, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(new[] { "A", "B", "C" }, host.Posted);

        // 每项都要先 Pending 后 Running 最后 Done
        foreach (string id in (string[])["a", "b", "c"])
        {
            var statuses = log.Where(x => x.Id == id).Select(x => x.Status).ToList();
            Assert.Equal(new[] { TaskRunStatus.Pending, TaskRunStatus.Running, TaskRunStatus.Done }, statuses);
        }

        Assert.Equal(ExecutionState.Idle, executor.State);
    }

    [Fact]
    public async Task RunAsync_SkipsDisabledItems()
    {
        var host = new FakeHost();
        using var executor = new QueueExecutor(host, new ExecutionOptions { StallTimeoutSeconds = 0 });

        ExecutionSummary summary = await executor.RunAsync(
            [Item("a", "A"), Item("b", "B", enabled: false), Item("c", "C")]);

        Assert.Equal(ExecutionOutcome.Completed, summary.Outcome);
        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(new[] { "A", "C" }, host.Posted);
        Assert.Contains(summary.Results, r => r.Id == "b" && r.Status == TaskRunStatus.Skipped);
    }

    [Fact]
    public async Task RunAsync_RetriesUntilSuccess()
    {
        var host = new FakeHost();
        int attempts = 0;
        host.Handler = (entry, _) => Task.FromResult(++attempts < 3 ? Fail(entry) : Succeed(entry));

        using var executor = new QueueExecutor(
            host, new ExecutionOptions { StallTimeoutSeconds = 0, MaxRetries = 2, RetryDelayMs = 0 });

        ExecutionSummary summary = await executor.RunAsync([Item("a", "A")]);

        Assert.Equal(ExecutionOutcome.Completed, summary.Outcome);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(3, host.Posted.Count);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public async Task RunAsync_StopsQueueWhenTaskFailsAndStopOnFailure()
    {
        var host = new FakeHost();
        host.Handler = (entry, _) => Task.FromResult(Fail(entry, "识别失败"));

        using var executor = new QueueExecutor(
            host, new ExecutionOptions { StallTimeoutSeconds = 0, MaxRetries = 1, RetryDelayMs = 0 });

        ExecutionSummary summary = await executor.RunAsync([Item("a", "A"), Item("b", "B")]);

        Assert.Equal(ExecutionOutcome.Failed, summary.Outcome);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(2, host.Posted.Count);      // 首次 + 1 次重试
        Assert.DoesNotContain("B", host.Posted); // 失败即停，后续任务不跑
        Assert.Equal(1, summary.NextIndex);
        Assert.Equal(ExecutionState.Idle, executor.State);
    }

    [Fact]
    public async Task RunAsync_ContinuesWhenStopOnFailureDisabled()
    {
        var host = new FakeHost();
        host.Handler = (entry, _) => Task.FromResult(
            entry == "A" ? Fail(entry) : Succeed(entry));

        using var executor = new QueueExecutor(
            host, new ExecutionOptions { StallTimeoutSeconds = 0, StopOnFailure = false });

        ExecutionSummary summary = await executor.RunAsync([Item("a", "A"), Item("b", "B")]);

        Assert.Equal(ExecutionOutcome.Completed, summary.Outcome);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Succeeded);
    }

    [Fact]
    public async Task Stop_MidRun_EndsWithStoppedOutcome()
    {
        var host = new FakeHost { BlockUntilCancelled = true };
        using var executor = new QueueExecutor(host, new ExecutionOptions { StallTimeoutSeconds = 0 });

        Task<ExecutionSummary> running = executor.RunAsync([Item("a", "A"), Item("b", "B")]);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(executor.Stop());
        ExecutionSummary summary = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ExecutionOutcome.Stopped, summary.Outcome);
        Assert.Contains(summary.Results, r => r.Id == "a" && r.Status == TaskRunStatus.Stopped);
        Assert.DoesNotContain("B", host.Posted);
        Assert.Equal(ExecutionState.Idle, executor.State);
    }

    [Fact]
    public async Task Pause_ThenResume_ContinuesFromPausedItem()
    {
        var host = new FakeHost { BlockUntilCancelled = true };
        using var executor = new QueueExecutor(host, new ExecutionOptions { StallTimeoutSeconds = 0 });

        Task<ExecutionSummary> running = executor.RunAsync([Item("a", "A")]);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(executor.Pause());
        ExecutionSummary paused = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ExecutionOutcome.Paused, paused.Outcome);
        Assert.Equal(0, paused.NextIndex);
        Assert.Equal(ExecutionState.Paused, executor.State);

        // 恢复：让宿主的第二次执行直接成功
        host.BlockUntilCancelled = false;
        Task<ExecutionSummary>? resumed = executor.Resume();
        Assert.NotNull(resumed);

        ExecutionSummary summary = await resumed!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExecutionOutcome.Completed, summary.Outcome);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(2, host.Posted.Count);   // 第一次被暂停 + 恢复后重跑
    }

    [Fact]
    public async Task StallTimeout_AbortsAndFailsTask()
    {
        DateTimeOffset now = new(2026, 10, 27, 8, 0, 0, TimeSpan.FromHours(8));
        var host = new FakeHost { BlockUntilCancelled = true };
        using var executor = new QueueExecutor(
            host,
            new ExecutionOptions { StallTimeoutSeconds = 30, MaxRetries = 0 },
            () => now);

        Task<ExecutionSummary> running = executor.RunAsync([Item("a", "A")]);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(executor.CheckTimeouts());        // 未超时
        now = now.AddSeconds(31);
        Assert.True(executor.CheckTimeouts());         // 触发停滞

        ExecutionSummary summary = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExecutionOutcome.Failed, summary.Outcome);
        Assert.Contains("停滞超时", summary.Message);
        Assert.Equal(ExecutionState.Idle, executor.State);
    }

    [Fact]
    public async Task Activity_ResetsStallTimer()
    {
        DateTimeOffset now = new(2026, 10, 27, 8, 0, 0, TimeSpan.FromHours(8));
        var host = new FakeHost { BlockUntilCancelled = true };
        using var executor = new QueueExecutor(
            host,
            new ExecutionOptions { StallTimeoutSeconds = 30 },
            () => now);

        Task<ExecutionSummary> running = executor.RunAsync([Item("a", "A")]);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        now = now.AddSeconds(20);
        host.RaiseActivity();                          // 有进展 → 心跳重置
        now = now.AddSeconds(20);
        Assert.False(executor.CheckTimeouts());        // 距上次活动仅 20s，未到 30s

        now = now.AddSeconds(11);
        Assert.True(executor.CheckTimeouts());

        ExecutionSummary summary = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExecutionOutcome.Failed, summary.Outcome);
    }

    [Fact]
    public async Task MaxDuration_AbortsEvenWithActivity()
    {
        DateTimeOffset now = new(2026, 10, 27, 8, 0, 0, TimeSpan.FromHours(8));
        var host = new FakeHost { BlockUntilCancelled = true };
        using var executor = new QueueExecutor(
            host,
            new ExecutionOptions { StallTimeoutSeconds = 0, MaxDurationSeconds = 60 },
            () => now);

        Task<ExecutionSummary> running = executor.RunAsync([Item("a", "A")]);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        now = now.AddSeconds(30);
        host.RaiseActivity();
        Assert.False(executor.CheckTimeouts());

        now = now.AddSeconds(31);                      // 累计 61s
        host.RaiseActivity();                          // 一直有进展也算超时长
        Assert.True(executor.CheckTimeouts());

        ExecutionSummary summary = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExecutionOutcome.Failed, summary.Outcome);
        Assert.Contains("运行时长上限", summary.Message);
    }

    [Fact]
    public async Task RunAsync_ThrowsWhenHostNotReady()
    {
        var host = new FakeHost { IsReady = false };
        using var executor = new QueueExecutor(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.RunAsync([Item("a", "A")]));
    }

    [Fact]
    public async Task RunAsync_ThrowsWhenAlreadyRunning()
    {
        var host = new FakeHost { BlockUntilCancelled = true };
        using var executor = new QueueExecutor(host, new ExecutionOptions { StallTimeoutSeconds = 0 });

        Task<ExecutionSummary> running = executor.RunAsync([Item("a", "A")]);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.RunAsync([Item("b", "B")]));

        executor.Stop();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>可控假宿主：可按需「挂起直到取消」或返回成功/失败。</summary>
    private sealed class FakeHost : IExecutionHost
    {
        private int _startedCount;

        public bool IsReady { get; set; } = true;

        public bool BlockUntilCancelled { get; set; }

        public List<string> Posted { get; } = [];

        public Func<string, CancellationToken, Task<TaskRun>>? Handler { get; set; }

        public TaskCompletionSource Started { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler? Activity;

        public Task<TaskRun> RunTaskAsync(string entry, string? pipelineOverride, CancellationToken cancellationToken)
        {
            Posted.Add(entry);

            if (Interlocked.Increment(ref _startedCount) > 1)
            {
                Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            Started.TrySetResult();

            if (Handler is not null)
            {
                return Handler(entry, cancellationToken);
            }

            if (!BlockUntilCancelled)
            {
                return Task.FromResult(Succeed(entry));
            }

            return RunBlockingAsync(entry, cancellationToken);
        }

        public bool Stop() => true;

        public void RaiseActivity() => Activity?.Invoke(this, EventArgs.Empty);

        private static async Task<TaskRun> RunBlockingAsync(string entry, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 被中止：返回 Stopped，模拟 PostStop 后的结果
            }

            return StopRun(entry);
        }
    }
}
