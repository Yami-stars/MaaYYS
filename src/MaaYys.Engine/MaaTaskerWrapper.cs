using System.Runtime.InteropServices;
using MaaYys.Engine.Native;

namespace MaaYys.Engine;

/// <summary>MaaTasker 原生句柄安全封装（SafeHandle 防泄漏）。</summary>
public sealed class MaaTaskerHandle : SafeHandle
{
    public MaaTaskerHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public MaaTaskerHandle(IntPtr ptr)
        : base(IntPtr.Zero, ownsHandle: true) => SetHandle(ptr);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        MaaFrameworkApi.MaaTaskerDestroy(handle);
        return true;
    }
}

/// <summary>
/// MaaFramework 任务器封装：BindResource/BindController 后 PostTask 执行 pipeline 入口，
/// 经 AddSink 订阅 Tasker.Task.* / Node.* 事件。
/// </summary>
public sealed class MaaTasker : IDisposable
{
    private readonly MaaTaskerHandle _handle;
    private readonly MaaEventCallback _sinkDelegate; // 持引用防 GC 回收导致回调崩溃
    private readonly long _sinkId;
    private bool _disposed;

    public MaaTasker()
    {
        _handle = new MaaTaskerHandle(MaaFrameworkApi.MaaTaskerCreate());
        if (_handle.IsInvalid)
        {
            throw new InvalidOperationException("MaaTaskerCreate 返回空句柄");
        }

        _sinkDelegate = OnNativeEvent;
        _sinkId = MaaFrameworkApi.MaaTaskerAddSink(_handle, _sinkDelegate, IntPtr.Zero);
    }

    /// <summary>任务/节点事件（Tasker.Task.* / Node.*）。</summary>
    public event EventHandler<MaaFrameworkEventArgs>? EventReceived;

    /// <summary>绑定资源。</summary>
    public bool BindResource(MaaResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return MaaFrameworkApi.MaaTaskerBindResource(_handle, resource.Handle) != MaaDef.False;
    }

    /// <summary>绑定控制器（原生句柄；仅引擎层内部使用）。</summary>
    internal bool BindController(IntPtr controller)
        => MaaFrameworkApi.MaaTaskerBindController(_handle, controller) != MaaDef.False;

    /// <summary>资源与控制器是否均已绑定。</summary>
    public bool Inited => MaaFrameworkApi.MaaTaskerInited(_handle) != MaaDef.False;

    /// <summary>
    /// 投递任务，返回任务 id（0 表示被拒绝，通常因资源/控制器未绑定）。
    /// pipeline_override 为 null 时传 "{}"（官方绑定的既定契约：该参数必须是合法 JSON 字符串，
    /// 传 nullptr 会在框架日志输出阶段触发未定义行为）。
    /// </summary>
    public long PostTask(string entry, string? pipelineOverride = null)
    {
        IntPtr entryPtr = MaaFramework.ToUtf8Native(entry);
        IntPtr overridePtr = MaaFramework.ToUtf8Native(pipelineOverride ?? "{}");
        try
        {
            return MaaFrameworkApi.MaaTaskerPostTask(_handle, entryPtr, overridePtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(entryPtr);
            Marshal.FreeCoTaskMem(overridePtr);
        }
    }

    /// <summary>查询任务状态。</summary>
    public MaaStatus Status(long taskId) => (MaaStatus)MaaFrameworkApi.MaaTaskerStatus(_handle, taskId);

    /// <summary>阻塞等待任务完成，返回最终状态。</summary>
    public MaaStatus Wait(long taskId) => (MaaStatus)MaaFrameworkApi.MaaTaskerWait(_handle, taskId);

    /// <summary>是否有任务在运行。</summary>
    public bool Running => MaaFrameworkApi.MaaTaskerRunning(_handle) != MaaDef.False;

    /// <summary>请求停止当前任务，返回停止任务 id。</summary>
    public long PostStop() => MaaFrameworkApi.MaaTaskerPostStop(_handle);

    private void OnNativeEvent(IntPtr handle, IntPtr message, IntPtr detailsJson, IntPtr transArg)
        => EventReceived?.Invoke(this, new MaaFrameworkEventArgs(
            MaaFramework.FromUtf8Native(message),
            MaaFramework.FromUtf8Native(detailsJson)));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_sinkId != 0)
        {
            MaaFrameworkApi.MaaTaskerRemoveSink(_handle, _sinkId);
        }

        _handle.Dispose();
        GC.SuppressFinalize(this);
    }
}
