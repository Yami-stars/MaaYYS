using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MaaYss.Engine;
using MaaYss.Engine.Native;

namespace MaaYss.P1Probe;

/// <summary>
/// P1.5 Agent 链路验证：C# 侧作为 AgentClient，拉起 Go agent 子进程（MaaAgentServer），
/// 完成握手后通过 pipeline 调用 agent 注册的 custom_action 与 custom_recognition。
///
/// 流程表验收标准：日志出现 agent 注册成功 + action 被调用。
///
/// 用法：
///   agent [--port 7788] [--agent <agent.exe 路径>] [--libdir <maafw 目录>]
/// </summary>
internal static class ProbeAgent
{
    private const string DefaultEntry = "ProbeAgentTask";
    private const string ExpectedAction = "MaaYssProbeAction";
    private const string ExpectedRecognition = "MaaYssProbeRecognition";

    public static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        Console.WriteLine("=== P1.5 Agent 链路验证 ===");

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

        // ---- [1] 定位 agent 可执行文件与运行库目录 ----
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

        Process? child = null;
        MaaAgentClient? client = null;

        // Resource 手动管理生命周期：必须在 client.Disconnect 之后销毁，
        // 否则 client 内部持有已释放的 resource 指针，Disconnect 会访问非法内存（0xC0000005）。
        MaaResource? resource = null;

        try
        {
            // ---- [2] 创建 AgentClient（TCP 模式：本进程监听，agent 连入）----
            port = FindFreePort(port);
            Console.WriteLine($"[2] 创建 AgentClient（TCP 127.0.0.1:{port}）:");
            client = MaaAgentClient.CreateTcp((ushort)port);

            string? identifier = client.Identifier();
            if (string.IsNullOrEmpty(identifier))
            {
                Console.Error.WriteLine("    Identifier() 返回空");
                return 1;
            }

            Console.WriteLine($"    identifier = {identifier}");

            // ---- [3] 拉起 Go agent 子进程 ----
            Console.WriteLine("[3] 启动 agent 子进程:");
            child = StartAgent(agentPath, identifier, libDir);
            Console.WriteLine($"    pid={child.Id} | args: {identifier} --libdir {libDir}");

            // ---- [4] 绑定资源并握手（BindResource 必须先于 Connect，否则框架报 resource is not bound）----
            // 注意：BindResource 把 agent 注册的 custom action/recognition 挂到本地 Resource，
            // 必须在 Connect 之前完成，否则框架拒绝握手。
            Console.WriteLine("[4] 绑定资源并与 agent 握手（连接超时 20s）:");
            resource = new MaaResource();
            if (!client.BindResource(resource))
            {
                Console.Error.WriteLine("    BindResource 失败");
                return 1;
            }

            Console.WriteLine("    BindResource -> 成功");

            client.SetTimeout(20_000);
            if (!client.Connect())
            {
                Console.Error.WriteLine("    Connect 失败（agent 未连入或超时）");
                return 1;
            }

            Console.WriteLine($"    Connected={client.Connected} Alive={client.Alive}");

            // ---- [5] 读取 agent 注册的能力清单 ----
            string[] actions = client.CustomActionList();
            string[] recognitions = client.CustomRecognitionList();
            Console.WriteLine($"[5] agent 注册的 custom_action: [{string.Join(", ", actions)}]");
            Console.WriteLine($"    agent 注册的 custom_recognition: [{string.Join(", ", recognitions)}]");

            bool registered = actions.Contains(ExpectedAction) && recognitions.Contains(ExpectedRecognition);
            Console.WriteLine($"    注册校验: {(registered ? "通过" : "失败")}");
            if (!registered)
            {
                return 1;
            }

            // ---- [6] 绑定控制器到 Tasker，投递调用 agent 的 pipeline ----
            Console.WriteLine("[6] 绑定控制器，投递任务:");

            using MaaNullController controller = MaaNullController.Create();
            using MaaTasker tasker = new();
            tasker.EventReceived += (_, e) =>
            {
                if (e.Message is not null && e.Message.StartsWith("Node.", StringComparison.Ordinal))
                {
                    Console.WriteLine($"      事件: {e.Message}");
                }
            };

            if (!tasker.BindController(controller.Handle))
            {
                Console.Error.WriteLine("    BindController 失败");
                return 1;
            }

            if (!tasker.BindResource(resource) || !tasker.Inited)
            {
                Console.Error.WriteLine("    BindResource/Inited 失败");
                return 1;
            }

            Console.WriteLine("    Tasker 初始化完成");

            string pipeline = BuildProbePipeline();
            Console.WriteLine($"    pipeline_override: {pipeline}");

            long taskId = tasker.PostTask(DefaultEntry, pipeline);
            if (taskId == 0)
            {
                Console.Error.WriteLine("    PostTask 被拒（返回 0）");
                return 1;
            }

            MaaStatus status = tasker.Wait(taskId);
            Console.WriteLine($"[7] PostTask -> id={taskId} 状态={status}");
            if (status != MaaStatus.Succeeded)
            {
                Console.Error.WriteLine("    任务未成功（agent action 可能未被调用）");
                return 1;
            }

            // ---- [8] 校验 agent 侧确实执行了 action ----
            Console.WriteLine("[8] 校验 agent 侧的调用痕迹:");
            string hitFile = Path.Combine(Path.GetDirectoryName(agentPath) ?? ".", "agent-action-hit.txt");
            if (File.Exists(hitFile))
            {
                Console.WriteLine($"    {hitFile}:");
                foreach (string line in File.ReadAllLines(hitFile))
                {
                    Console.WriteLine($"      {line}");
                }

                File.Delete(hitFile);
                Console.WriteLine("    ✓ agent 的 custom_action 确实被执行");
            }
            else
            {
                Console.Error.WriteLine($"    未找到调用痕迹文件 {hitFile}");
                return 1;
            }

            Console.WriteLine("=== P1.5 验证完成 ===");
            return 0;
        }
        finally
        {
            // 清理顺序：先断开（让 agent Join 返回）→ 销毁 client → 再销毁 resource → 收子进程。
            try
            {
                client?.Disconnect();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Disconnect 异常（忽略）: {ex.Message}");
            }

            client?.Dispose();
            resource?.Dispose();
            StopAgent(child);
        }
    }

    /// <summary>构造引用 agent 能力的 pipeline：先 custom recognition，再 custom action。</summary>
    private static string BuildProbePipeline()
    {
        var pipeline = new Dictionary<string, object>
        {
            [DefaultEntry] = new Dictionary<string, object>
            {
                ["recognition"] = new Dictionary<string, object>
                {
                    ["type"] = "Custom",
                    ["param"] = new Dictionary<string, object>
                    {
                        ["custom_recognition"] = ExpectedRecognition,
                        ["custom_recognition_param"] = new Dictionary<string, object> { ["probe"] = "p15" },
                    },
                },
                ["action"] = new Dictionary<string, object>
                {
                    ["type"] = "Custom",
                    ["param"] = new Dictionary<string, object>
                    {
                        ["custom_action"] = ExpectedAction,
                        ["custom_action_param"] = new Dictionary<string, object> { ["hello"] = "maayss" },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(pipeline);
    }

    private static Process StartAgent(string agentPath, string identifier, string libDir)
    {
        var psi = new ProcessStartInfo(agentPath)
        {
            WorkingDirectory = Path.GetDirectoryName(agentPath) ?? ".",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(identifier);
        psi.ArgumentList.Add("--libdir");
        psi.ArgumentList.Add(libDir);

        Process child = Process.Start(psi) ?? throw new InvalidOperationException("agent 子进程启动失败");

        // 实时转发 agent 日志（带前缀，便于在探针输出中区分）。
        child.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Console.WriteLine($"  [agent-out] {e.Data}");
            }
        };
        child.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Console.WriteLine($"  [agent-err] {e.Data}");
            }
        };
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        return child;
    }

    private static void StopAgent(Process? child)
    {
        if (child is null)
        {
            return;
        }

        try
        {
            if (!child.WaitForExit(3000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(3000);
            }

            Console.WriteLine($"    agent 子进程已退出（exit={child.ExitCode}）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    收尾子进程异常（忽略）: {ex.Message}");
        }
        finally
        {
            child.Dispose();
        }
    }

    /// <summary>从 startPort 起找一个未被占用的 TCP 端口。</summary>
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

    /// <summary>自 AppContext.BaseDirectory 向上查找含 agent/ 与 third_party/ 的目录。</summary>
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
