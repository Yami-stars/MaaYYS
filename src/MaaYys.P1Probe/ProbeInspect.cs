using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MaaYys.P1Probe;

/// <summary>
/// 只读窗口状态诊断（P2.5 二次修复：定位「游戏窗口在 MaaYys 里能抓到画面，但桌面上看不到」）。
/// 用法：<c>inspect [--process onmyoji] [--shot out.bmp]</c>
///
/// 只读，不做任何最小化/移动/输入操作 —— 可以对正在运行的游戏窗口直接采样。
/// 关键判定：
///   · 几何 + 是否在显示器可视区域内（在屏幕外 ⇒ 桌面当然看不到）
///   · DWM_CLOAKED（被 DWM 隐藏 ⇒ 不参与合成，桌面看到的是后面的内容）
///   · 扩展样式（LAYERED/TRANSPARENT/NOREDIRECTIONBITMAP 都会让窗口「占位但不显示」）
///   · 窗口中心实际命中的窗口（命中桌面而不是目标 ⇒ 目标窗口并不在那个位置）
/// </summary>
internal static class ProbeInspect
{
    private const int GwlExStyle = -20;
    private const int DwmwaCloaked = 14;
    private const uint GaRoot = 2;

    private const long WsExTransparent = 0x00000020;
    private const long WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwFrame = 0x0400;
    private const uint RdwUpdateNow = 0x0100;

    public static int Run(string[] args)
    {
        string processName = ReadOption(args, "--process") ?? "onmyoji";
        string? shot = ReadOption(args, "--shot");

        Process[] candidates = Process.GetProcessesByName(processName);
        if (candidates.Length == 0)
        {
            Console.WriteLine($"[失败] 未找到进程 {processName}");
            return 1;
        }

        int targets = 0;
        foreach (Process process in candidates.OrderBy(p => p.Id))
        {
            IntPtr hwnd = process.MainWindowHandle;
            Console.WriteLine(
                $"[进程] pid={process.Id} hwnd=0x{hwnd:X} title=\"{GetTitle(hwnd)}\" class=\"{GetClassName(hwnd)}\"");
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            targets++;
            Console.WriteLine("  === 主窗口（MainWindowHandle）===");
            DumpWindow(hwnd, "    ");

            // 关键：游戏常有多层顶层窗口（壳窗口 + 真正渲染窗口）。
            // MaaYys 绑定的是哪一个，决定「预览有画面但桌面看不到」到底是哪层的问题。
            Console.WriteLine("  === 进程内全部顶层窗口 ===");
            bool showAll = args.Contains("--all");
            int skipped = 0;
            foreach ((IntPtr handle, bool visible) in EnumerateTopLevelWindows(process.Id))
            {
                GetWindowRect(handle, out RECT bounds);
                long area = (long)(bounds.Right - bounds.Left) * (bounds.Bottom - bounds.Top);

                // 默认略过不可见的微型窗口（输入法/隐藏辅助窗口），--all 时才全列
                if (!showAll && !visible && area < 40000)
                {
                    skipped++;
                    continue;
                }

                string mark = handle == hwnd ? "  ← MainWindowHandle" : string.Empty;
                Console.WriteLine(
                    $"    [0x{handle:X}] \"{GetTitle(handle)}\" class=\"{GetClassName(handle)}\"" +
                    $" visible={visible}{mark}");
                DumpWindow(handle, "      ");
            }

            if (skipped > 0)
            {
                Console.WriteLine($"    （已略过 {skipped} 个不可见的微型辅助窗口，加 --all 可全列）");
            }

            if (shot is not null)
            {
                string path = candidates.Length > 1
                    ? Path.Combine(
                        Path.GetDirectoryName(Path.GetFullPath(shot)) ?? ".",
                        $"{Path.GetFileNameWithoutExtension(shot)}-{process.Id}.bmp")
                    : shot;
                Console.WriteLine($"  主窗口内容抓图: {SampleWindow(hwnd, path)}");
            }

            if (args.Contains("--minimize"))
            {
                Console.WriteLine("  === 操作：SW_MINIMIZE（复现「最小化 → 控制器恢复」路径）===");
                ShowWindow(hwnd, 6 /* SW_MINIMIZE */);
                Thread.Sleep(600);
                DumpWindow(hwnd, "    ");
            }

            if (args.Contains("--restore"))
            {
                Console.WriteLine("  === 操作：SW_RESTORE（模拟用户点任务栏恢复，不修样式）===");
                ShowWindow(hwnd, 9 /* SW_RESTORE */);
                Thread.Sleep(600);
                DumpWindow(hwnd, "    ");
            }

            if (args.Contains("--break"))
            {
                Console.WriteLine("  === 操作：注入幽灵状态（LAYERED + TRANSPARENT + alpha=0）===");
                long exStyleNow = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
                SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyleNow | WsExLayered | WsExTransparent));
                SetLayeredWindowAttributes(hwnd, 0, 0, LwaAlpha);
                Thread.Sleep(300);
                DumpWindow(hwnd, "    ");
            }

            if (args.Contains("--fix"))
            {
                Console.WriteLine("  === 修复：恢复窗口可见性 ===");
                FixWindow(hwnd);
                DumpWindow(hwnd, "    ");
            }

            Console.WriteLine();
        }

        return targets > 0 ? 0 : 2;
    }

    /// <summary>
    /// 修复「窗口在正确位置、也在渲染，但桌面上完全透明」：把最小化期间被设上的
    /// WS_EX_LAYERED/WS_EX_TRANSPARENT + alpha=0 还原，并强制重绘一帧。
    /// </summary>
    private static void FixWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
        {
            Console.WriteLine("    窗口处于最小化 → SW_RESTORE");
            ShowWindow(hwnd, 9 /* SW_RESTORE */);
            Thread.Sleep(300);
        }

        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((exStyle & WsExLayered) != 0 &&
            GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out _) &&
            alpha == 0)
        {
            Console.WriteLine($"    分层 alpha=0（完全透明）→ 设为 255（不透明）");
            SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        }

        if ((exStyle & WsExTransparent) != 0)
        {
            Console.WriteLine("    移除 WS_EX_TRANSPARENT（点击穿透）");
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle & ~WsExTransparent));
        }

        SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwUpdateNow | RdwFrame);
        Console.WriteLine("    已强制重绘一帧");
    }

    /// <summary>枚举属于指定进程的全部顶层窗口（含不可见窗口）。</summary>
    private static List<(IntPtr Handle, bool Visible)> EnumerateTopLevelWindows(int pid)
    {
        var result = new List<(IntPtr, bool)>();
        EnumWindows(
            (hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner == pid)
                {
                    result.Add((hwnd, IsWindowVisible(hwnd)));
                }

                return true;
            },
            IntPtr.Zero);
        return result;
    }

    private static void DumpWindow(IntPtr hwnd, string indent)
    {
        if (!GetWindowRect(hwnd, out RECT rect))
        {
            Console.WriteLine($"{indent}取窗口矩形失败");
            return;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        Console.WriteLine($"{indent}几何: ({rect.Left},{rect.Top}) - ({rect.Right},{rect.Bottom})  {width}×{height}");
        Console.WriteLine(
            $"{indent}状态: Visible={IsWindowVisible(hwnd)}  Iconic(最小化)={IsIconic(hwnd)}  " +
            $"Zoomed(最大化)={IsZoomed(hwnd)}  Enabled={IsWindowEnabled(hwnd)}");

        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var flags = new List<string>();
        if ((exStyle & 0x00000008) != 0) flags.Add("TOPMOST");
        if ((exStyle & 0x00000020) != 0) flags.Add("TRANSPARENT(点击穿透)");
        if ((exStyle & 0x00000080) != 0) flags.Add("TOOLWINDOW");
        if ((exStyle & 0x00080000) != 0) flags.Add("LAYERED(分层)");
        if ((exStyle & 0x00200000) != 0) flags.Add("NOREDIRECTIONBITMAP(无重定向表面)");
        if ((exStyle & 0x00040000) != 0) flags.Add("APPWINDOW");
        Console.WriteLine($"{indent}扩展样式: 0x{exStyle:X8}  [{(flags.Count == 0 ? "无特殊位" : string.Join(", ", flags))}]");

        if (GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint layerFlags))
        {
            Console.WriteLine($"{indent}分层属性: alpha={alpha} flags=0x{layerFlags:X}（alpha=0 即完全透明）");
        }

        int cloaked = -1;
        int hr = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int));
        Console.WriteLine($"{indent}DWM Cloaked: {(hr == 0 ? cloaked.ToString() : $"取不到（hr=0x{hr:X}）")}" +
                          (cloaked == 1 ? "  ← 被 DWM 隐藏，不参与桌面合成" : string.Empty));

        // 与所在显示器可视区域求交，判断窗口是否整体落在屏幕外
        IntPtr monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            RECT screen = info.rcMonitor;
            int overlapW = Math.Max(0, Math.Min(rect.Right, screen.Right) - Math.Max(rect.Left, screen.Left));
            int overlapH = Math.Max(0, Math.Min(rect.Bottom, screen.Bottom) - Math.Max(rect.Top, screen.Top));
            long area = (long)width * height;
            double ratio = area <= 0 ? 0 : 100.0 * overlapW * overlapH / area;
            Console.WriteLine(
                $"{indent}显示器: ({screen.Left},{screen.Top}) - ({screen.Right},{screen.Bottom})  " +
                $"窗口在屏幕内占比 {ratio:F1}%" + (ratio <= 0 ? "  ← 完全在屏幕外" : string.Empty));
        }

        // 窗口中心点实际命中的顶层窗口：命中桌面说明目标窗口并不在该位置
        int cx = rect.Left + width / 2;
        int cy = rect.Top + height / 2;
        IntPtr hit = WindowFromPoint(new POINT { X = cx, Y = cy });
        IntPtr hitRoot = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GaRoot);
        Console.WriteLine(
            $"{indent}中心点({cx},{cy})命中: 0x{hitRoot:X} \"{GetTitle(hitRoot)}\" class=\"{GetClassName(hitRoot)}\"" +
            (hitRoot == hwnd ? "  ← 就是目标窗口" : "  ← 不是目标窗口"));

        IntPtr foreground = GetForegroundWindow();
        Console.WriteLine(
            $"{indent}当前前台: 0x{foreground:X} \"{GetTitle(foreground)}\"" +
            (foreground == hwnd ? "（目标）" : string.Empty));

        Console.WriteLine($"{indent}z-order: {DescribeZOrder(hwnd, indent)}");
    }

    /// <summary>GetWindow(GW_HWNDPREV) 逐层往上数，返回「上面压了几层可见窗口」。</summary>
    private static string DescribeZOrder(IntPtr hwnd, string indent)
    {
        int above = 0;
        var seen = new List<string>();
        IntPtr current = GetWindow(hwnd, 3 /* GW_HWNDPREV */);
        while (current != IntPtr.Zero && above < 32)
        {
            if (IsWindowVisible(current))
            {
                above++;
                if (seen.Count < 3)
                {
                    seen.Add($"\"{GetTitle(current)}\"");
                }
            }

            current = GetWindow(current, 3);
        }

        return above == 0
            ? "最上层（没有被遮挡）"
            : $"上方有 {above} 个可见窗口，最近三个 {string.Join(" / ", seen)}";
    }

    private static string SampleWindow(IntPtr hwnd, string path)
    {
        if (!GetWindowRect(hwnd, out RECT rect))
        {
            return "取窗口尺寸失败";
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return $"窗口尺寸异常 {width}×{height}";
        }

        IntPtr windowDc = GetWindowDC(hwnd);
        IntPtr memDc = CreateCompatibleDC(windowDc);
        IntPtr bitmap = CreateCompatibleBitmap(windowDc, width, height);
        IntPtr previous = SelectObject(memDc, bitmap);
        bool printed = PrintWindow(hwnd, memDc, 0x00000002 /* PW_RENDERFULLCONTENT */);

        var header = new BitmapInfoHeader
        {
            biSize = Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        var pixels = new byte[width * height * 4];
        int lines = GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref header, 0);

        SelectObject(memDc, previous);
        DeleteObject(bitmap);
        DeleteDC(memDc);
        ReleaseDC(hwnd, windowDc);

        if (!printed || lines == 0)
        {
            return $"PrintWindow 失败（printed={printed} lines={lines}）—— 游戏用 D3D 独占/覆盖层时常见，不代表窗口没画面";
        }

        int lit = 0;
        long sum = 0;
        long square = 0;
        long total = (long)width * height;
        for (int i = 0; i + 4 <= pixels.Length; i += 4)
        {
            int luma = (pixels[i + 2] * 299 + pixels[i + 1] * 587 + pixels[i] * 114) / 1000;
            sum += luma;
            square += (long)luma * luma;
            if (luma > 12)
            {
                lit++;
            }
        }

        double mean = (double)sum / total;
        double variance = Math.Max(0, (double)square / total - mean * mean);
        WriteBmp24(path, pixels, width, height);
        return $"{width}×{height} 平均亮度 {mean:F1} 方差 {variance:F0} 非暗 {100.0 * lit / total:F1}% → {Path.GetFullPath(path)}";
    }

    private static void WriteBmp24(string path, byte[] bgra, int width, int height)
    {
        int rowSize = (width * 3 + 3) / 4 * 4;
        int dataSize = rowSize * height;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + dataSize);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(dataSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        byte[] row = new byte[rowSize];
        for (int y = 0; y < height; y++)
        {
            int src = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                row[x * 3] = bgra[src + x * 4];
                row[x * 3 + 1] = bgra[src + x * 4 + 1];
                row[x * 3 + 2] = bgra[src + x * 4 + 2];
            }

            writer.Write(row);
        }
    }

    private static string GetTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(512);
        GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(256);
        GetClassNameW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RedrawWindow(
        IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(
        IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetWindowTextW(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetClassNameW(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hwnd);

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
        IntPtr hdc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader header, uint usage);
}
