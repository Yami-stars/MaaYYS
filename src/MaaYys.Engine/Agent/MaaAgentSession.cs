using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MaaYys.Engine;
using MaaYys.Engine.Native;

namespace MaaYys.Engine.Agent;

/// <summary>
/// agent 会话：托管 Go agent 子进程（MaaAgentServer）的生命周期，并通过
/// <see cref="MaaAgentClient"/> 与之握手、把其注册的自定义能力绑定到本地 <see cref="MaaResource"/>。
///
/// 设计原则（对齐 P4.1 验收：GUI 启动拉起、退出回收、日志互通）：
///   - 全部失败路径返回 null 或空列表，调用方降级为「无 agent」，绝不拖垮主流程；
///   - Dispose 时先断开 client 再杀子进程，避免 0xC0000005（client 持有已释放 resource 指针）；
///   - 子进程的 MaaFramework 运行库目录从当前进程解析（与 NativeLoader 同源），保证版本一致。
/// </summary>
public sealed class MaaAgentSession : IDisposable
{
    private readonly Process? _process;
    private readonly MaaAgentClient? _client;
    private readonly int _port;
    private bool _disposed;

    private MaaAgentSession(Process? process, MaaAgentClient? client, int port)
    {
        _process = process;
        _client = client;
        _port = port;
    }

    /// <summary>TCP 端口（agent 以此作为 identifier 连回本进程）。</summary>
    public int Port => _port;

    /// <summary>agent 注册的自定义动作名（握手后可用；失败返回空）。</summary>
    public string[] ActionNames => _client?.CustomActionList() ?? [];

    /// <summary>agent 注册的自定义识别名（握手后可用；失败返回空）。</summary>
    public string[] RecognitionNames => _client?.CustomRecognitionList() ?? [];

    public bool Connected => _client?.Connected ?? false;
    public bool Alive => _client?.Alive ?? false;

    /// <summary>
    /// 启动 Go agent 子进程并与之握手，把其注册的自定义能力绑定到 <paramref name="resource"/>。
    /// 任意步骤失败返回 null（调用方降级为「无 agent」，不影响主流程）。
    /// </summary>
    /// <param name="resource">已创建（通常已加载 bundle）的资源实例。</param>
    /// <param name="options">可选覆盖：agent 可执行文件、libdir、端口、超时。</param>
    public static MaaAgentSession? TryStart(MaaResource resource, AgentStartOptions? options = null)
    {
        options ??= new AgentStartOptions();
        try
        {
            string? exe = ResolveAgentExec(options.AgentExec);
            if (exe is null)
            {
                return null;
            }

            string? libDir = options.LibDir ?? DefaultLibDir();
            if (libDir is null)
            {
                return null;
            }

            int port = FindFreePort(options.BasePort);

            var startInfo = new ProcessStartInfo(exe, $"{port} --libdir \"{libDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            Process? proc = null;
            try
            {
                proc = Process.Start(startInfo);
            }
            catch
            {
                return null;
            }

            if (proc is null)
            {
                return null;
            }

            var client = MaaAgentClient.CreateTcp((ushort)port);
            if (!client.BindResource(resource))
            {
                client.Dispose();
                TryKill(proc);
                return null;
            }

            client.SetTimeout(options.ConnectTimeoutMs);
            if (!client.Connect())
            {
                client.Dispose();
                TryKill(proc);
                return null;
            }

            return new MaaAgentSession(proc, client, port);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>停止 agent：先断开 client（释放其对 resource 的引用），再杀子进程。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _client?.Disconnect();
        }
        catch
        {
            // 忽略断开异常
        }

        try
        {
            _client?.Dispose();
        }
        catch
        {
            // 忽略释放异常
        }

        TryKill(_process);
    }

    private static string? ResolveAgentExec(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string abs = Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(AppContext.BaseDirectory, configured);
            if (File.Exists(abs))
            {
                return abs;
            }
        }

        // 向上最多 8 级查找：仓库根的 agent/agent.exe 或 resource/agent/agent.exe（对齐 interface.json 的 child_exec）。
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string cand = Path.Combine(dir.FullName, "agent", "agent.exe");
            if (File.Exists(cand))
            {
                return cand;
            }

            string cand2 = Path.Combine(dir.FullName, "resource", "agent", "agent.exe");
            if (File.Exists(cand2))
            {
                return cand2;
            }
        }

        return null;
    }

    private static string? DefaultLibDir()
    {
        string? dll = MaaFramework.NativeLibraryPath;
        return dll is null ? null : Path.GetDirectoryName(dll);
    }

    private static int FindFreePort(int start)
    {
        for (int p = start; p < start + 200; p++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, p);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // 端口被占用，尝试下一个
            }
        }

        return start;
    }

    private static void TryKill(Process? proc)
    {
        if (proc is null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill();
            }
        }
        catch
        {
            // 忽略清理异常
        }
        finally
        {
            try
            {
                proc.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }
}

/// <summary>agent 启动选项（均可省略，走合理默认值）。</summary>
public sealed class AgentStartOptions
{
    /// <summary>agent 可执行文件路径；null = 自动解析（interface.json 的 child_exec / 向上查找）。</summary>
    public string? AgentExec { get; init; }

    /// <summary>MaaFramework 运行库目录（agent 子进程 --libdir）；null = 取当前进程同源目录。</summary>
    public string? LibDir { get; init; }

    /// <summary>起始端口（实际取到的空闲端口可能更高）。</summary>
    public int BasePort { get; init; } = 7788;

    /// <summary>与 agent 握手超时（毫秒）。</summary>
    public int ConnectTimeoutMs { get; init; } = 20_000;
}
