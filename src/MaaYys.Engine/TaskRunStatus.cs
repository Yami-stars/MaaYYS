namespace MaaYys.Engine;

/// <summary>
/// 任务队列项的运行态。P5.1 用于 UI 状态着色；P5.3 执行引擎在运行时填充。
/// <para>纯枚举，不承载任何业务逻辑，可被 Engine / App 两层共享。</para>
/// </summary>
public enum TaskRunStatus
{
    /// <summary>未运行 / 初始。</summary>
    None = 0,

    /// <summary>等待执行（排在队列中但还没轮到）。</summary>
    Pending,

    /// <summary>正在运行。</summary>
    Running,

    /// <summary>成功完成。</summary>
    Done,

    /// <summary>被跳过（禁用或未满足前置条件）。</summary>
    Skipped,

    /// <summary>执行失败。</summary>
    Failed,

    /// <summary>被用户手动停止。</summary>
    Stopped,
}
