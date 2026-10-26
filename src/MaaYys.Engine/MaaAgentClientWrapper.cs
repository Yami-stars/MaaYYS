using System.Runtime.InteropServices;
using MaaYys.Engine.Native;

namespace MaaYys.Engine;

/// <summary>
/// MaaAgentClient 托管封装（MaaAgentClient.dll，PI V2 agent 链路的服务端侧）。
///
/// 工作流（对齐官方 examples/agent-client）：
///   1. 创建 client（TCP 端口模式：client 监听，agent 子进程连入）
///   2. 取回 identifier（TCP 模式为端口号字符串），作为子进程启动参数
///   3. agent 子进程 MaaAgentServerStartUp(identifier) 后，调用 Connect 完成握手
///   4. BindResource：把 agent 注册的 custom action/recognition 挂到本地 Resource
///   5. pipeline 中即可用 custom_action / custom_recognition 引用 agent 提供的能力
///
/// 注意：BindResource 必须在 Connect 之前或之后均可，但必须在 PostTask 之前完成，
/// 否则任务中的 custom 节点找不到实现。
/// </summary>
public sealed class MaaAgentClient : IDisposable
{
    private readonly MaaAgentClientHandle _handle;
    private bool _disposed;

    private MaaAgentClient(MaaAgentClientHandle handle) => _handle = handle;

    /// <summary>
    /// 以 IPC 名称模式创建 client。identifier 为空时由框架生成随机名称。
    /// 创建后应调用 <see cref="Identifier"/> 取回实际名称，交给 agent 子进程。
    /// </summary>
    public static MaaAgentClient Create(string? identifier = null)
    {
        MaaStringBufferHandle buffer = new(MaaFrameworkApi.MaaStringBufferCreate());
        using (buffer)
        {
            if (!string.IsNullOrEmpty(identifier))
            {
                SetBufferString(buffer, identifier);
            }

            IntPtr ptr = MaaFrameworkApi.MaaAgentClientCreateV2(buffer);
            if (ptr == IntPtr.Zero)
            {
                throw new InvalidOperationException("MaaAgentClientCreateV2 返回空句柄（MaaAgentClient.dll 加载失败或参数非法）");
            }

            return new MaaAgentClient(new MaaAgentClientHandle(ptr));
        }
    }

    /// <summary>
    /// 以 TCP 端口模式创建 client：client 在 127.0.0.1:port 监听，
    /// agent 子进程使用端口号字符串作为 identifier 连入。
    /// </summary>
    public static MaaAgentClient CreateTcp(ushort port)
    {
        IntPtr ptr = MaaFrameworkApi.MaaAgentClientCreateTcp(port);
        if (ptr == IntPtr.Zero)
        {
            throw new InvalidOperationException($"MaaAgentClientCreateTcp({port}) 返回空句柄（端口被占用或 DLL 加载失败）");
        }

        return new MaaAgentClient(new MaaAgentClientHandle(ptr));
    }

    /// <summary>本 client 的 identifier（传给 agent 子进程作为启动参数）。</summary>
    public string? Identifier()
    {
        MaaStringBufferHandle buffer = new(MaaFrameworkApi.MaaStringBufferCreate());
        using (buffer)
        {
            return MaaFrameworkApi.MaaAgentClientIdentifier(_handle, buffer) == MaaDef.False
                ? null
                : buffer.GetString();
        }
    }

    /// <summary>把 agent 注册的自定义能力绑定到本地资源（PostTask 前必须完成）。</summary>
    public bool BindResource(MaaResource resource)
        => MaaFrameworkApi.MaaAgentClientBindResource(_handle, resource.Handle) != MaaDef.False;

    /// <summary>设置单次通信超时（含首次握手）。</summary>
    public bool SetTimeout(int milliseconds)
        => MaaFrameworkApi.MaaAgentClientSetTimeout(_handle, milliseconds) != MaaDef.False;

    /// <summary>与 agent 子进程握手；agent 未启动时会阻塞至超时后返回 false。</summary>
    public bool Connect()
        => MaaFrameworkApi.MaaAgentClientConnect(_handle) != MaaDef.False;

    public bool Disconnect()
        => MaaFrameworkApi.MaaAgentClientDisconnect(_handle) != MaaDef.False;

    public bool Connected => MaaFrameworkApi.MaaAgentClientConnected(_handle) != MaaDef.False;

    public bool Alive => MaaFrameworkApi.MaaAgentClientAlive(_handle) != MaaDef.False;

    /// <summary>agent 注册的 custom recognition 名称列表（握手后可用）。</summary>
    public string[] CustomRecognitionList()
    {
        MaaStringListBufferHandle buffer = new(MaaFrameworkApi.MaaStringListBufferCreate());
        using (buffer)
        {
            return MaaFrameworkApi.MaaAgentClientGetCustomRecognitionList(_handle, buffer) == MaaDef.False
                ? []
                : buffer.ToArray();
        }
    }

    /// <summary>agent 注册的 custom action 名称列表（握手后可用）。</summary>
    public string[] CustomActionList()
    {
        MaaStringListBufferHandle buffer = new(MaaFrameworkApi.MaaStringListBufferCreate());
        using (buffer)
        {
            return MaaFrameworkApi.MaaAgentClientGetCustomActionList(_handle, buffer) == MaaDef.False
                ? []
                : buffer.ToArray();
        }
    }

    private static void SetBufferString(MaaStringBufferHandle buffer, string value)
    {
        IntPtr ptr = MaaFramework.ToUtf8Native(value);
        try
        {
            MaaFrameworkApi.MaaStringBufferSet(buffer.DangerousGetHandle(), ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }
}
