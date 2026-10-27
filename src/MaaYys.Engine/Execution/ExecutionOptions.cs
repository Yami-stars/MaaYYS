namespace MaaYys.Engine.Execution;

/// <summary>
/// 任务执行引擎策略（对标 MAA 的 StallTimeout / 重试 / 时限）。
/// <para>所有时限单位均为「秒」；<c>0</c> 表示关闭该策略。</para>
/// </summary>
public sealed class ExecutionOptions
{
    /// <summary>
    /// 停滞超时（对标 MAA StallTimeout）：超过该时长没有任何框架事件（节点无进展）即判定卡死。
    /// 默认 60 秒；0 = 不检测。
    /// </summary>
    public int StallTimeoutSeconds { get; init; } = 60;

    /// <summary>单个任务的运行时长上限（含重试）；0 = 不限。</summary>
    public int MaxDurationSeconds { get; init; }

    /// <summary>失败后的重试次数（不含首次执行）。</summary>
    public int MaxRetries { get; init; }

    /// <summary>两次尝试之间的等待毫秒数。</summary>
    public int RetryDelayMs { get; init; } = 1000;

    /// <summary>任务最终失败后是否停止整个队列（MAA 默认停止）；false = 继续跑后续任务。</summary>
    public bool StopOnFailure { get; init; } = true;

    /// <summary>默认策略：60s 停滞超时、不重试、失败即停、不限时长。</summary>
    public static ExecutionOptions Default { get; } = new();

    internal ExecutionOptions Sanitized() => new()
    {
        StallTimeoutSeconds = Math.Max(0, StallTimeoutSeconds),
        MaxDurationSeconds = Math.Max(0, MaxDurationSeconds),
        MaxRetries = Math.Max(0, MaxRetries),
        RetryDelayMs = Math.Max(0, RetryDelayMs),
        StopOnFailure = StopOnFailure,
    };
}

/// <summary>执行器状态。</summary>
public enum ExecutionState
{
    /// <summary>空闲：未运行或已结束。</summary>
    Idle = 0,

    /// <summary>正在按队列顺序执行。</summary>
    Running,

    /// <summary>已暂停：当前任务被中止，下次从该任务重新开始。</summary>
    Paused,

    /// <summary>停止中：已发出停止请求，等待当前任务收尾。</summary>
    Stopping,
}

/// <summary>一次队列执行的整体结果。</summary>
public enum ExecutionOutcome
{
    /// <summary>队列跑完（含个别任务失败但配置为继续）。</summary>
    Completed = 0,

    /// <summary>用户停止。</summary>
    Stopped,

    /// <summary>用户暂停（可从 <see cref="ExecutionSummary.NextIndex"/> 继续）。</summary>
    Paused,

    /// <summary>某任务失败且策略为「失败即停」。</summary>
    Failed,
}

/// <summary>
/// 队列中的一个执行项：已编译好 <c>pipeline_override</c>，执行器只管投递。
/// <paramref name="Id"/> 为队列实例唯一 Id（用于回写运行态）。
/// </summary>
public sealed record ExecutionItem(
    string Id,
    string Entry,
    string? PipelineOverride,
    string DisplayName,
    bool Enabled = true);

/// <summary>单个执行项的结果明细（供日志面板 P5.5 汇总）。</summary>
public sealed record ExecutionItemResult(
    string Id,
    string DisplayName,
    TaskRunStatus Status,
    string? Message,
    TimeSpan? Duration);

/// <summary>队列执行汇总。</summary>
public sealed record ExecutionSummary(
    ExecutionOutcome Outcome,
    int Succeeded,
    int Failed,
    int Skipped,
    int NextIndex,
    string? Message,
    IReadOnlyList<ExecutionItemResult> Results)
{
    /// <summary>是否正常跑完（不含失败/暂停/停止）。</summary>
    public bool IsCompleted => Outcome == ExecutionOutcome.Completed;
}

/// <summary>某队列项运行态变化（UI 据此着色）。</summary>
public sealed class QueueItemStatusChangedEventArgs(
    ExecutionItem item,
    int index,
    TaskRunStatus status,
    string? message,
    Session.TaskRun? run) : EventArgs
{
    public ExecutionItem Item { get; } = item;

    /// <summary>在队列中的下标。</summary>
    public int Index { get; } = index;

    public TaskRunStatus Status { get; } = status;

    public string? Message { get; } = message;

    /// <summary>引擎侧的任务运行记录（Skipped/Pending 阶段为 null）。</summary>
    public Session.TaskRun? Run { get; } = run;
}
