using System.Text.Json;
using MaaYys.Engine.Native;

namespace MaaYys.Engine.Session;

/// <summary>
/// 任务状态（引擎层状态机，独立于 MaaStatus，额外区分「已投递未开始」与「被停止」）。
/// 合法迁移：
///   Idle → Pending
///   Pending → Running | Succeeded | Failed | Stopped
///   Running → Succeeded | Failed | Stopped
/// 终态（Succeeded / Failed / Stopped）不可再变更。
/// </summary>
public enum TaskState
{
    Idle = 0,
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Stopped = 5,
}

/// <summary>一次任务运行的完整记录（供 GUI 任务链、日志面板展示）。</summary>
public sealed class TaskRun
{
    internal TaskRun(long taskId, string entry, string? pipelineOverride)
    {
        TaskId = taskId;
        Entry = entry;
        PipelineOverride = pipelineOverride;
        CreatedAt = DateTimeOffset.Now;
    }

    public long TaskId { get; }

    public string Entry { get; }

    public string? PipelineOverride { get; }

    public TaskState State { get; internal set; } = TaskState.Idle;

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? StartedAt { get; internal set; }

    public DateTimeOffset? EndedAt { get; internal set; }

    public string? FailureMessage { get; internal set; }

    /// <summary>是否为终态（不可再变更）。</summary>
    public bool IsTerminal => State is TaskState.Succeeded or TaskState.Failed or TaskState.Stopped;

    /// <summary>运行时长（未结束返回 null）。</summary>
    public TimeSpan? Duration => StartedAt is null || EndedAt is null ? null : EndedAt - StartedAt;

    internal static TaskRun Rejected(string entry, string reason)
    {
        var run = new TaskRun(0, entry, null) { State = TaskState.Failed, EndedAt = DateTimeOffset.Now };
        run.FailureMessage = reason;
        return run;
    }

    public override string ToString() => $"#{TaskId} {Entry} → {State}{(FailureMessage is null ? string.Empty : $" ({FailureMessage})")}";
}

/// <summary>
/// 任务状态变更事件参数。
/// Current 为**快照**而非实时读取——事件可能被异步派发（UI 线程），
/// 若实时读取 Run.State，回调执行时任务可能已推进到下一状态，导致订阅端漏掉中间态。
/// </summary>
public sealed class TaskStateChangedEventArgs(TaskRun run, TaskState previous, TaskState current, string? nativeMessage) : EventArgs
{
    public TaskRun Run { get; } = run;

    public TaskState Previous { get; } = previous;

    public TaskState Current { get; } = current;

    /// <summary>触发本次变更的原生消息名（如 Tasker.Task.Succeeded）；空表示由引擎主动设置。</summary>
    public string? NativeMessage { get; } = nativeMessage;
}

/// <summary>
/// 任务状态机：把 MaaFramework 的 Tasker.Task.* 事件推进为可控状态，并记录非法迁移。
/// 线程安全（框架回调线程 + 调用线程并发）。事件在锁外触发，订阅者可安全回调引擎。
/// </summary>
public sealed class TaskStateMachine
{
    private static readonly Dictionary<TaskState, TaskState[]> AllowedTransitions = new()
    {
        [TaskState.Idle] = [TaskState.Pending],
        [TaskState.Pending] = [TaskState.Running, TaskState.Succeeded, TaskState.Failed, TaskState.Stopped],
        [TaskState.Running] = [TaskState.Succeeded, TaskState.Failed, TaskState.Stopped],
        [TaskState.Succeeded] = [],
        [TaskState.Failed] = [],
        [TaskState.Stopped] = [],
    };

    private readonly object _gate = new();
    private readonly Dictionary<long, TaskRun> _runs = new();

    /// <summary>状态变更通知（在触发线程同步调用，UI 侧需自行切线程或由引擎统一派发）。</summary>
    public event EventHandler<TaskStateChangedEventArgs>? StateChanged;

    /// <summary>最近一次非法迁移描述（正常流程应为 null，可用于自检/日志）。</summary>
    public string? LastTransitionError { get; private set; }

    /// <summary>登记新任务（投递成功后状态即为 Pending），随后由事件推进。</summary>
    public TaskRun Start(long taskId, string entry, string? pipelineOverride = null)
    {
        var run = new TaskRun(taskId, entry, pipelineOverride);
        lock (_gate)
        {
            _runs[taskId] = run;
        }

        Transition(run, TaskState.Pending, "engine:posted");
        return run;
    }

    public TaskRun? Get(long taskId)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(taskId, out TaskRun? run) ? run : null;
        }
    }

    /// <summary>当前所有任务记录快照（按投递顺序）。</summary>
    public IReadOnlyList<TaskRun> Runs
    {
        get
        {
            lock (_gate)
            {
                return _runs.Values.OrderBy(r => r.TaskId).ToList();
            }
        }
    }

    /// <summary>
    /// 按原生事件推进状态。仅处理 Tasker.Task.*；其它消息（Node.* 等）返回 false。
    /// </summary>
    public bool ApplyEvent(string message, string? detailsJson)
    {
        TaskState? target = message switch
        {
            "Tasker.Task.Starting" or "Tasker.Task.Started" => TaskState.Running,
            "Tasker.Task.Succeeded" => TaskState.Succeeded,
            "Tasker.Task.Failed" => TaskState.Failed,
            _ => null,
        };

        if (target is null)
        {
            return false;
        }

        long taskId = TryReadTaskId(detailsJson);
        if (taskId == 0)
        {
            LastTransitionError = $"事件 {message} 缺少 task_id，已忽略";
            return false;
        }

        TaskRun? run;
        lock (_gate)
        {
            _runs.TryGetValue(taskId, out run);
        }

        if (run is null)
        {
            LastTransitionError = $"事件 {message} 指向未登记的 task_id={taskId}，已忽略";
            return false;
        }

        return Transition(run, target.Value, message);
    }

    /// <summary>将所有未终态任务置为 Stopped（用户点击停止 / 取消运行时调用）。</summary>
    public IReadOnlyList<TaskRun> MarkStopped(string? reason = null)
    {
        List<TaskRun> changed = [];
        lock (_gate)
        {
            foreach (TaskRun run in _runs.Values)
            {
                if (!run.IsTerminal)
                {
                    changed.Add(run);
                }
            }
        }

        foreach (TaskRun run in changed)
        {
            run.FailureMessage ??= reason;
            Transition(run, TaskState.Stopped, null);
        }

        return changed;
    }

    /// <summary>
    /// 兜底：若任务仍非终态（例如事件未到达），按 MaaTasker.Wait 的最终状态补齐。
    /// </summary>
    public TaskRun EnsureFinal(TaskRun run, MaaStatus status, string? failureMessage = null)
    {
        if (run.IsTerminal)
        {
            return run;
        }

        TaskState target = status switch
        {
            MaaStatus.Succeeded => TaskState.Succeeded,
            MaaStatus.Failed => TaskState.Failed,
            MaaStatus.Running => TaskState.Running,
            MaaStatus.Pending => TaskState.Pending,
            _ => TaskState.Failed,
        };

        if (target == TaskState.Failed && failureMessage is not null)
        {
            run.FailureMessage ??= failureMessage;
        }

        if (target != run.State)
        {
            Transition(run, target, null);
        }

        return run;
    }

    private bool Transition(TaskRun run, TaskState target, string? nativeMessage)
    {
        TaskState previous;
        lock (_gate)
        {
            previous = run.State;
            if (previous == target)
            {
                return true;
            }

            if (!AllowedTransitions.TryGetValue(previous, out TaskState[]? allowed) || !allowed.Contains(target))
            {
                LastTransitionError = $"非法状态迁移 {previous} → {target}（task #{run.TaskId}，事件 {nativeMessage ?? "engine"}）";
                return false;
            }

            run.State = target;
            if (target == TaskState.Running && run.StartedAt is null)
            {
                run.StartedAt = DateTimeOffset.Now;
            }

            if (run.IsTerminal)
            {
                run.EndedAt = DateTimeOffset.Now;
            }
        }

        StateChanged?.Invoke(this, new TaskStateChangedEventArgs(run, previous, target, nativeMessage));
        return true;
    }

    /// <summary>从 details_json 读取 task_id（缺失或非法返回 0）。</summary>
    private static long TryReadTaskId(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return 0;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            foreach (string key in (string[])["task_id", "taskId", "id"])
            {
                if (doc.RootElement.TryGetProperty(key, out JsonElement value))
                {
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
                    {
                        return number;
                    }

                    if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out long parsed))
                    {
                        return parsed;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 细节 JSON 异常不应影响状态推进，交由上层日志处理
        }

        return 0;
    }
}
