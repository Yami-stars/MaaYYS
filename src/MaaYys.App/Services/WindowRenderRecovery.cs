using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MaaYys.App.Services;

/// <summary>
/// 最小化 → 恢复后的「渲染表面自愈」（P2.5 二次修复）。
///
/// 现象：偶发地从最小化恢复后，窗口框架、位置、z-order 全部正常，但客户区**没有任何像素**，
/// 透过窗口直接看到桌面。必须改变窗口大小或切换窗口才会画出来。
///
/// 根因：WPF 窗口的像素由 DWM 上的重定向表面（redirection surface）承载。最小化时该表面被释放、
/// 渲染线程挂起；恢复时表面是**异步重建**的。只在托管层调用 <c>InvalidateVisual()</c> 属于
/// 「标记脏区」，当渲染线程仍处于挂起态时它不会唤醒设备重建 —— 一击不中就一直空着。
///
/// 对策分三层：
/// <list type="number">
///   <item>Win32 强制重绘：<c>SetWindowPos(SWP_FRAMECHANGED)</c> + <c>RedrawWindow(RDW_UPDATENOW)</c>
///         走 Win32 绘制路径，能真正驱动 HwndSource 重新提交一帧；</item>
///   <item>自愈守卫：恢复后按 ~240ms 采样窗口内容是否真的画出来了（PrintWindow 抓左上角小块），
///         判定为空则做一次 1px 尺寸抖动，强制 DWM 重建表面；</item>
///   <item>（进程级）关闭 Windows 效率模式，见 <see cref="ProcessPowerMode"/>。</item>
/// </list>
/// </summary>
public static class WindowRenderRecovery
{
    // SetWindowPos
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    // RedrawWindow
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwFrame = 0x0400;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;

    // PrintWindow
    private const uint PwRenderFullContent = 0x00000002;

    // GetDIBits
    private const uint DibRgbColors = 0;

    /// <summary>采样区域（窗口左上角），只要覆盖标题栏 + 导航栏即可判定「画没画出来」。</summary>
    private const int SampleWidth = 240;
    private const int SampleHeight = 180;

    /// <summary>每 2 个 tick 检测一次；tick 120ms ⇒ 每 ~240ms 一次。</summary>
    private const int TickMilliseconds = 120;

    /// <summary>守卫最长存活 tick 数（16 × 120ms ≈ 1.9s）。</summary>
    private const int MaxTicks = 16;

    /// <summary>tick 计数到这个值后才开始做内容检测（留出表面重建的时间）。</summary>
    private const int FirstProbeTick = 4;

    private static Window? _window;
    private static IntPtr _hwnd;
    private static DispatcherTimer? _guard;
    private static int _tick;
    private static bool _nudged;
    private static DateTime _startedAt;

    /// <summary>挂到主窗口上（只注册一次清理钩子）。</summary>
    public static void Attach(Window window)
    {
        _window = window;
        SyncHandle(window);
        window.Closed += (_, _) => StopGuard();
    }

    /// <summary>
    /// 开始一轮恢复：立即强制重绘一次，然后交给守卫循环持续收尾。
    /// 由 <see cref="Window"/> 在「从最小化恢复」时调用（也可手动调用于诊断）。
    /// </summary>
    public static void Reanchor(Window window)
    {
        _window = window;
        SyncHandle(window);

        _tick = 0;
        _nudged = false;
        _startedAt = DateTime.Now;
        ForceRedraw();
        Diag($"从最小化恢复：强制重绘（hwnd=0x{_hwnd:X}）");

        _guard ??= new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(TickMilliseconds),
        };
        _guard.Tick -= OnGuardTick;
        _guard.Tick += OnGuardTick;
        _guard.Start();
    }

    private static void SyncHandle(Window window)
    {
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(window).Handle;
        }
    }

    private static void StopGuard()
    {
        _guard?.Stop();
        _tick = 0;
        _nudged = false;
    }

    private static void OnGuardTick(object? sender, EventArgs e)
    {
        _tick++;

        // 前 3 个 tick 连续强制重绘，覆盖「窗口刚恢复、表面还在重建」的窗口期
        if (_tick <= 3)
        {
            ForceRedraw();
        }

        // 之后每 2 个 tick 检测一次内容
        if (_tick >= FirstProbeTick && _tick % 2 == 0)
        {
            bool? painted = HasVisibleContent(_hwnd);
            if (painted == true)
            {
                Diag($"恢复完成：第 {_tick} 次 tick（约 {_tick * TickMilliseconds}ms）内容已绘制" +
                     $"{(_nudged ? "，期间做过 1px 抖动" : string.Empty)}");
                StopGuard();
                return;
            }

            // 判定为空（或无法判定但已过一段时间）：先抖动一次尺寸强制重建表面
            if (painted == false && !_nudged)
            {
                _nudged = true;
                Diag($"第 {_tick} 次 tick 检测到客户区空白 → 触发 1px 尺寸抖动强制重建表面");
                NudgeWindowSize();
                return;
            }
        }

        if (_tick >= MaxTicks)
        {
            Diag($"守卫到期：{MaxTicks * TickMilliseconds}ms 内未确认内容（nudged={_nudged}）");
            StopGuard();
        }
    }

    /// <summary>诊断日志（GUI 子系统无控制台，落文件便于无人值守验证）。</summary>
    private static void Diag(string message)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "render-recovery.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [{(_startedAt == default ? "-" : ((DateTime.Now - _startedAt).TotalMilliseconds).ToString("F0") + "ms")}] {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // 诊断日志写失败不影响主流程
        }
    }

    /// <summary>
    /// 诊断入口：手动触发一次 1px 尺寸抖动，用于验证兜底路径不会破坏窗口尺寸/布局。
    /// 由 <c>--diag-nudge</c> 启动参数调用。
    /// </summary>
    public static void RunNudgeSelfTest(Window window)
    {
        _window = window;
        SyncHandle(window);
        if (!GetWindowRect(_hwnd, out RECT before))
        {
            Diag("自检：取窗口尺寸失败");
            return;
        }

        NudgeWindowSize();
        window.UpdateLayout();
        window.InvalidateVisual();

        GetWindowRect(_hwnd, out RECT after);
        int beforeW = before.Right - before.Left;
        int beforeH = before.Bottom - before.Top;
        int afterW = after.Right - after.Left;
        int afterH = after.Bottom - after.Top;
        Diag($"自检：抖动前 {beforeW}×{beforeH} → 后 {afterW}×{afterH}" +
             $"（{(beforeW == afterW && beforeH == afterH ? "尺寸一致" : "尺寸不一致！")}）" +
             $" | ActualWidth={window.ActualWidth:F1} ActualHeight={window.ActualHeight:F1}");
    }

    /// <summary>Win32 层强制重绘：驱动 HwndSource 重新提交一帧到 DWM。</summary>
    private static void ForceRedraw()
    {
        Window? window = _window;
        if (window is null || _hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            _hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        RedrawWindow(
            _hwnd, IntPtr.Zero, IntPtr.Zero,
            RdwInvalidate | RdwAllChildren | RdwUpdateNow | RdwFrame);

        window.InvalidateVisual();
        window.UpdateLayout();
    }

    /// <summary>
    /// 把窗口尺寸抖 1px 再复原。尺寸变化是 DWM 重建重定向表面的最硬触发条件，
    /// 视觉上只有 1px、几乎不可见。最大化状态下改尺寸会取消最大化，改用 Topmost 翻转代替。
    /// </summary>
    private static void NudgeWindowSize()
    {
        Window? window = _window;
        if (window is null || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!GetWindowRect(_hwnd, out RECT rect))
        {
            return;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (window.WindowState == WindowState.Normal)
        {
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height + 1,
                SwpNoMove | SwpNoZOrder | SwpNoActivate);
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height,
                SwpNoMove | SwpNoZOrder | SwpNoActivate);
        }
        else
        {
            bool topmost = window.Topmost;
            window.Topmost = true;
            window.Topmost = topmost;
            window.InvalidateVisual();
            window.UpdateLayout();
        }
    }

    /// <summary>
    /// 抓窗口左上角一小块判断「客户区是否真的画出了内容」。
    /// 返回 true=有内容，false=空白，null=无法判定（抓取失败）。
    /// </summary>
    private static bool? HasVisibleContent(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect))
        {
            return null;
        }

        int width = Math.Min(rect.Right - rect.Left, SampleWidth);
        int height = Math.Min(rect.Bottom - rect.Top, SampleHeight);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bitmap = CreateCompatibleBitmap(screenDc, width, height);
        IntPtr previous = SelectObject(memDc, bitmap);
        try
        {
            if (!PrintWindow(hwnd, memDc, PwRenderFullContent))
            {
                return null;
            }

            var buffer = new byte[width * height * 4];
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height, // 负值 = top-down，统计与方向无关，省一次翻转
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = 0,
                },
            };

            if (GetDIBits(memDc, bitmap, 0, (uint)height, buffer, ref info, DibRgbColors) == 0)
            {
                return null;
            }

            return Analyze(buffer, width * height);
        }
        finally
        {
            SelectObject(memDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>空白表面抓出来是全黑：非暗像素占比极低、亮度方差趋近 0。</summary>
    private static bool Analyze(byte[] bgra, int pixels)
    {
        int lit = 0;
        long sum = 0;
        long square = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            // BGRA 顺序，系数取整数近似（0.114 / 0.587 / 0.299）
            int luminance = (bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000;
            if (luminance > 12)
            {
                lit++;
            }

            sum += luminance;
            square += luminance * luminance;
        }

        if (pixels <= 0)
        {
            return false;
        }

        double mean = (double)sum / pixels;
        double variance = (double)square / pixels - mean * mean;
        return lit > pixels * 0.03 || variance > 40;
    }

    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RedrawWindow(
        IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);

    #endregion
}
