using MaaYys.Engine.Session;

namespace MaaYys.Engine.Execution;

/// <summary>
/// 执行宿主抽象：把「投递一个 pipeline 入口并等它结束」这件事与具体引擎解耦，
/// 便于 <see cref="QueueExecutor"/> 在没有 MaaFramework 的环境下单测。
/// </summary>
public interface IExecutionHost
{
    /// <summary>宿主是否就绪（引擎已连接且资源已绑定）。</summary>
    bool IsReady { get; }

    /// <summary>任一框架事件（含节点级事件），用于停滞超时的心跳。</summary>
    event EventHandler? Activity;

    /// <summary>投递任务并等待结束。取消令牌触发时应请求停止而非硬杀。</summary>
    Task<TaskRun> RunTaskAsync(string entry, string? pipelineOverride, CancellationToken cancellationToken);

    /// <summary>请求停止当前任务（不等待）。</summary>
    bool Stop();
}
