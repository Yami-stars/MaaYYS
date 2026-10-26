using System.Runtime.InteropServices;

namespace MaaYys.Engine.Native;

/// <summary>
/// MaaFramework C API 基础类型与常量。
/// 来源：MaaFramework v5.13.0-beta.2 include/MaaFramework（与 third_party/maafw 运行库同版本）。
/// </summary>
internal static class MaaDef
{
    // typedef uint8_t MaaBool;  → 0 = false, 1 = true
    public const byte True = 1;
    public const byte False = 0;

    /// <summary>OpenCV 图像类型 CV_8UC3（3 通道 8 位，BGR）。</summary>
    public const int Cv8UC3 = 16;
}

/// <summary>MaaControllerFeature 位标志（MaaDef.h）。</summary>
[Flags]
public enum MaaControllerFeature : ulong
{
    None = 0,
    UseMouseDownAndUpInsteadOfClick = 1UL << 0,
    UseKeyboardDownAndUpInsteadOfClick = 1UL << 1,
    NoScalingTouchPoints = 1UL << 2,
}

/// <summary>
/// MaaCustomControllerCallbacks（MaaCustomController.h，21 个函数指针，字段顺序敏感）。
/// 未实现的回调传 null；框架调用未实现的回调时可能崩溃（CustomControlUnit 对部分回调有空值保护）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MaaCustomControllerCallbacks
{
    public IntPtr Connect;
    public IntPtr Connected;
    public IntPtr RequestUuid;
    public IntPtr GetFeatures;
    public IntPtr StartApp;
    public IntPtr StopApp;
    public IntPtr Screencap;
    public IntPtr Click;
    public IntPtr Swipe;
    public IntPtr TouchDown;
    public IntPtr TouchMove;
    public IntPtr TouchUp;
    public IntPtr ClickKey;
    public IntPtr InputText;
    public IntPtr KeyDown;
    public IntPtr KeyUp;
    public IntPtr Scroll;
    public IntPtr RelativeMove;
    public IntPtr Shell;
    public IntPtr Inactive;
    public IntPtr GetInfo;
}

// ---- 自定义控制器回调委托（签名逐条核对自 MaaCustomController.h）----

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate byte MaaCustomControllerConnectCallback(IntPtr transArg);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate byte MaaCustomControllerConnectedCallback(IntPtr transArg);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate byte MaaCustomControllerRequestUuidCallback(IntPtr transArg, IntPtr buffer /* MaaStringBuffer* */);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate ulong MaaCustomControllerGetFeaturesCallback(IntPtr transArg);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate byte MaaCustomControllerScreencapCallback(IntPtr transArg, IntPtr buffer /* MaaImageBuffer* */);

/// <summary>MaaStatus：任务/资源生命周期状态（MaaDef.h 枚举值）。</summary>
public enum MaaStatus
{
    Invalid = 0,
    Pending = 1000,
    Running = 2000,
    Succeeded = 3000,
    Failed = 4000,
}

/// <summary>
/// MaaEventCallback（MaaDef.h）：
/// void(MAA_CALL* MaaEventCallback)(void* handle, const char* message, const char* details_json, void* trans_arg);
/// 字符串参数用 IntPtr 接收、由调用侧按 UTF-8 转换，避免默认 ANSI marshalling 在中文环境下乱码。
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void MaaEventCallback(IntPtr handle, IntPtr message, IntPtr detailsJson, IntPtr transArg);

/// <summary>MaaMsg 消息常量（MaaMsg.h，常量值即消息字符串本身）。</summary>
public static class MaaMsg
{
    public const string ResourceLoadingStarting = "Resource.Loading.Starting";
    public const string ResourceLoadingSucceeded = "Resource.Loading.Succeeded";
    public const string ResourceLoadingFailed = "Resource.Loading.Failed";

    public const string ControllerActionStarting = "Controller.Action.Starting";
    public const string ControllerActionSucceeded = "Controller.Action.Succeeded";
    public const string ControllerActionFailed = "Controller.Action.Failed";

    public const string TaskerTaskStarting = "Tasker.Task.Starting";
    public const string TaskerTaskSucceeded = "Tasker.Task.Succeeded";
    public const string TaskerTaskFailed = "Tasker.Task.Failed";

    public const string NodePipelineNodeStarting = "Node.PipelineNode.Starting";
    public const string NodePipelineNodeSucceeded = "Node.PipelineNode.Succeeded";
    public const string NodePipelineNodeFailed = "Node.PipelineNode.Failed";

    public const string NodeRecognitionNodeStarting = "Node.RecognitionNode.Starting";
    public const string NodeRecognitionNodeSucceeded = "Node.RecognitionNode.Succeeded";
    public const string NodeRecognitionNodeFailed = "Node.RecognitionNode.Failed";

    public const string NodeActionNodeStarting = "Node.ActionNode.Starting";
    public const string NodeActionNodeSucceeded = "Node.ActionNode.Succeeded";
    public const string NodeActionNodeFailed = "Node.ActionNode.Failed";

    public const string NodeNextListStarting = "Node.NextList.Starting";
    public const string NodeNextListSucceeded = "Node.NextList.Succeeded";
    public const string NodeNextListFailed = "Node.NextList.Failed";

    public const string NodeRecognitionStarting = "Node.Recognition.Starting";
    public const string NodeRecognitionSucceeded = "Node.Recognition.Succeeded";
    public const string NodeRecognitionFailed = "Node.Recognition.Failed";

    public const string NodeActionStarting = "Node.Action.Starting";
    public const string NodeActionSucceeded = "Node.Action.Succeeded";
    public const string NodeActionFailed = "Node.Action.Failed";

    public const string NodeWaitFreezesStarting = "Node.WaitFreezes.Starting";
    public const string NodeWaitFreezesSucceeded = "Node.WaitFreezes.Succeeded";
    public const string NodeWaitFreezesFailed = "Node.WaitFreezes.Failed";
}

/// <summary>
/// MaaCtrlOption（MaaDef.h 枚举）。控制器/任务器级设置项。
/// 注：值即枚举序数（ScreenshotTargetLongSide=1 起）。
/// </summary>
public enum MaaCtrlOption
{
    Invalid = 0,
    ScreenshotTargetLongSide = 1,
    ScreenshotTargetShortSide = 2,
    ScreenshotUseRawSize = 3,
    MouseLockFollow = 4,
    ScreenshotResizeMethod = 6,
    BackgroundManagedKeys = 7,
    ScreenshotTargetExpand = 8,
}

/// <summary>Win32 截图方法位掩码（MaaDef.h，uint64_t 位标志）。</summary>
public static class MaaWin32ScreencapMethod
{
    public const ulong None = 0UL;
    public const ulong Gdi = 1UL;
    public const ulong FramePool = 1UL << 1;
    public const ulong DxgiDesktopDup = 1UL << 2;
    public const ulong DxgiDesktopDupWindow = 1UL << 3;
    public const ulong PrintWindow = 1UL << 4;
    public const ulong ScreenDc = 1UL << 5;
    public const ulong All = ~None;
    /// <summary>前台截图组合（DXGI 窗口 + ScreenDC）。</summary>
    public const ulong Foreground = DxgiDesktopDupWindow | ScreenDc;
    /// <summary>后台截图组合（FramePool + PrintWindow），可抓硬件加速画面且不抢焦点。</summary>
    public const ulong Background = FramePool | PrintWindow;
}

/// <summary>Win32 输入方法位掩码（MaaDef.h，uint64_t 位标志）。</summary>
public static class MaaWin32InputMethod
{
    public const ulong None = 0UL;
    public const ulong Seize = 1UL;
    public const ulong SendMessage = 1UL << 1;
    public const ulong PostMessage = 1UL << 2;
    public const ulong LegacyEvent = 1UL << 3;
    public const ulong SendMessageWithCursorPos = 1UL << 5;
    public const ulong PostMessageWithCursorPos = 1UL << 6;
    public const ulong SendMessageWithWindowPos = 1UL << 7;
    public const ulong PostMessageWithWindowPos = 1UL << 8;
    public const ulong Interception = 1UL << 9;
    public const ulong AnchoredTouch = 1UL << 10;
}
