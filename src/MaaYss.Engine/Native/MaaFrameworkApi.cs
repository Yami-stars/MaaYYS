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

    // ---- Controller：Win32 控制器与通用操作（P1.4）----
    // 签名核对自 v5.15.0-beta.1 include/MaaFramework/Instance/MaaController.h。

    /// <summary>
    /// MaaController* MaaWin32ControllerCreate(void* hWnd, MaaWin32ScreencapMethod,
    /// MaaWin32InputMethod mouse, MaaWin32InputMethod keyboard);
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaWin32ControllerCreate(
        IntPtr hWnd,
        ulong screencapMethod,
        ulong mouseMethod,
        ulong keyboardMethod);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long MaaControllerPostConnection(MaaControllerHandle ctrl);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long MaaControllerPostClick(MaaControllerHandle ctrl, int x, int y);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long MaaControllerPostSwipe(
        MaaControllerHandle ctrl, int x1, int y1, int x2, int y2, int duration);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long MaaControllerPostScreencap(MaaControllerHandle ctrl);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int MaaControllerStatus(MaaControllerHandle ctrl, long id);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int MaaControllerWait(MaaControllerHandle ctrl, long id);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaControllerConnected(MaaControllerHandle ctrl);

    /// <summary>MaaBool MaaControllerCachedImage(ctrl, /* out */ MaaImageBuffer* buffer)。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaControllerCachedImage(MaaControllerHandle ctrl, MaaImageBufferHandle buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaControllerGetUuid(MaaControllerHandle ctrl, MaaStringBufferHandle buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaControllerGetInfo(MaaControllerHandle ctrl, MaaStringBufferHandle buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaControllerGetResolution(MaaControllerHandle ctrl, out int width, out int height);

    /// <summary>
    /// MaaBool MaaControllerSetOption(ctrl, MaaCtrlOption key, MaaOptionValue value, MaaOptionValueSize val_size)。
    /// value 为 void*（按 key 语义解释为 int*/char*/bool*），此处由托管侧分配原生内存。
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaControllerSetOption(MaaControllerHandle ctrl, int key, IntPtr value, ulong valueSize);

    // ---- StringBuffer（控制器信息读取用）----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaStringBufferCreate();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MaaStringBufferDestroy(IntPtr buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaStringBufferIsEmpty(IntPtr buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaStringBufferGet(IntPtr buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong MaaStringBufferSize(IntPtr buffer);

    // ---- ImageBuffer（截图读取用）----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaImageBufferCreate();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MaaImageBufferDestroy(IntPtr buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaImageBufferIsEmpty(IntPtr buffer);

    /// <summary>MaaImageRawData（void*）——cv::Mat 数据视图，生命周期依附于 buffer。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaImageBufferGetRawData(IntPtr buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int MaaImageBufferWidth(IntPtr buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int MaaImageBufferHeight(IntPtr buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int MaaImageBufferChannels(IntPtr buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int MaaImageBufferType(IntPtr buffer);

    // ---- Buffer（自定义控制器回调写结果用）----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaStringBufferSet(IntPtr buffer, IntPtr str);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaImageBufferSetRawData(IntPtr buffer, IntPtr data, int width, int height, int type);

    // ---- Toolkit：桌面窗口枚举（MaaToolkit.dll，P1.4）----
    // 签名核对自 v5.15.0-beta.1 include/MaaToolkit/DesktopWindow/MaaToolkitDesktopWindow.h。
    // 注意：MaaWin32ControlUnit 的 MaaWin32ControllerCreate 直接收 hWnd，
    // 窗口正则搜索是 Toolkit 的职责（对齐 MaaPiCli interactor.cpp 的行为）。

    private const string ToolkitLib = "MaaToolkit";

    [DllImport(ToolkitLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaToolkitDesktopWindowListCreate();

    [DllImport(ToolkitLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MaaToolkitDesktopWindowListDestroy(IntPtr list);

    [DllImport(ToolkitLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaToolkitDesktopWindowFindAll(MaaToolkitDesktopWindowListHandle list);

    [DllImport(ToolkitLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong MaaToolkitDesktopWindowListSize(MaaToolkitDesktopWindowListHandle list);

    [DllImport(ToolkitLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaToolkitDesktopWindowListAt(MaaToolkitDesktopWindowListHandle list, ulong index);

    [DllImport(ToolkitLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaToolkitDesktopWindowGetHandle(IntPtr window);

    [DllImport(ToolkitLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaToolkitDesktopWindowGetClassName(IntPtr window);

    [DllImport(ToolkitLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaToolkitDesktopWindowGetWindowName(IntPtr window);

    // ---- AgentClient（MaaAgentClient.dll，P1.5）----
    // 签名核对自 v5.15.0-beta.1 include/MaaAgentClient/MaaAgentClientAPI.h。
    // 注意：Identifier 为 in/out 参数——CreateV2 时由调用方写入期望名称，
    // 返回后由框架改写为实际使用的 identifier（TCP 模式下为端口号字符串）。

    private const string AgentClientLib = "MaaAgentClient";

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaAgentClientCreateV2(MaaStringBufferHandle identifier);

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaAgentClientCreateTcp(ushort port);

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MaaAgentClientDestroy(IntPtr client);

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaAgentClientIdentifier(MaaAgentClientHandle client, MaaStringBufferHandle identifier);

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaAgentClientBindResource(MaaAgentClientHandle client, MaaResourceHandle res);

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaAgentClientConnect(MaaAgentClientHandle client);

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaAgentClientDisconnect(MaaAgentClientHandle client);

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaAgentClientConnected(MaaAgentClientHandle client);

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaAgentClientAlive(MaaAgentClientHandle client);

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaAgentClientSetTimeout(MaaAgentClientHandle client, long milliseconds);

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaAgentClientGetCustomRecognitionList(
        MaaAgentClientHandle client, MaaStringListBufferHandle buffer);

    [DllImport(AgentClientLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaAgentClientGetCustomActionList(
        MaaAgentClientHandle client, MaaStringListBufferHandle buffer);

    // ---- StringListBuffer（MaaFramework.dll，读取 agent 注册列表用）----

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaStringListBufferCreate();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MaaStringListBufferDestroy(IntPtr buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte MaaStringListBufferIsEmpty(IntPtr buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong MaaStringListBufferSize(IntPtr buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MaaStringListBufferAt(IntPtr buffer, ulong index);
}
