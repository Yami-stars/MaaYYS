using MaaYys.Engine;
using MaaYys.Engine.Execution;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// P5.3 集成验证：真引擎（Null 控制器 + test_resource 干跑包）→ <see cref="MaaEngineExecutionHost"/>
/// → <see cref="QueueExecutor"/> 全链路，确认宿主适配与状态回写没问题。
/// </summary>
public sealed class ExecutionIntegrationTests
{
    [Fact]
    public async Task RealEngine_RunsQueueAndReportsDone()
    {
        using MaaYysEngine engine = MaaYysEngine.CreateNull(marshalToSynchronizationContext: false);
        EngineConnectResult connect = await engine.ConnectAsync();
        Assert.True(connect.Success, connect.Error);
        Assert.True(await engine.LoadResourceAsync(TestPaths.TestResourceDir), "资源加载失败");
        Assert.True(engine.Ready);

        using var host = new MaaEngineExecutionHost(engine);
        using var executor = new QueueExecutor(host, new ExecutionOptions { StallTimeoutSeconds = 30 });

        List<(string Id, TaskRunStatus Status)> log = [];
        executor.ItemStatusChanged += (_, e) =>
        {
            lock (log)
            {
                log.Add((e.Item.Id, e.Status));
            }
        };

        ExecutionSummary summary = await executor.RunAsync(
        [
            new ExecutionItem("a", "TestTask", null, "测试任务 A"),
            new ExecutionItem("b", "TestTask", null, "测试任务 B"),
        ]);

        Assert.Equal(ExecutionOutcome.Completed, summary.Outcome);
        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(ExecutionState.Idle, executor.State);

        lock (log)
        {
            Assert.Equal(2, log.Count(x => x.Status == TaskRunStatus.Running));
            Assert.Equal(2, log.Count(x => x.Status == TaskRunStatus.Done));
        }

        // 每项都有独立的引擎任务记录（真实投递，不同 task_id）
        Assert.Equal(2, summary.Results.Count);
        Assert.All(summary.Results, r => Assert.Equal(TaskRunStatus.Done, r.Status));
    }

    [Fact]
    public async Task RealEngine_UnknownEntryFailsWithoutHanging()
    {
        using MaaYysEngine engine = MaaYysEngine.CreateNull(marshalToSynchronizationContext: false);
        await engine.ConnectAsync();
        await engine.LoadResourceAsync(TestPaths.TestResourceDir);

        using var host = new MaaEngineExecutionHost(engine);
        using var executor = new QueueExecutor(
            host, new ExecutionOptions { StallTimeoutSeconds = 30, MaxRetries = 0 });

        ExecutionSummary summary = await executor
            .RunAsync([new ExecutionItem("x", "_不存在的入口_", null, "坏任务")])
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(ExecutionOutcome.Failed, summary.Outcome);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(TaskRunStatus.Failed, summary.Results[0].Status);
    }
}
