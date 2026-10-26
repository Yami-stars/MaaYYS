using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MaaYys.Engine;
using MaaYys.Engine.Agent;
using MaaYys.Engine.Native;

namespace MaaYys.P1Probe;

/// <summary>
/// P4 Agent 链路验证：C# 侧作为 AgentClient，拉起 Go agent 子进程（MaaAgentServer），
/// 完成握手后校验其注册的 17 个自定义动作 + 2 个自定义识别，并用真实动作跑通一条 pipeline。
///
/// 用法：
///   agent [--port 7788] [--agent &lt;agent.exe 路径&gt;] [--libdir &lt;maafw 目录&gt;]
/// </summary>
internal static class ProbeAgent
{
    private const string DefaultEntry = "ProbeAgentTask";

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

    public static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        Console.WriteLine("=== P4 Agent 链路验证 ===");

        int port = 7788;
        string? agentPath = null;
        string? libDir = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    port = int.Parse(args[++i]);
                    break;
                case "--agent" when i + 1 < args.Length:
                    agentPath = args[++i];
                    break;
                case "--libdir" when i + 1 < args.Length:
                    libDir = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"未知或不完整参数: {args[i]}");
                    return 2;
            }
        }

        string repoRoot = FindRepoRoot();
        Console.WriteLine($"[1] 仓库根目录: {repoRoot}");

        agentPath ??= Path.Combine(repoRoot, "agent", "agent.exe");
        if (!File.Exists(agentPath))
        {
            Console.Error.WriteLine($"    未找到 agent 可执行文件: {agentPath}");
            Console.Error.WriteLine("    请先构建：go build -o agent.exe ./agent（需要 Go 1.25+）");
            return 1;
        }

        libDir ??= Path.Combine(repoRoot, "third_party", "maafw");
        Console.WriteLine($"    agent: {agentPath}");
        Console.WriteLine($"    libdir: {libDir}");

        port = FindFreePort(port);
        Console.WriteLine($"[2] 创建 AgentClient（TCP 127.0.0.1:{port}）:");

        using var resource = new MaaResource();
        var session = MaaAgentSession.TryStart(
            resource,
            new AgentStartOptions { AgentExec = agentPath, LibDir = libDir, BasePort = port });
        if (session is null)
        {
            Console.Error.WriteLine("    agent 会话启动失败（TryStart 返回 null）");
            return 1;
        }

        using (session)
        {
            Console.WriteLine($"    Connected={session.Connected} Alive={session.Alive} Port={session.Port}");

            string[] actions = session.ActionNames;
            string[] recognitions = session.RecognitionNames;
            Console.WriteLine($"[3] 自定义动作 ({actions.Length}): [{string.Join(", ", actions)}]");
            Console.WriteLine($"    自定义识别 ({recognitions.Length}): [{string.Join(", ", recognitions)}]");

            bool actionsOk = ExpectedActions.SetEquals(actions);
            bool recosOk = ExpectedRecos.SetEquals(recognitions);
            Console.WriteLine($"    动作注册校验: {(actionsOk ? "通过" : "失败")}");
            Console.WriteLine($"    识别注册校验: {(recosOk ? "通过" : "失败")}");
            if (!actionsOk)
            {
                Console.Error.WriteLine($"    缺失动作: {string.Join(", ", ExpectedActions.Except(actions))}");
                Console.Error.WriteLine($"    多余动作: {string.Join(", ", actions.Except(ExpectedActions))}");
            }
            if (!recosOk)
            {
                Console.Error.WriteLine($"    缺失识别: {string.Join(", ", ExpectedRecos.Except(recognitions))}");
            }
            if (!actionsOk || !recosOk)
            {
                return 1;
            }

            // [4] 用真实动作 RandomWait 跑一条 pipeline，确认 agent 侧真的执行了动作。
            // 注意：tasker 必须绑定与 agent 会话【同一个】 resource，否则看不到 agent 注册的能力。
            Console.WriteLine("[4] 投递引用 RandomWait 的 pipeline（验证动作执行）:");
            string pipeline = BuildProbePipeline();
            Console.WriteLine($"    pipeline_override: {pipeline}");

            using var controller = MaaNullController.Create();
            using var tasker = new MaaTasker();
            if (!tasker.BindController(controller.Handle) ||
                !tasker.BindResource(resource) ||
                !tasker.Inited)
            {
                Console.Error.WriteLine("    Tasker 初始化失败");
                return 1;
            }

            long taskId = tasker.PostTask(DefaultEntry, pipeline);
            if (taskId == 0)
            {
                Console.Error.WriteLine("    PostTask 被拒（返回 0）");
                return 1;
            }

            MaaStatus status = tasker.Wait(taskId);
            Console.WriteLine($"    PostTask -> id={taskId} 状态={status}");
            if (status != MaaStatus.Succeeded)
            {
                Console.Error.WriteLine("    任务未成功（RandomWait 可能未被执行）");
                return 1;
            }
        }

        Console.WriteLine("=== P4 验证完成：agent 链路闭环（注册 + 执行）通过 ===");
        return 0;
    }

    /// <summary>构造引用真实自定义动作 RandomWait 的 pipeline。</summary>
    private static string BuildProbePipeline()
    {
        var pipeline = new Dictionary<string, object>
        {
            [DefaultEntry] = new Dictionary<string, object>
            {
                ["action"] = new Dictionary<string, object>
                {
                    ["type"] = "Custom",
                    ["param"] = new Dictionary<string, object>
                    {
                        ["custom_action"] = "RandomWait",
                        ["custom_action_param"] = new Dictionary<string, object> { ["min"] = 1, ["max"] = 2 },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(pipeline);
    }

    private static int FindFreePort(int startPort)
    {
        for (int p = startPort; p < startPort + 50; p++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, p);
                listener.Start();
                listener.Stop();
                return p;
            }
            catch (SocketException)
            {
                // 端口占用，试下一个
            }
        }

        throw new InvalidOperationException($"未找到空闲端口（{startPort}..{startPort + 49}）");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "agent")) &&
                Directory.Exists(Path.Combine(dir.FullName, "third_party", "maafw")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("未能定位仓库根目录（未找到 agent/ 与 third_party/maafw/）");
    }
}
