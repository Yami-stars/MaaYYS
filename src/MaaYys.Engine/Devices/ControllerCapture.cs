using MaaYys.Engine.Native;

namespace MaaYys.Engine.Devices;

/// <summary>
/// 控制器截图的公共实现：MaaController 的截图 API 与控制器类型无关（Win32 / Adb 同一套 Post/Wait），
/// 因此抓图、亮度统计、黑边测量统一放这里，避免各控制器重复实现（P2.3 引入 Adb 时抽出）。
/// </summary>
internal static class ControllerCapture
{
    /// <summary>截图一次并返回统计信息；<paramref name="pixelsOut"/> 为 BGR/BGRA 原始像素。</summary>
    public static ScreenshotResult? Capture(MaaControllerHandle handle, out byte[]? pixelsOut)
    {
        pixelsOut = null;

        long id = MaaFrameworkApi.MaaControllerPostScreencap(handle);
        if (id == 0)
        {
            return null;
        }

        if ((MaaStatus)MaaFrameworkApi.MaaControllerWait(handle, id) != MaaStatus.Succeeded)
        {
            return null;
        }

        MaaImageBufferHandle buffer = new(MaaFrameworkApi.MaaImageBufferCreate());
        using (buffer)
        {
            if (buffer.IsInvalid || MaaFrameworkApi.MaaControllerCachedImage(handle, buffer) == MaaDef.False)
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

            // 统计：平均亮度与非黑像素比例（任一通道 > 8 视为非黑）。
            // 用途：验证后台截图抓到了真实画面而非纯黑/空白图。
            // 每 4 个像素采样一次：1280×720 全量统计单帧约 20ms，是实时预览的瓶颈；
            // 采样后比例精度足够（仅用于判空/判黑），P3 识别走框架内部，不依赖此处统计。
            const int StatsPixelStep = 4;
            double sum = 0;
            long nonBlack = 0;
            long sampledPixels = 0;
            for (long i = 0; i + channels <= pixels.Length; i += (long)channels * StatsPixelStep)
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

                sampledPixels++;
                if (!isBlack)
                {
                    nonBlack++;
                }
            }

            double average = sampledPixels > 0 ? sum / (sampledPixels * channels) : 0;
            double nonBlackRatio = sampledPixels > 0 ? (double)nonBlack / sampledPixels : 0;

            // 黑边测量：DPI-unaware 游戏（如阴阳师桌面版在 125% 缩放下）用 PrintWindow 截图时，
            // 只有左上「虚拟客户区」有画面，右侧/底部留下大片纯黑，会导致点击坐标整体偏移。
            var (blackRight, blackBottom) = MeasureBlackBorder(pixels, width, height, channels);

            pixelsOut = pixels;
            return new ScreenshotResult(
                width, height, channels, type, pixels.LongLength,
                average, nonBlackRatio, blackRight, blackBottom);
        }
    }

    /// <summary>
    /// 测量截图右侧/底部的纯黑边框宽度（像素）。
    /// 单列/单行非黑像素比例低于 2% 即视为黑边；最多扫描对应尺寸的一半，避免暗场景退化为全图扫描。
    /// </summary>
    private static (int Right, int Bottom) MeasureBlackBorder(byte[] pixels, int width, int height, int channels)
    {
        const double NonBlackThreshold = 0.02;

        const int BorderScanStep = 2;

        int maxScanW = Math.Max(1, width / 2);
        int right = 0;
        for (int x = width - 1; x >= 0 && right < maxScanW; x--)
        {
            int nonBlack = 0;
            int sampled = 0;
            for (int y = 0; y < height; y += BorderScanStep)
            {
                long idx = ((long)y * width + x) * channels;
                for (int c = 0; c < channels; c++)
                {
                    if (pixels[idx + c] > 8)
                    {
                        nonBlack++;
                        break;
                    }
                }

                sampled++;
            }

            if (sampled > 0 && (double)nonBlack / sampled >= NonBlackThreshold)
            {
                break;
            }

            right++;
        }

        int maxScanH = Math.Max(1, height / 2);
        int bottom = 0;
        for (int y = height - 1; y >= 0 && bottom < maxScanH; y--)
        {
            int nonBlack = 0;
            int sampled = 0;
            for (int x = 0; x < width; x += BorderScanStep)
            {
                long idx = ((long)y * width + x) * channels;
                for (int c = 0; c < channels; c++)
                {
                    if (pixels[idx + c] > 8)
                    {
                        nonBlack++;
                        break;
                    }
                }

                sampled++;
            }

            if (sampled > 0 && (double)nonBlack / sampled >= NonBlackThreshold)
            {
                break;
            }

            bottom++;
        }

        return (right, bottom);
    }
}
