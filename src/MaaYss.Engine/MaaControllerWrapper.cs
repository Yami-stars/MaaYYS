using System.Runtime.InteropServices;
using MaaYss.Engine.Native;

namespace MaaYss.Engine;

/// <summary>
/// Win32 控制器托管封装（MaaWin32ControllerCreate + 通用 Post/Wait 操作 API）。
/// 截图方式默认 PrintWindow（后台可抓硬件加速画面），键鼠默认 SendMessage（后台不抢焦点），
/// 与 MaaYYs interface.json 的 Win32 控制器配置一致。
/// </summary>
public sealed class MaaWin32Controller : IDisposable
{
    private readonly MaaControllerHandle _handle;
    private bool _disposed;

    private MaaWin32Controller(MaaControllerHandle handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// 创建 Win32 控制器。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="screencapMethod">截图方法位掩码（默认 PrintWindow）。</param>
    /// <param name="mouseMethod">鼠标输入方法位掩码（默认 SendMessage）。</param>
    /// <param name="keyboardMethod">键盘输入方法位掩码（默认 SendMessage）。</param>
    public static MaaWin32Controller Create(
        IntPtr hWnd,
        ulong screencapMethod = MaaWin32ScreencapMethod.PrintWindow,
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

    /// <summary>原生句柄（仅供 MaaTasker.BindController 使用）。</summary>
    public IntPtr Handle => _handle.DangerousGetHandle();

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

    /// <summary>
    /// 截图并保存为 24 位 BMP（自包含编码，无外部依赖）。
    /// 返回截图尺寸与统计信息；截图失败返回 null。
    /// </summary>
    public ScreenshotResult? SaveScreenshot(string path)
    {
        MaaStatus status = Screencap();
        if (status != MaaStatus.Succeeded)
        {
            return null;
        }

        MaaImageBufferHandle buffer = new(MaaFrameworkApi.MaaImageBufferCreate());
        using (buffer)
        {
            if (buffer.IsInvalid || MaaFrameworkApi.MaaControllerCachedImage(_handle, buffer) == MaaDef.False)
            {
                return null;
            }

            if (buffer.IsEmpty)
            {
                return null;
            }

            byte[]? pixels = buffer.CopyRawData();
            if (pixels is null)
            {
                return null;
            }

            int width = buffer.Width, height = buffer.Height, channels = buffer.Channels, type = buffer.Type;

            // 统计：平均亮度（全部通道字节均值）与非黑像素比例（任一通道 > 8 视为非黑）。
            // 用途：验证 PrintWindow 后台截图抓到了真实画面而非纯黑/空白图。
            double sum = 0;
            long nonBlack = 0;
            long pixelCount = (long)width * height;
            for (long i = 0; i + channels <= pixels.Length; i += channels)
            {
                bool isBlack = true;
                for (int c = 0; c < channels; c++)
                {
                    byte v = pixels[i + c];
                    sum += v;
                    if (v > 8)
                    {
                        isBlack = false;
                    }
                }

                if (!isBlack)
                {
                    nonBlack++;
                }
            }

            double average = pixels.Length > 0 ? sum / pixels.Length : 0;
            double nonBlackRatio = pixelCount > 0 ? (double)nonBlack / pixelCount : 0;

            BmpEncoder.Save(path, pixels, width, height, channels);
            return new ScreenshotResult(width, height, channels, type, pixels.LongLength, average, nonBlackRatio);
        }
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
public sealed record ScreenshotResult(
    int Width, int Height, int Channels, int CvType, long PixelBytes, double AverageBrightness, double NonBlackRatio);

/// <summary>
/// PI V2 界面配置中 Win32 方法名称（interface.json 的 mouse/keyboard/screencap 字符串）
/// 到 MaaFramework 位掩码的映射。名称对齐 MaaWin32ControlUnit 的方法命名。
/// </summary>
public static class MaaWin32MethodNames
{
    /// <summary>截图方法名称 → 位掩码。</summary>
    public static ulong ParseScreencap(string? name) => name?.Trim() switch
    {
        null or "" => MaaWin32ScreencapMethod.PrintWindow,
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
