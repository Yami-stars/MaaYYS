using System.Runtime.InteropServices;
using MaaYss.Engine.Native;

namespace MaaYss.Engine;

/// <summary>MaaResource 原生句柄安全封装（SafeHandle 防泄漏）。</summary>
public sealed class MaaResourceHandle : SafeHandle
{
    public MaaResourceHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public MaaResourceHandle(IntPtr ptr)
        : base(IntPtr.Zero, ownsHandle: true) => SetHandle(ptr);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        MaaFrameworkApi.MaaResourceDestroy(handle);
        return true;
    }
}

/// <summary>
/// MaaFramework 资源封装：PostBundle 加载 pipeline/image/model，
/// 经 AddSink 订阅 Resource.Loading.* 事件。
/// </summary>
public sealed class MaaResource : IDisposable
{
    private readonly MaaResourceHandle _handle;
    private readonly MaaEventCallback _sinkDelegate; // 持引用防 GC 回收导致回调崩溃
    private readonly long _sinkId;
    private bool _disposed;

    public MaaResource()
    {
        _handle = new MaaResourceHandle(MaaFrameworkApi.MaaResourceCreate());
        if (_handle.IsInvalid)
        {
            throw new InvalidOperationException("MaaResourceCreate 返回空句柄");
        }

        _sinkDelegate = OnNativeEvent;
        _sinkId = MaaFrameworkApi.MaaResourceAddSink(_handle, _sinkDelegate, IntPtr.Zero);
    }

    /// <summary>资源加载事件（Resource.Loading.*）。</summary>
    public event EventHandler<MaaFrameworkEventArgs>? EventReceived;

    /// <summary>异步加载资源包目录（含 pipeline/image/model 子目录），返回任务 id。</summary>
    public long PostBundle(string path)
    {
        IntPtr pathPtr = MaaFramework.ToUtf8Native(path);
        try
        {
            return MaaFrameworkApi.MaaResourcePostBundle(_handle, pathPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>查询指定加载任务的状态。</summary>
    public MaaStatus Status(long id) => (MaaStatus)MaaFrameworkApi.MaaResourceStatus(_handle, id);

    /// <summary>阻塞等待指定加载任务完成，返回最终状态。</summary>
    public MaaStatus Wait(long id) => (MaaStatus)MaaFrameworkApi.MaaResourceWait(_handle, id);

    /// <summary>资源是否已加载完成。</summary>
    public bool Loaded => MaaFrameworkApi.MaaResourceLoaded(_handle) != MaaDef.False;

    internal MaaResourceHandle Handle => _handle;

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
            MaaFrameworkApi.MaaResourceRemoveSink(_handle, _sinkId);
        }

        _handle.Dispose();
        GC.SuppressFinalize(this);
    }
}
