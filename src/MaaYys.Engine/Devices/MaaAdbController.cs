using System.Runtime.InteropServices;
using MaaYys.Engine.Native;

namespace MaaYys.Engine.Devices;

/// <summary>
/// ADB 控制器托管封装（MaaAdbControllerCreate，P2.3）。
/// 截图/点击映射逻辑与 Win32 控制器共用 <see cref="ControllerCapture"/>；
/// 方法位掩码由设备能力决定（MaaToolkitAdbDeviceGetScreencapMethods / GetInputMethods）。
/// </summary>
public sealed class MaaAdbController : IDisposable
{
    private readonly MaaControllerHandle _handle;
    private long _frameSequence;
    private bool _disposed;

    private MaaAdbController(MaaControllerHandle handle) => _handle = handle;

    /// <summary>
    /// 创建 ADB 控制器。
    /// </summary>
    /// <param name="adbPath">adb 可执行文件绝对路径。</param>
    /// <param name="address">设备连接地址（如 127.0.0.1:16384）。</param>
    /// <param name="screencapMethods">截图方法位掩码。</param>
    /// <param name="inputMethods">输入方法位掩码。</param>
    /// <param name="config">设备配置 JSON（模拟器预设携带，可空）。</param>
    /// <param name="agentPath">MaaAgentBinary 目录（可空，框架内置默认）。</param>
    public static MaaAdbController Create(
        string adbPath,
        string address,
        ulong screencapMethods = (ulong)MaaAdbScreencapMethod.Default,
        ulong inputMethods = (ulong)MaaAdbInputMethod.Default,
        string? config = null,
        string? agentPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        IntPtr adbPtr = MaaFramework.ToUtf8Native(adbPath);
        IntPtr addressPtr = MaaFramework.ToUtf8Native(address);
        IntPtr configPtr = config is null ? IntPtr.Zero : MaaFramework.ToUtf8Native(config);
        IntPtr agentPtr = agentPath is null ? IntPtr.Zero : MaaFramework.ToUtf8Native(agentPath);
        try
        {
            IntPtr ptr = MaaFrameworkApi.MaaAdbControllerCreate(
                adbPtr, addressPtr, screencapMethods, inputMethods, configPtr, agentPtr);
            if (ptr == IntPtr.Zero)
            {
                throw new InvalidOperationException($"MaaAdbControllerCreate 返回空句柄（{adbPath} @ {address}）");
            }

            return new MaaAdbController(new MaaControllerHandle(ptr));
        }
        finally
        {
            Marshal.FreeCoTaskMem(adbPtr);
            Marshal.FreeCoTaskMem(addressPtr);
            if (configPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(configPtr);
            }

            if (agentPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(agentPtr);
            }
        }
    }

    /// <summary>原生句柄（仅引擎层内部使用）。</summary>
    internal IntPtr Handle => _handle.DangerousGetHandle();

    public MaaStatus Connect()
    {
        long id = MaaFrameworkApi.MaaControllerPostConnection(_handle);
        return id == 0 ? MaaStatus.Invalid : (MaaStatus)MaaFrameworkApi.MaaControllerWait(_handle, id);
    }

    public bool Connected => MaaFrameworkApi.MaaControllerConnected(_handle) != MaaDef.False;

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

    /// <summary>截图并返回统计信息（不落盘）。</summary>
    public ScreenshotResult? Capture() => ControllerCapture.Capture(_handle, out _);

    /// <summary>截图并保存为 BMP（排障用）。</summary>
    public ScreenshotResult? SaveScreenshot(string path)
    {
        ScreenshotResult? result = ControllerCapture.Capture(_handle, out byte[]? pixels);
        if (result is null || pixels is null)
        {
            return null;
        }

        BmpEncoder.Save(path, pixels, result.Width, result.Height, result.Channels);
        return result;
    }

    /// <summary>截图并返回带原始像素的帧（实时预览用）。</summary>
    public FrameData? CaptureFrame()
    {
        ScreenshotResult? stats = ControllerCapture.Capture(_handle, out byte[]? pixels);
        if (stats is null || pixels is null)
        {
            return null;
        }

        return new FrameData(
            pixels, stats.Width, stats.Height, stats.Channels,
            Interlocked.Increment(ref _frameSequence), DateTimeOffset.Now, stats);
    }

    public MaaStatus Click(int x, int y)
    {
        long id = MaaFrameworkApi.MaaControllerPostClick(_handle, x, y);
        return id == 0 ? MaaStatus.Invalid : (MaaStatus)MaaFrameworkApi.MaaControllerWait(_handle, id);
    }

    public MaaStatus Swipe(int x1, int y1, int x2, int y2, int durationMs)
    {
        long id = MaaFrameworkApi.MaaControllerPostSwipe(_handle, x1, y1, x2, y2, durationMs);
        return id == 0 ? MaaStatus.Invalid : (MaaStatus)MaaFrameworkApi.MaaControllerWait(_handle, id);
    }

    /// <summary>
    /// 启动应用（P3.5 渠道服）。intent 由渠道层的 <c>package</c> 提供，可以是
    /// <c>包名</c> 或 <c>包名/Activity</c>；Adb 控制器走 <c>am start</c>。
    /// </summary>
    public MaaStatus StartApp(string? intent) => MaaControllerApps.StartApp(_handle, intent);

    /// <summary>
    /// 关闭应用（P3.5 渠道服）。必须是<b>纯包名</b>：走的是 <c>am force-stop</c>，带 activity 会失败。
    /// </summary>
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
