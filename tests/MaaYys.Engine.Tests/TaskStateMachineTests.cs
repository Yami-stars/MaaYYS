using MaaYys.Engine.Native;
using MaaYys.Engine.Session;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>任务状态机纯逻辑测试（不依赖原生运行库）。</summary>
public sealed class TaskStateMachineTests
{
    [Fact]
    public void Start_ShouldEnterPending_AndRaiseEvent()
    {
        var machine = new TaskStateMachine();
        var transitions = new List<(TaskState From, TaskState To)>();
        machine.StateChanged += (_, e) => transitions.Add((e.Previous, e.Current));

        TaskRun run = machine.Start(1, "TestTask");

        Assert.Equal(TaskState.Pending, run.State);
        Assert.False(run.IsTerminal);
        Assert.Single(transitions);
        Assert.Equal((TaskState.Idle, TaskState.Pending), transitions[0]);
    }

    [Fact]
    public void TaskEvents_ShouldAdvanceToSucceeded()
    {
        var machine = new TaskStateMachine();
        TaskRun run = machine.Start(7, "TestTask");

        Assert.True(machine.ApplyEvent("Tasker.Task.Starting", """{"task_id":7}"""));
        Assert.Equal(TaskState.Running, run.State);
        Assert.NotNull(run.StartedAt);

        Assert.True(machine.ApplyEvent("Tasker.Task.Succeeded", """{"task_id":7}"""));
        Assert.Equal(TaskState.Succeeded, run.State);
        Assert.True(run.IsTerminal);
        Assert.NotNull(run.EndedAt);
        Assert.NotNull(run.Duration);
        Assert.Null(machine.LastTransitionError);
    }

    [Fact]
    public void FailedEvent_ShouldMarkFailed()
    {
        var machine = new TaskStateMachine();
        TaskRun run = machine.Start(3, "Foo");
        machine.ApplyEvent("Tasker.Task.Starting", """{"task_id":3}""");

        Assert.True(machine.ApplyEvent("Tasker.Task.Failed", """{"task_id":"3"}"""));
        Assert.Equal(TaskState.Failed, run.State);
        Assert.True(run.IsTerminal);
    }

    [Fact]
    public void TerminalState_ShouldRejectFurtherTransitions()
    {
        var machine = new TaskStateMachine();
        TaskRun run = machine.Start(4, "Foo");
        machine.ApplyEvent("Tasker.Task.Starting", """{"task_id":4}""");
        machine.ApplyEvent("Tasker.Task.Succeeded", """{"task_id":4}""");

        bool applied = machine.ApplyEvent("Tasker.Task.Failed", """{"task_id":4}""");

        Assert.False(applied);
        Assert.Equal(TaskState.Succeeded, run.State);
        Assert.Contains("非法状态迁移", machine.LastTransitionError);
    }

    [Fact]
    public void UnknownTaskId_ShouldBeIgnored()
    {
        var machine = new TaskStateMachine();

        Assert.False(machine.ApplyEvent("Tasker.Task.Succeeded", """{"task_id":999}"""));
        Assert.Contains("未登记", machine.LastTransitionError);
    }

    [Fact]
    public void NonTaskerMessage_ShouldBeIgnored()
    {
        var machine = new TaskStateMachine();
        Assert.False(machine.ApplyEvent("Node.PipelineNode.Starting", """{"task_id":1}"""));
        Assert.Null(machine.LastTransitionError);
    }

    [Fact]
    public void MarkStopped_ShouldStopAllNonTerminalRuns()
    {
        var machine = new TaskStateMachine();
        TaskRun running = machine.Start(1, "A");
        TaskRun finished = machine.Start(2, "B");
        machine.ApplyEvent("Tasker.Task.Starting", """{"task_id":1}""");
        machine.ApplyEvent("Tasker.Task.Starting", """{"task_id":2}""");
        machine.ApplyEvent("Tasker.Task.Succeeded", """{"task_id":2}""");

        IReadOnlyList<TaskRun> stopped = machine.MarkStopped("用户停止");

        Assert.Single(stopped);
        Assert.Equal(TaskState.Stopped, running.State);
        Assert.Equal(TaskState.Succeeded, finished.State);
        Assert.Equal("用户停止", running.FailureMessage);
    }

    [Fact]
    public void EnsureFinal_ShouldFillStateWhenEventMissing()
    {
        var machine = new TaskStateMachine();
        TaskRun run = machine.Start(5, "Foo");

        machine.EnsureFinal(run, MaaStatus.Succeeded);

        Assert.Equal(TaskState.Succeeded, run.State);
    }

    [Fact]
    public void EnsureFinal_ShouldNotOverrideTerminalState()
    {
        var machine = new TaskStateMachine();
        TaskRun run = machine.Start(6, "Foo");
        machine.ApplyEvent("Tasker.Task.Succeeded", """{"task_id":6}""");

        machine.EnsureFinal(run, MaaStatus.Failed, "不该覆盖");

        Assert.Equal(TaskState.Succeeded, run.State);
        Assert.Null(run.FailureMessage);
    }

    [Fact]
    public void Runs_ShouldBeOrderedByTaskId()
    {
        var machine = new TaskStateMachine();
        machine.Start(10, "A");
        machine.Start(2, "B");

        IReadOnlyList<TaskRun> runs = machine.Runs;

        Assert.Equal(new long[] { 2, 10 }, runs.Select(r => r.TaskId));
    }
}
