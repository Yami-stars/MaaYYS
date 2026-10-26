namespace MaaYys.Engine.Devices;

/// <summary>
/// 一帧截图（含原始像素，供 GUI 转位图 / 识别调试）。
/// 像素为 <see cref="Channels"/> 通道的 BGR（3）或 BGRA（4），行优先、无 padding。
/// </summary>
public sealed record FrameData(
    byte[] Pixels,
    int Width,
    int Height,
    int Channels,
    long Sequence,
    DateTimeOffset Timestamp,
    ScreenshotResult Stats)
{
    /// <summary>每行字节数（无 padding，等于 Width × Channels）。</summary>
    public int Stride => Width * Channels;

    /// <summary>本帧的采集耗时（最后一帧无意义，由采集泵另行统计）。</summary>
    public double NonBlackRatio => Stats.NonBlackRatio;

    public bool HasSevereBlackBorder => Stats.HasSevereBlackBorder;
}
