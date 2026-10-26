using MaaYys.Engine;
using MaaYys.Engine.Agent;
using MaaYys.Engine.Native;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// P4.1 agent 框架集成测试：拉起真实 Go agent 子进程，验证 17 动作 + 2 识别全部注册，
/// 且可用真实动作跑通一条 pipeline。依赖 agent/agent.exe 与 third_party/maafw。
/// </summary>
public class MaaAgentSessionTests
{
    private static readonly HashSet<string> ExpectedActions = new()
    {
        "RandomWait", "SwitchSoul", "ClearHitCount", "RepeatChallengeNTimes", "BonusToggleAction",
        "TimeCheck", "RandomSwipe", "ReStartGame", "HumanTouch", "ChallengeDungeonBoss", "BattleTeam",
        "RandomTouch", "RandomTask", "QuestionMatcher", "Guess", "AutoFoster", "AutoBattle",
    };

    private static readonly HashSet<string> ExpectedRecos = new()
    {
        "CoBattleTargetRecognition", "TaskCounterRecognition",
    };

    [Fact]
    public void AgentSession_RegistersAll19Capabilities()
    {
        using var resource = new MaaResource();
        using var session = MaaAgentSession.TryStart(resource);

        Assert.NotNull(session);
        Assert.True(session!.Connected);
        Assert.True(session.Alive);

        var actions = session.ActionNames.ToHashSet();
        var recos = session.RecognitionNames.ToHashSet();

        Assert.Equal(ExpectedActions.Count, actions.Count);
        Assert.Equal(ExpectedRecos.Count, recos.Count);
        Assert.Subset(ExpectedActions, actions);
        Assert.Subset(ExpectedRecos, recos);
    }

    [Fact]
    public void AgentSession_ExecutesRealActionViaPipeline()
    {
        using var resource = new MaaResource();
        using var session = MaaAgentSession.TryStart(resource);
        Assert.NotNull(session);

        // 用真实动作 RandomWait（纯逻辑、无需设备）跑一条 pipeline，确认 agent 侧确实执行。
        using var controller = MaaNullController.Create();
        using var tasker = new MaaTasker();
        Assert.True(tasker.BindController(controller.Handle));
        Assert.True(tasker.BindResource(resource));
        Assert.True(tasker.Inited);

        const string entry = "AgentExecProbe";
        string pipeline = $$"""
        {
          "{{entry}}": {
            "action": {
              "type": "Custom",
              "param": {
                "custom_action": "RandomWait",
                "custom_action_param": { "min": 1, "max": 1 }
              }
            }
          }
        }
        """;

        long taskId = tasker.PostTask(entry, pipeline);
        Assert.NotEqual(0L, taskId);
        Assert.Equal(MaaStatus.Succeeded, tasker.Wait(taskId));
    }

    [Fact]
    public void AgentSession_Dispose_KillsChildProcess()
    {
        using var resource = new MaaResource();
        var session = MaaAgentSession.TryStart(resource);
        Assert.NotNull(session);
        int port = session!.Port;

        session.Dispose();

        // 端口应被释放（子进程已退出），新 listener 能绑定同一端口。
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }
}
