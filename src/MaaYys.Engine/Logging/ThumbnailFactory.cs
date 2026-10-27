using MaaYys.Engine.Devices;

namespace MaaYys.Engine.Logging;

/// <summary>
/// 截图卡片生成（P5.5）：把一帧原始像素等比缩小成缩略图。
/// <para>
/// 用**块平均**而不是最近邻采样：识别区域/血条这类小字在 1/4 缩放下最近邻会糊成噪点，
/// 块平均能保留可辨识的轮廓，代价是每个目标像素读 16 个源像素（1280×720 → 320 宽 ≈ 23 万次读，亚毫秒级）。
/// </para>
/// </summary>
public static class ThumbnailFactory
{
    /// <summary>
    /// 生成缩略图；帧无效或超出支持通道数时返回 null（调用方按「无截图」处理）。
    /// </summary>
    /// <param name="frame">源帧（BGR / BGRA / 灰度）。</param>
    /// <param name="targetWidth">目标宽度（像素）；源图更窄时不放大。</param>
    public static LogThumbnail? Create(FrameData? frame, int targetWidth)
    {
        if (frame is null || frame.Width <= 0 || frame.Height <= 0)
        {
            return null;
        }

        int channels = frame.Channels;
        if (channels is not (1 or 3 or 4))
        {
            return null;
        }

        long needed = (long)frame.Width * frame.Height * channels;
        if (frame.Pixels.Length < needed)
        {
            return null;
        }

        int width = frame.Width;
        int height = frame.Height;

        // 源图不比目标宽：不放大（放大只会让卡片变糊且更占内存）
        if (targetWidth <= 0 || width <= targetWidth)
        {
            byte[] copy = new byte[needed];
            Array.Copy(frame.Pixels, copy, needed);
            return new LogThumbnail(copy, width, height, channels);
        }

        int targetHeight = Math.Max(1, (int)Math.Round((double)height * targetWidth / width));
        var pixels = new byte[targetWidth * targetHeight * channels];

        for (int ty = 0; ty < targetHeight; ty++)
        {
            int sy0 = ty * height / targetHeight;
            int sy1 = Math.Max(sy0 + 1, (ty + 1) * height / targetHeight);

            for (int tx = 0; tx < targetWidth; tx++)
            {
                int sx0 = tx * width / targetWidth;
                int sx1 = Math.Max(sx0 + 1, (tx + 1) * width / targetWidth);
                int count = (sy1 - sy0) * (sx1 - sx0);

                int r = 0, g = 0, b = 0, a = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int rowStart = sy * frame.Stride;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int index = rowStart + (sx * channels);
                        if (channels == 1)
                        {
                            r += frame.Pixels[index];
                        }
                        else
                        {
                            b += frame.Pixels[index];
                            g += frame.Pixels[index + 1];
                            r += frame.Pixels[index + 2];
                            if (channels == 4)
                            {
                                a += frame.Pixels[index + 3];
                            }
                        }
                    }
                }

                int dst = ((ty * targetWidth) + tx) * channels;
                if (channels == 1)
                {
                    pixels[dst] = (byte)(r / count);
                    continue;
                }

                pixels[dst] = (byte)(b / count);
                pixels[dst + 1] = (byte)(g / count);
                pixels[dst + 2] = (byte)(r / count);
                if (channels == 4)
                {
                    pixels[dst + 3] = (byte)(a / count);
                }
            }
        }

        return new LogThumbnail(pixels, targetWidth, targetHeight, channels);
    }
}
