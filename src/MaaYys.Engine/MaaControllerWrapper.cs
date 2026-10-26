using System.Runtime.InteropServices;
using MaaYys.Engine.Devices;
using MaaYys.Engine.Native;

namespace MaaYys.Engine;

/// <summary>
/// Win32 控制器托管封装（MaaWin32ControllerCreate + 通用 Post/Wait 操作 API）。
/// 截图方式默认 FramePool：它抓的是窗口的完整客户区画面（无黑边），截图坐标与客户区坐标
/// 严格成比例，点击映射精确。P1.4 实测（阴阳师桌面版，DPI 125%）：
///   PrintWindow 抓到的图右侧/底部各约 20% 为黑边（DPI-unaware 游戏只绘制虚拟客户区），
///   框架按「截图尺寸:客户区尺寸」映射会把点击整体带偏；FramePool 无此问题，一次点击即命中。
/// 若 FramePool 不可用（远程桌面/驱动异常等导致截图空白），由 CreateWithFallback 回退 PrintWindow。
/// 键鼠默认 SendMessage（后台不抢焦点），与 MaaYYs interface.json 的 Win32 控制器配置一致。
/// </summary>
public sealed class MaaWin32Controller : IDisposable
{
    private readonly MaaControllerHandle _handle;
    private long _frameSequence;
    private bool _disposed;

    private MaaWin32Controller(MaaControllerHandle handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// 创建 Win32 控制器。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="screencapMethod">截图方法位掩码（默认 FramePool，理由见类注释）。</param>
    /// <param name="mouseMethod">鼠标输入方法位掩码（默认 SendMessage）。</param>
    /// <param name="keyboardMethod">键盘输入方法位掩码（默认 SendMessage）。</param>
    public static MaaWin32Controller Create(
        IntPtr hWnd,
        ulong screencapMethod = MaaWin32ScreencapMethod.FramePool,
        ulong mouseMethod = MaaWin32InputMethod.SendMessage,
        ulong keyboardMethod = MaaWin32InputMethod.SendMessage)
    {
        IntPtr ptr = MaaFrameworkApi.MaaWin32ControllerCreate(hWnd, screencapMethod, mouseMethod, keyboardMethod);
        if (ptr == IntPtr.Zero)
        {
            throw new InvalidOperationException("MaaWin32ControllerCreate 返回空句柄（hWnd 无效或窗口已销毁）");
        }

        return new MaaWin32Controller(new MaaControllerHandle(ptr));
    }

    /// <summary>本实例实际使用的截图方法（回退场景下由 CreateWithFallback 写入）。</summary>
    public ulong ScreencapMethod { get; private set; } = MaaWin32ScreencapMethod.FramePool;

    /// <summary>
    /// 创建 Win32 控制器并自动选择可用的截图方法。
    /// 优先使用 <paramref name="preferredScreencapMethod"/>（未指定时为 FramePool）；
    /// 若连接失败、截图为空或黑边严重，则回退 PrintWindow。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="usedScreencapMethod">实际采用的截图方法。</param>
    /// <param name="diagnostic">各候选方法的尝试结果说明（用于日志/排障）。</param>
    /// <param name="mouseMethod">鼠标输入方法位掩码。</param>
    /// <param name="keyboardMethod">键盘输入方法位掩码。</param>
    /// <param name="preferredScreencapMethod">首选截图方法；null 表示 FramePool。</param>
    /// <returns></returns>
    public static MaaWin32Controller CreateWithFallback(
        IntPtr hWnd,
        out ulong usedScreencapMethod,
        out string diagnostic,
        ulong mouseMethod = MaaWin32InputMethod.SendMessage,
        ulong keyboardMethod = MaaWin32InputMethod.SendMessage,
        ulong? preferredScreencapMethod = null)
    {
        // 候选顺序：首选 → FramePool → PrintWindow。
        // 这样即便配置里写的是 PrintWindow，也会因黑边严重而自动落到 FramePool 上。
        ulong preferred = preferredScreencapMethod ?? MaaWin32ScreencapMethod.FramePool;
        var candidates = new List<ulong> { preferred };
        if (!candidates.Contains(MaaWin32ScreencapMethod.FramePool))
        {
            candidates.Add(MaaWin32ScreencapMethod.FramePool);
        }

        if (!candidates.Contains(MaaWin32ScreencapMethod.PrintWindow))
        {
            candidates.Add(MaaWin32ScreencapMethod.PrintWindow);
        }

        var notes = new List<string>();
        for (int i = 0; i < candidates.Count; i++)
        {
            ulong method = candidates[i];
            bool isLast = i == candidates.Count - 1;
            MaaWin32Controller? candidate = null;
            try
            {
                candidate = Create(hWnd, method, mouseMethod, keyboardMethod);
                var status = candidate.Connect();
                if (status != MaaStatus.Succeeded)
                {
                    notes.Add($"{MaaWin32MethodNames.ScreencapName(method)}: 连接失败({status})");
                    candidate.Dispose();
                    continue;
                }

                var probe = candidate.Capture();
                if (probe is null)
                {
                    notes.Add($"{MaaWin32MethodNames.ScreencapName(method)}: 截图为空");
                    candidate.Dispose();
                    continue;
                }

                if (probe.NonBlackRatio < 0.01)
                {
                    notes.Add($"{MaaWin32MethodNames.ScreencapName(method)}: 画面几乎全黑（非黑比例 {probe.NonBlackRatio:P1}）");
                    if (!isLast)
                    {
                        candidate.Dispose();
                        continue;
                    }
                }
                else if (probe.HasSevereBlackBorder)
                {
                    notes.Add($"{MaaWin32MethodNames.ScreencapName(method)}: 黑边严重(右{probe.BlackBorderRight}px 下{probe.BlackBorderBottom}px, {probe.Width}x{probe.Height})");
                    if (!isLast)
                    {
                        candidate.Dispose();
                        continue;
                    }
                }
                else
                {
                    notes.Add($"{MaaWin32MethodNames.ScreencapName(method)}: 可用（黑边 右{probe.BlackBorderRight}px 下{probe.BlackBorderBottom}px）");
                }

                candidate.ScreencapMethod = method;
                usedScreencapMethod = method;
                diagnostic = string.Join(" | ", notes);
                return candidate;
            }
            catch (Exception ex)
            {
                notes.Add($"{MaaWin32MethodNames.ScreencapName(method)}: 异常 {ex.Message}");
                candidate?.Dispose();
            }
        }

        // 全部失败：仍返回最后尝试的组合，由调用方根据 diagnostic 提示用户。
        usedScreencapMethod = candidates[^1];
        diagnostic = string.Join(" | ", notes);
        return Create(hWnd, usedScreencapMethod, mouseMethod, keyboardMethod);
    }

    /// <summary>原生句柄（仅引擎层内部使用，不向应用层暴露；P1.6 评审 R6）。</summary>
    internal IntPtr Handle => _handle.DangerousGetHandle();

    /// <summary>投递连接请求并等待完成，返回最终状态。</summary>
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

    /// <summary>读取控制器信息 JSON（失败返回 null）。</summary>
    public string? InfoJson
    {
        get
        {
            MaaStringBufferHandle buffer = new(MaaFrameworkApi.MaaStringBufferCreate());
            using (buffer)
            {
                if (buffer.IsInvalid || MaaFrameworkApi.MaaControllerGetInfo(_handle, buffer) == MaaDef.False)
                {
                    return null;
                }

                return buffer.GetString();
            }
        }
    }

    /// <summary>获取未缩放的原始设备分辨率（连接并截图过后可用）。</summary>
    public bool TryGetResolution(out int width, out int height)
        => MaaFrameworkApi.MaaControllerGetResolution(_handle, out width, out height) != MaaDef.False;

    /// <summary>
    /// 设置截图目标短边像素（对应 interface.json 的 display_short_side）。
    /// 截图会按此设置等比缩放；设为 0 或使用 MaaCtrlOption.ScreenshotUseRawSize 可关闭缩放。
    /// </summary>
    public bool SetScreenshotTargetShortSide(int shortSide)
    {
        IntPtr value = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            Marshal.WriteInt32(value, shortSide);
            return MaaFrameworkApi.MaaControllerSetOption(
                _handle, (int)MaaCtrlOption.ScreenshotTargetShortSide, value, (ulong)sizeof(int)) != MaaDef.False;
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    /// <summary>投递截图请求并等待完成，返回最终状态。</summary>
    public MaaStatus Screencap()
    {
        long id = MaaFrameworkApi.MaaControllerPostScreencap(_handle);
        if (id == 0)
        {
            return MaaStatus.Invalid;
        }

        return (MaaStatus)MaaFrameworkApi.MaaControllerWait(_handle, id);
    }

    /// <summary>截图并返回统计信息（含黑边测量），不写文件。</summary>
    public ScreenshotResult? Capture() => CaptureCore(out _);

    /// <summary>
    /// 截图并保存为 24 位 BMP（自包含编码，无外部依赖）。
    /// 返回截图尺寸与统计信息；截图失败返回 null。
    /// </summary>
    public ScreenshotResult? SaveScreenshot(string path)
    {
        ScreenshotResult? result = CaptureCore(out byte[]? pixels);
        if (result is null || pixels is null)
        {
            return null;
        }

        BmpEncoder.Save(path, pixels, result.Width, result.Height, result.Channels);
        return result;
    }

    private ScreenshotResult? CaptureCore(out byte[]? pixelsOut) =>
        Devices.ControllerCapture.Capture(_handle, out pixelsOut);

    /// <summary>
    /// 截图并返回带原始像素的帧（P2.3 实时预览用）；失败返回 null。
    /// </summary>
    public FrameData? CaptureFrame()
    {
        ScreenshotResult? stats = CaptureCore(out byte[]? pixels);
        if (stats is null || pixels is null)
        {
            return null;
        }

        return new FrameData(
            pixels, stats.Width, stats.Height, stats.Channels,
            Interlocked.Increment(ref _frameSequence), DateTimeOffset.Now, stats);
    }

    /// <summary>投递单击请求并等待完成，返回最终状态。</summary>
    public MaaStatus Click(int x, int y)
    {
        long id = MaaFrameworkApi.MaaControllerPostClick(_handle, x, y);
        if (id == 0)
        {
            return MaaStatus.Invalid;
        }

        return (MaaStatus)MaaFrameworkApi.MaaControllerWait(_handle, id);
    }

    /// <summary>投递滑动请求并等待完成，返回最终状态。</summary>
    public MaaStatus Swipe(int x1, int y1, int x2, int y2, int durationMs)
    {
        long id = MaaFrameworkApi.MaaControllerPostSwipe(_handle, x1, y1, x2, y2, durationMs);
        if (id == 0)
        {
            return MaaStatus.Invalid;
        }

        return (MaaStatus)MaaFrameworkApi.MaaControllerWait(_handle, id);
    }

    /// <summary>
    /// 启动应用（P3.5）。<b>Win32 控制器没有实现这个能力</b>：桌面端由用户自己开游戏，
    /// 脚本不去启动进程，所以这里只会返回 <see cref="MaaStatus.Invalid"/> 或 failed。
    /// 保留方法是为了让调用方不必按控制器类型分支（渠道包名只在 Adb 下有意义）。
    /// </summary>
    public MaaStatus StartApp(string? intent) => MaaControllerApps.StartApp(_handle, intent);

    /// <summary>关闭应用（P3.5）。同上：Win32 控制器不支持。</summary>
    public MaaStatus StopApp(string? package) => MaaControllerApps.StopApp(_handle, package);

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

/// <summary>截图结果信息。</summary>
/// <param name="Width">截图宽度（可能因缩放与原始分辨率不同）。</param>
/// <param name="Height">截图高度。</param>
/// <param name="Channels">通道数（PrintWindow 为 3，BGR）。</param>
/// <param name="CvType">OpenCV 类型（16 = CV_8UC3）。</param>
/// <param name="PixelBytes">像素数据字节数。</param>
/// <param name="AverageBrightness">平均亮度（0-255，全部通道字节均值）。</param>
/// <param name="NonBlackRatio">非黑像素比例（0-1，任一通道 &gt; 8 视为非黑）。</param>
/// <param name="BlackBorderRight">右侧纯黑边框宽度（像素；0 表示无）。</param>
/// <param name="BlackBorderBottom">底部纯黑边框高度（像素；0 表示无）。</param>
public sealed record ScreenshotResult(
    int Width, int Height, int Channels, int CvType, long PixelBytes,
    double AverageBrightness, double NonBlackRatio, int BlackBorderRight, int BlackBorderBottom)
{
    /// <summary>
    /// 是否存在严重黑边（任一边超过对应尺寸的 10%）。
    /// 严重时截图未覆盖完整客户区，框架按「截图尺寸:客户区尺寸」映射的点击坐标会整体偏移。
    /// </summary>
    public bool HasSevereBlackBorder =>
        Width > 0 && Height > 0 &&
        (BlackBorderRight > Width * 0.10 || BlackBorderBottom > Height * 0.10);
}

/// <summary>
/// PI V2 界面配置中 Win32 方法名称（interface.json 的 mouse/keyboard/screencap 字符串）
/// 到 MaaFramework 位掩码的映射。名称对齐 MaaWin32ControlUnit 的方法命名。
/// </summary>
public static class MaaWin32MethodNames
{
    /// <summary>PI 协议里合法的截图方式名（interface.json 的 <c>controller[].win32.screencap</c>）。</summary>
    public static readonly string[] ScreencapNames =
        ["FramePool", "PrintWindow", "GDI", "DXGI_DesktopDup", "DXGI_DesktopDup_Window", "ScreenDC", "Background", "Foreground"];

    /// <summary>PI 协议里合法的输入方式名（<c>win32.mouse</c> / <c>win32.keyboard</c>）。</summary>
    public static readonly string[] InputNames =
        ["SendMessage", "PostMessage", "Seize", "LegacyEvent", "SendMessageWithCursorPos",
         "PostMessageWithCursorPos", "SendMessageWithWindowPos", "PostMessageWithWindowPos",
         "Interception", "AnchoredTouch"];

    /// <summary>
    /// 截图方式名是否合法。写错名字在 MaaFramework 里是<b>静默回退</b>（落到 PrintWindow 而不是报错），
    /// 行为悄悄就变了，所以 P3.6 的 schema 校验必须拦住。
    /// </summary>
    public static bool IsKnownScreencap(string name)
        => ScreencapNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>输入方式名是否合法（同 <see cref="IsKnownScreencap"/> 的理由）。</summary>
    public static bool IsKnownInput(string name)
        => InputNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    public static ulong ParseScreencap(string? name) => name?.Trim() switch
    {
        null or "" => MaaWin32ScreencapMethod.FramePool,
        "PrintWindow" => MaaWin32ScreencapMethod.PrintWindow,
        "GDI" or "Gdi" => MaaWin32ScreencapMethod.Gdi,
        "FramePool" => MaaWin32ScreencapMethod.FramePool,
        "DXGI_DesktopDup" => MaaWin32ScreencapMethod.DxgiDesktopDup,
        "DXGI_DesktopDup_Window" => MaaWin32ScreencapMethod.DxgiDesktopDupWindow,
        "ScreenDC" => MaaWin32ScreencapMethod.ScreenDc,
        "Background" => MaaWin32ScreencapMethod.Background,
        "Foreground" => MaaWin32ScreencapMethod.Foreground,
        _ => MaaWin32ScreencapMethod.PrintWindow,
    };

    /// <summary>截图方法位掩码 → 名称（诊断输出用）。</summary>
    public static string ScreencapName(ulong method) => method switch
    {
        MaaWin32ScreencapMethod.FramePool => "FramePool",
        MaaWin32ScreencapMethod.PrintWindow => "PrintWindow",
        MaaWin32ScreencapMethod.Gdi => "GDI",
        MaaWin32ScreencapMethod.DxgiDesktopDup => "DXGI_DesktopDup",
        MaaWin32ScreencapMethod.DxgiDesktopDupWindow => "DXGI_DesktopDup_Window",
        MaaWin32ScreencapMethod.ScreenDc => "ScreenDC",
        MaaWin32ScreencapMethod.Background => "Background",
        MaaWin32ScreencapMethod.Foreground => "Foreground",
        MaaWin32ScreencapMethod.None => "None",
        _ => $"0x{method:X}",
    };

    /// <summary>输入方法名称 → 位掩码。</summary>
    public static ulong ParseInput(string? name) => name?.Trim() switch
    {
        null or "" => MaaWin32InputMethod.SendMessage,
        "SendMessage" => MaaWin32InputMethod.SendMessage,
        "PostMessage" => MaaWin32InputMethod.PostMessage,
        "Seize" => MaaWin32InputMethod.Seize,
        "LegacyEvent" => MaaWin32InputMethod.LegacyEvent,
        "SendMessageWithCursorPos" => MaaWin32InputMethod.SendMessageWithCursorPos,
        "PostMessageWithCursorPos" => MaaWin32InputMethod.PostMessageWithCursorPos,
        "SendMessageWithWindowPos" => MaaWin32InputMethod.SendMessageWithWindowPos,
        "PostMessageWithWindowPos" => MaaWin32InputMethod.PostMessageWithWindowPos,
        "Interception" => MaaWin32InputMethod.Interception,
        "AnchoredTouch" => MaaWin32InputMethod.AnchoredTouch,
        _ => MaaWin32InputMethod.SendMessage,
    };
}
