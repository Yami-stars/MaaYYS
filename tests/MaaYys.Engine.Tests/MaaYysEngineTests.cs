using MaaYys.Engine.Session;
using Xunit;

// 原生运行库存在全局状态，集成测试串行执行
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MaaYys.Engine.Tests;

/// <summary>
/// 引擎会话集成测试：用空控制器（1x1 静态截图）+ TestTask（DirectHit + DoNothing）跑通
/// 连接 / 断开 / 资源加载 / 任务投递 全链路，同时验证状态机与事件流的实际接线。
/// 依赖 third_party/maafw 运行库与 test_resource（仓库内已提供）。
/// </summary>
public sealed class MaaYysEngineTests
{
    [Fact]
    public async Task Connect_ShouldSucceed_AndDisconnectShouldResetState()
    {
        using MaaYysEngine engine = MaaYysEngine.CreateNull();

        Assert.Equal(ControllerKind.Null, engine.Controller);
        Assert.False(engine.Connected);
        Assert.False(engine.Ready);

        EngineConnectResult result = await engine.ConnectAsync();

        Assert.True(result.Success, result.Error);
        Assert.True(engine.Connected);
        Assert.True(engine.Ready);
        Assert.NotNull(engine.Uuid);

        engine.Disconnect();

        Assert.False(engine.Ready);
        Assert.Empty(engine.Tasks.MarkStopped());
    }

    [Fact]
    public async Task LoadResource_And_RunTask_ShouldSucceed_WithStateEvents()
    {
        using MaaYysEngine engine = MaaYysEngine.CreateNull(marshalToSynchronizationContext: false);
        var states = new List<TaskState>();

        // 事件由框架回调线程 + 调用线程共同推送，收集必须加锁（List<T> 非线程安全）
        using IDisposable subscription = engine.TaskStateChanges.Subscribe(e =>
        {
            lock (states)
            {
                states.Add(e.Current);
            }
        });

        EngineConnectResult connect = await engine.ConnectAsync();
        Assert.True(connect.Success, connect.Error);

        bool loaded = await engine.LoadResourceAsync(TestPaths.TestResourceDir);
        Assert.True(loaded, "资源加载失败");
        Assert.True(engine.ResourceLoaded);

        TaskRun run = await engine.RunTaskAsync("TestTask");

        Assert.Equal(TaskState.Succeeded, run.State);
        Assert.True(run.TaskId > 0);
        Assert.Equal("TestTask", run.Entry);
        Assert.NotNull(run.Duration);

        // 状态链：Pending → Running → Succeeded（事件由框架回调线程推进）
        lock (states)
        {
            Assert.Contains(TaskState.Pending, states);
            Assert.Contains(TaskState.Running, states);
            Assert.Equal(TaskState.Succeeded, states[^1]);
        }
        Assert.Null(engine.Tasks.LastTransitionError);
    }

    [Fact]
    public async Task EngineEvents_ShouldDeliverNativeMessages()
    {
        using MaaYysEngine engine = MaaYysEngine.CreateNull(marshalToSynchronizationContext: false);
        var messages = new List<string>();

        using IDisposable subscription = engine.Events.Subscribe(e =>
        {
            if (e.Message is not null)
            {
                lock (messages)
                {
                    messages.Add(e.Message);
                }
            }
        });

        await engine.ConnectAsync();
        await engine.LoadResourceAsync(TestPaths.TestResourceDir);
        await engine.RunTaskAsync("TestTask");

        lock (messages)
        {
            Assert.Contains("Resource.Loading.Succeeded", messages);
            Assert.Contains("Tasker.Task.Succeeded", messages);
        }
    }

    [Fact]
    public async Task RunTask_WithoutConnect_ShouldThrow()
    {
        using MaaYysEngine engine = MaaYysEngine.CreateNull();

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunTaskAsync("TestTask"));
    }

    [Fact]
    public void Dispose_ThenUse_ShouldThrow()
    {
        MaaYysEngine engine = MaaYysEngine.CreateNull();
        engine.Dispose();

        Assert.Throws<ObjectDisposedException>(() => engine.Stop());
    }

    [Fact]
    public void CreateWin32_WithInvalidHandle_ShouldThrow()
        => Assert.Throws<ArgumentException>(() => MaaYysEngine.CreateWin32(IntPtr.Zero));
}
