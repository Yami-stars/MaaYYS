using System.Runtime.InteropServices;
using MaaYys.Engine.Native;

namespace MaaYys.Engine;

/// <summary>
/// 本地空控制器：基于 MaaCustomControllerCreate 自定义控制器扩展点实现，无额外 DLL 依赖
/// （MaaYYs 分发的运行库未包含 MaaDbgControlUnit.dll，Debug 控制器不可用）。
/// 行为：connect/connected 恒真；screencap 返回 1x1 静态黑色图像（CV_8UC3）；
/// request_uuid 返回固定标识；get_features 为 None；输入类回调不实现。
/// 用途：无设备环境下跑通引擎链路（P1.2）；后续可作为 pipeline 干跑控制器。
/// </summary>
public sealed class MaaNullController : IDisposable
{
    // 1x1 BGR 黑像素。DirectHit 识别不解析像素内容，仅要求 screencap 结果非空。
    private static readonly byte[] BlackPixel = [0, 0, 0];

    private const string ControllerUuid = "MaaYys-NullController";

    private readonly MaaControllerHandle _handle;
    private readonly IntPtr _callbacksPtr;
    private readonly IntPtr _pixelPtr;

    // 委托字段保活：GC 回收/移动被固定为函数指针的委托会导致原生调用崩溃
    private readonly MaaCustomControllerConnectCallback _onConnect;
    private readonly MaaCustomControllerConnectedCallback _onConnected;
    private readonly MaaCustomControllerRequestUuidCallback _onRequestUuid;
    private readonly MaaCustomControllerGetFeaturesCallback _onGetFeatures;
    private readonly MaaCustomControllerScreencapCallback _onScreencap;

    private bool _disposed;

    private MaaNullController(
        IntPtr handle,
        IntPtr callbacksPtr,
        IntPtr pixelPtr,
        MaaCustomControllerConnectCallback onConnect,
        MaaCustomControllerConnectedCallback onConnected,
        MaaCustomControllerRequestUuidCallback onRequestUuid,
        MaaCustomControllerGetFeaturesCallback onGetFeatures,
        MaaCustomControllerScreencapCallback onScreencap)
    {
        _handle = new MaaControllerHandle(handle);
        _callbacksPtr = callbacksPtr;
        _pixelPtr = pixelPtr;
        _onConnect = onConnect;
        _onConnected = onConnected;
        _onRequestUuid = onRequestUuid;
        _onGetFeatures = onGetFeatures;
        _onScreencap = onScreencap;
    }

    public static MaaNullController Create()
    {
        IntPtr pixelPtr = Marshal.AllocCoTaskMem(BlackPixel.Length);
        Marshal.Copy(BlackPixel, 0, pixelPtr, BlackPixel.Length);

        MaaCustomControllerConnectCallback onConnect = _ => MaaDef.True;
        MaaCustomControllerConnectedCallback onConnected = _ => MaaDef.True;
        MaaCustomControllerRequestUuidCallback onRequestUuid = (_, buffer) =>
        {
            IntPtr uuidPtr = MaaFramework.ToUtf8Native(ControllerUuid);
            try
            {
                return MaaFrameworkApi.MaaStringBufferSet(buffer, uuidPtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(uuidPtr);
            }
        };
        MaaCustomControllerGetFeaturesCallback onGetFeatures = _ => (ulong)MaaControllerFeature.None;
        MaaCustomControllerScreencapCallback onScreencap = (_, buffer) =>
            MaaFrameworkApi.MaaImageBufferSetRawData(buffer, pixelPtr, 1, 1, MaaDef.Cv8UC3);

        MaaCustomControllerCallbacks callbacks = new()
        {
            Connect = Marshal.GetFunctionPointerForDelegate(onConnect),
            Connected = Marshal.GetFunctionPointerForDelegate(onConnected),
            RequestUuid = Marshal.GetFunctionPointerForDelegate(onRequestUuid),
            GetFeatures = Marshal.GetFunctionPointerForDelegate(onGetFeatures),
            Screencap = Marshal.GetFunctionPointerForDelegate(onScreencap),
        };

        IntPtr callbacksPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<MaaCustomControllerCallbacks>());
        Marshal.StructureToPtr(callbacks, callbacksPtr, false);

        try
        {
            IntPtr handle = MaaFrameworkApi.MaaCustomControllerCreate(callbacksPtr, IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("MaaCustomControllerCreate 返回空句柄");
            }

            return new MaaNullController(handle, callbacksPtr, pixelPtr, onConnect, onConnected, onRequestUuid, onGetFeatures, onScreencap);
        }
        catch
        {
            Marshal.FreeCoTaskMem(callbacksPtr);
            Marshal.FreeCoTaskMem(pixelPtr);
            throw;
        }
    }

    /// <summary>原生句柄（仅引擎层内部使用，不向应用层暴露；P1.6 评审 R6）。</summary>
    internal IntPtr Handle => _handle.DangerousGetHandle();

    /// <summary>投递连接请求并等待完成（空控制器恒成功）。</summary>
    public MaaStatus Connect()
    {
        long id = MaaFrameworkApi.MaaControllerPostConnection(_handle);
        if (id == 0)
        {
            return MaaStatus.Invalid;
        }

        return (MaaStatus)MaaFrameworkApi.MaaControllerWait(_handle, id);
    }

    /// <summary>控制器当前是否已连接。</summary>
    public bool Connected => MaaFrameworkApi.MaaControllerConnected(_handle) != MaaDef.False;

    /// <summary>读取控制器 uuid（失败返回 null）。</summary>
    public string? Uuid
    {
        get
        {
            MaaStringBufferHandle buffer = new(MaaFrameworkApi.MaaStringBufferCreate());
            using (buffer)
            {
                if (buffer.IsInvalid || MaaFrameworkApi.MaaControllerGetUuid(_handle, buffer) == MaaDef.False)
                {
                    return null;
                }

                return buffer.GetString();
            }
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
        Marshal.FreeCoTaskMem(_callbacksPtr);
        Marshal.FreeCoTaskMem(_pixelPtr);
        GC.SuppressFinalize(this);
    }
}

/// <summary>MaaController 原生句柄安全封装。</summary>
public sealed class MaaControllerHandle : SafeHandle
{
    public MaaControllerHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public MaaControllerHandle(IntPtr ptr)
        : base(IntPtr.Zero, ownsHandle: true) => SetHandle(ptr);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        MaaFrameworkApi.MaaControllerDestroy(handle);
        return true;
    }
}
