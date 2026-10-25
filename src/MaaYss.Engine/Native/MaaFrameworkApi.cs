using System.Runtime.InteropServices;

namespace MaaYss.Engine.Native;

/// <summary>
/// MaaFramework.dll C API 的 P/Invoke 声明。
/// 签名逐条核对自 v5.13.0-beta.2 头文件（MaaTasker.h / MaaResource.h / MaaController.h / MaaUtility.h）。
/// 注意：该版本为 Tasker 中心化 API，不存在 MaaInstance 系列。
/// 所有 const char* 参数/返回值一律手工按 UTF-8 marshalling（框架内部为 UTF-8）。
/// 句柄参数使用 SafeHandle 派生类型（P/Invoke 自动提取且调用期间保活）；
/// *Destroy 保留 IntPtr，因其在 SafeHandle.ReleaseHandle 内部调用。
/// </summary>
internal static class MaaFrameworkApi
{
    private const string Lib = "MaaFramework";

    // ---- Utility ----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "MaaVersion")]
    private static extern IntPtr MaaVersionNative();

    public static string Version()
    {
        IntPtr ptr = MaaVersionNative();
        return ptr == IntPtr.Zero ? "<null>" : Marshal.PtrToStringUTF8(ptr) ?? "<null>";
    }

    // ---- Tasker ----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaTaskerCreate();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MaaTaskerDestroy(IntPtr tasker);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long MaaTaskerAddSink(MaaTaskerHandle tasker, MaaEventCallback sink, IntPtr transArg);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MaaTaskerRemoveSink(MaaTaskerHandle tasker, long sinkId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaTaskerBindResource(MaaTaskerHandle tasker, MaaResourceHandle res);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaTaskerBindController(MaaTaskerHandle tasker, IntPtr ctrl);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaTaskerInited(MaaTaskerHandle tasker);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long MaaTaskerPostTask(MaaTaskerHandle tasker, IntPtr entry, IntPtr pipelineOverride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int MaaTaskerStatus(MaaTaskerHandle tasker, long id);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int MaaTaskerWait(MaaTaskerHandle tasker, long id);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaTaskerRunning(MaaTaskerHandle tasker);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long MaaTaskerPostStop(MaaTaskerHandle tasker);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaTaskerStopping(MaaTaskerHandle tasker);

    // ---- Resource ----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaResourceCreate();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MaaResourceDestroy(IntPtr res);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long MaaResourceAddSink(MaaResourceHandle res, MaaEventCallback sink, IntPtr transArg);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MaaResourceRemoveSink(MaaResourceHandle res, long sinkId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long MaaResourcePostBundle(MaaResourceHandle res, IntPtr path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int MaaResourceStatus(MaaResourceHandle res, long id);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int MaaResourceWait(MaaResourceHandle res, long id);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaResourceLoaded(MaaResourceHandle res);

    // ---- Controller（P1.2 仅用到 Debug 控制器）----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaCustomControllerCreate(IntPtr callbacks, IntPtr controllerArg);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MaaControllerDestroy(IntPtr ctrl);

    // ---- Buffer（自定义控制器回调写结果用）----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaStringBufferSet(IntPtr buffer, IntPtr str);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaImageBufferSetRawData(IntPtr buffer, IntPtr data, int width, int height, int type);
}
