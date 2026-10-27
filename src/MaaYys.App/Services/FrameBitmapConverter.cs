using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaaYys.Engine.Devices;
using MaaYys.Engine.Logging;

namespace MaaYys.App.Services;

/// <summary>
/// 引擎帧（BGR/BGRA 原始像素）→ WPF BitmapSource。
/// 引擎层不依赖 WPF，位图封装只在 GUI 侧做（P2.3）。
/// </summary>
public static class FrameBitmapConverter
{
    /// <summary>转为冻结的位图（一次性用途：截图存档等；像素按帧内 stride 拷贝）。</summary>
    public static BitmapSource ToBitmap(FrameData frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        BitmapSource bitmap = BitmapSource.Create(
            frame.Width, frame.Height, 96, 96, ToPixelFormat(frame.Channels), null, frame.Pixels, frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>日志截图卡片（P5.5）：缩略图是同一套原始像素，只是更小。</summary>
    public static BitmapSource ToBitmap(LogThumbnail thumbnail)
    {
        ArgumentNullException.ThrowIfNull(thumbnail);

        BitmapSource bitmap = BitmapSource.Create(
            thumbnail.Width, thumbnail.Height, 96, 96,
            ToPixelFormat(thumbnail.Channels), null, thumbnail.Pixels, thumbnail.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static PixelFormat ToPixelFormat(int channels) => channels switch
    {
        3 => PixelFormats.Bgr24,
        4 => PixelFormats.Bgra32,
        1 => PixelFormats.Gray8,
        _ => throw new NotSupportedException($"不支持的通道数：{channels}"),
    };
}

/// <summary>
/// 实时预览位图（P2.5）：**复用同一个 <see cref="WriteableBitmap"/>**，每帧原地写入像素。
///
/// 为什么不再用 <see cref="FrameBitmapConverter.ToBitmap"/>：
/// 1280×720 的 Bgr24 帧约 2.7MB，每帧新建 BitmapSource 会在 LOH（大对象堆）反复分配
/// 27MB/s 级别的大对象，导致高频 Gen2 GC（实测最小化挂后台时进程吃 100%~260% CPU，
/// 且 GC 暂停会让任务栏点击恢复窗口时明显卡顿）。原地写入后分配降到 0。
///
/// 只能在创建它的 UI 线程上使用（WriteableBitmap 未冻结）。
/// </summary>
public sealed class PreviewBitmap
{
    private WriteableBitmap? _bitmap;

    /// <summary>当前位图（首次 <see cref="Update"/> 前为 null）。</summary>
    public WriteableBitmap? Bitmap => _bitmap;

    /// <summary>把帧写入位图并返回之；尺寸或像素格式变化时自动重建。</summary>
    public WriteableBitmap Update(FrameData frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        PixelFormat format = FrameBitmapConverter.ToPixelFormat(frame.Channels);

        if (_bitmap is null ||
            _bitmap.PixelWidth != frame.Width ||
            _bitmap.PixelHeight != frame.Height ||
            _bitmap.Format != format)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, format, null);
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
        return _bitmap;
    }

    /// <summary>丢弃位图（断开/停泵时释放其非托管缓冲）。</summary>
    public void Clear() => _bitmap = null;
}
