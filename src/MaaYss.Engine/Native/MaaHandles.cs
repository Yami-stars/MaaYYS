using System.Runtime.InteropServices;
using MaaYss.Engine.Native;

namespace MaaYss.Engine;

/// <summary>
/// MaaStringBuffer 原生句柄安全封装（MaaStringBufferCreate/Destroy）。
/// 用于读取控制器返回的字符串信息（uuid / info JSON）。
/// </summary>
public sealed class MaaStringBufferHandle : SafeHandle
{
    public MaaStringBufferHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public MaaStringBufferHandle(IntPtr ptr)
        : base(IntPtr.Zero, ownsHandle: true) => SetHandle(ptr);

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>读取缓冲区内容（UTF-8）。空缓冲区返回 null。</summary>
    public string? GetString()
    {
        if (IsInvalid || MaaFrameworkApi.MaaStringBufferIsEmpty(handle) != MaaDef.False)
        {
            return null;
        }

        IntPtr ptr = MaaFrameworkApi.MaaStringBufferGet(handle);
        return MaaFramework.FromUtf8Native(ptr);
    }

    protected override bool ReleaseHandle()
    {
        MaaFrameworkApi.MaaStringBufferDestroy(handle);
        return true;
    }
}

/// <summary>
/// MaaImageBuffer 原生句柄安全封装（MaaImageBufferCreate/Destroy）。
/// 用于接收控制器截图（MaaControllerCachedImage）。
/// </summary>
public sealed class MaaImageBufferHandle : SafeHandle
{
    public MaaImageBufferHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public MaaImageBufferHandle(IntPtr ptr)
        : base(IntPtr.Zero, ownsHandle: true) => SetHandle(ptr);

    public override bool IsInvalid => handle == IntPtr.Zero;

    public bool IsEmpty => MaaFrameworkApi.MaaImageBufferIsEmpty(handle) != MaaDef.False;

    public int Width => MaaFrameworkApi.MaaImageBufferWidth(handle);

    public int Height => MaaFrameworkApi.MaaImageBufferHeight(handle);

    public int Channels => MaaFrameworkApi.MaaImageBufferChannels(handle);

    /// <summary>OpenCV 类型（如 16 = CV_8UC3）。</summary>
    public int Type => MaaFrameworkApi.MaaImageBufferType(handle);

    /// <summary>
    /// 读取 cv::Mat 原始像素（视图，不拷贝；仅在句柄存活期内有效）。
    /// 调用方须按 Width×Height×Channels 立即拷贝。
    /// </summary>
    public IntPtr RawDataView => MaaFrameworkApi.MaaImageBufferGetRawData(handle);

    /// <summary>拷贝原始像素到托管数组；空图返回 null。</summary>
    public byte[]? CopyRawData()
    {
        if (IsInvalid || IsEmpty)
        {
            return null;
        }

        int w = Width, h = Height, c = Channels;
        if (w <= 0 || h <= 0 || c <= 0)
        {
            return null;
        }

        IntPtr src = RawDataView;
        if (src == IntPtr.Zero)
        {
            return null;
        }

        // cv::Mat 行字节可能含对齐填充；按 channels 逐行拷贝以保证数据正确。
        byte[] dst = new byte[w * h * c];
        for (int row = 0; row < h; row++)
        {
            Marshal.Copy(src + (row * w * c), dst, row * w * c, w * c);
        }
        return dst;
    }

    protected override bool ReleaseHandle()
    {
        MaaFrameworkApi.MaaImageBufferDestroy(handle);
        return true;
    }
}

/// <summary>
/// MaaToolkitDesktopWindowList 原生句柄安全封装（MaaToolkitDesktopWindowListCreate/Destroy）。
/// </summary>
public sealed class MaaToolkitDesktopWindowListHandle : SafeHandle
{
    public MaaToolkitDesktopWindowListHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public MaaToolkitDesktopWindowListHandle(IntPtr ptr)
        : base(IntPtr.Zero, ownsHandle: true) => SetHandle(ptr);

    public override bool IsInvalid => handle == IntPtr.Zero;

    public ulong Count => MaaFrameworkApi.MaaToolkitDesktopWindowListSize(this);

    protected override bool ReleaseHandle()
    {
        MaaFrameworkApi.MaaToolkitDesktopWindowListDestroy(handle);
        return true;
    }
}

/// <summary>
/// MaaAgentClient 原生句柄安全封装（MaaAgentClientCreateV2/CreateTcp + Destroy）。
/// 用于连接 Go agent 子进程（MaaAgentServer），把其注册的 custom action/recognition 绑定到本地 Resource。
/// </summary>
public sealed class MaaAgentClientHandle : SafeHandle
{
    public MaaAgentClientHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public MaaAgentClientHandle(IntPtr ptr)
        : base(IntPtr.Zero, ownsHandle: true) => SetHandle(ptr);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        MaaFrameworkApi.MaaAgentClientDestroy(handle);
        return true;
    }
}

/// <summary>
/// MaaStringListBuffer 原生句柄安全封装（MaaStringListBufferCreate/Destroy）。
/// 用于读取 agent 注册的 custom action / recognition 名称列表。
/// 注意：MaaStringListBufferAt 返回的是内部 MaaStringBuffer*（借用指针，不可销毁）。
/// </summary>
public sealed class MaaStringListBufferHandle : SafeHandle
{
    public MaaStringListBufferHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public MaaStringListBufferHandle(IntPtr ptr)
        : base(IntPtr.Zero, ownsHandle: true) => SetHandle(ptr);

    public override bool IsInvalid => handle == IntPtr.Zero;

    public ulong Count => MaaFrameworkApi.MaaStringListBufferSize(handle);

    /// <summary>读取全部字符串（UTF-8）。空列表返回空数组。</summary>
    public string[] ToArray()
    {
        if (IsInvalid)
        {
            return [];
        }

        ulong size = Count;
        var items = new string[size];
        for (ulong i = 0; i < size; i++)
        {
            // At 返回的是借用的 MaaStringBuffer*，需再经 MaaStringBufferGet 取内部 char*。
            IntPtr at = MaaFrameworkApi.MaaStringListBufferAt(handle, i);
            IntPtr str = at == IntPtr.Zero ? IntPtr.Zero : MaaFrameworkApi.MaaStringBufferGet(at);
            items[i] = MaaFramework.FromUtf8Native(str) ?? string.Empty;
        }

        return items;
    }

    protected override bool ReleaseHandle()
    {
        MaaFrameworkApi.MaaStringListBufferDestroy(handle);
        return true;
    }
}

/// <summary>
/// 无格式 24 位 BMP 编码器（自包含，无外部依赖）。
/// 输入为 BGR（3 通道）或 BGRA（4 通道）8 位像素，输出标准 24 位 BMP（行底向上、4 字节对齐）。
/// </summary>
internal static class BmpEncoder
{
    /// <summary>将 BGR/BGRA 原始像素编码为 24 位 BMP 并写入文件。</summary>
    public static void Save(string path, byte[] pixels, int width, int height, int channels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("图像尺寸必须为正数");
        }

        if (channels is not (3 or 4))
        {
            throw new NotSupportedException($"仅支持 3/4 通道 8 位图像，当前 channels={channels}");
        }

        int srcStride = width * channels;
        int dstStride = (width * 3 + 3) & ~3; // 每行按 4 字节对齐
        int pixelDataSize = dstStride * height;
        int fileSize = 14 + 40 + pixelDataSize;

        byte[] bmp = new byte[fileSize];

        // BITMAPFILEHEADER（14 字节）
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes((short)0).CopyTo(bmp, 6);
        BitConverter.GetBytes((short)0).CopyTo(bmp, 8);
        BitConverter.GetBytes(14 + 40).CopyTo(bmp, 10);

        // BITMAPINFOHEADER（40 字节）
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);       // planes
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);      // bit count
        BitConverter.GetBytes(0).CopyTo(bmp, 30);              // BI_RGB
        BitConverter.GetBytes(pixelDataSize).CopyTo(bmp, 34);

        // 像素数据：BMP 行序为底向上，与 cv::Mat（顶向下）相反
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * srcStride;
            int dstRow = 14 + 40 + ((height - 1 - y) * dstStride);
            for (int x = 0; x < width; x++)
            {
                int src = srcRow + (x * channels);
                int dst = dstRow + (x * 3);
                bmp[dst] = pixels[src];         // B
                bmp[dst + 1] = pixels[src + 1]; // G
                bmp[dst + 2] = pixels[src + 2]; // R
            }
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, bmp);
    }
}
