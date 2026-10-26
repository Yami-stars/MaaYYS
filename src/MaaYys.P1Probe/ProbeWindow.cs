using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MaaYys.P1Probe;

/// <summary>
/// GUI 窗口行为诊断（P2.5 排查「最小化后无法从任务栏恢复」）。
/// 用法：<c>window [--process MaaYys] [--rounds 8]</c>
///
/// 复现步骤与真实用户操作一致：最小化目标窗口 → 模拟点击任务栏恢复（SW_RESTORE + 前台）
/// → 连续采样「前台窗口 / IsIconic / UI 线程响应」，判定是「没恢复」「恢复了但被抢前台」还是「UI 线程卡死」。
/// </summary>
internal static class ProbeWindow
{
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;

    public static int Run(string[] args)
    {
        string processName = ReadOption(args, "--process") ?? "MaaYys";
        int rounds = int.TryParse(ReadOption(args, "--rounds"), out int r) ? Math.Clamp(r, 3, 40) : 8;
        int holdSeconds = int.TryParse(ReadOption(args, "--hold"), out int h) ? Math.Clamp(h, 0, 300) : 0;
        int watchSeconds = int.TryParse(ReadOption(args, "--watch"), out int w) ? Math.Clamp(w, 0, 300) : 0;

        Process? process = Process.GetProcessesByName(processName)
            .Where(p => p.MainWindowHandle != IntPtr.Zero)
            .OrderByDescending(p => p.StartTime)
            .FirstOrDefault();
        if (process is null)
        {
            Console.WriteLine($"[失败] 未找到带窗口的进程 {processName}");
            return 1;
        }

        IntPtr hwnd = process.MainWindowHandle;
        Console.WriteLine($"[目标] pid={process.Id} hwnd=0x{hwnd:X} title=\"{GetTitle(hwnd)}\"");

        // 只做前台采样（不最小化），用于确认「谁在周期性抢前台」
        if (watchSeconds > 0)
        {
            Console.WriteLine($"[操作] 先激活目标窗口，随后观察 {watchSeconds}s（每 200ms 采样）");
            ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
            Thread.Sleep(300);
            var hits = new Dictionary<string, int>();
            int samples = 0;
            bool prevSelf = GetForegroundWindow() == hwnd;
            for (int i = 0; i < watchSeconds * 5; i++)
            {
                Thread.Sleep(200);
                samples++;
                IntPtr fg = GetForegroundWindow();
                bool self = fg == hwnd;
                if (!self)
                {
                    string key = Describe(fg);
                    hits[key] = hits.TryGetValue(key, out int c) ? c + 1 : 1;
                    if (prevSelf)
                    {
                        Console.WriteLine($"  +{i * 200}ms  前台丢失 → {key}");
                    }
                }
                else if (!prevSelf)
                {
                    Console.WriteLine($"  +{i * 200}ms  前台回到自身");
                }

                prevSelf = self;
            }

            Console.WriteLine();
            Console.WriteLine($"采样 {samples} 次，前台不属于本窗口：" +
                              $"{(hits.Count == 0 ? 0 : hits.Values.Sum())} 次");
            foreach (KeyValuePair<string, int> kv in hits.OrderByDescending(k => k.Value))
            {
                Console.WriteLine($"  {kv.Value,4} 次  {kv.Key}");
            }

            return hits.Count == 0 ? 0 : 3;
        }

        Console.WriteLine();
        Console.WriteLine("=== 进程内顶层窗口清单（找隐藏/幽灵窗口） ===");
        DumpProcessWindows(process.Id);

        Console.WriteLine();
        Console.WriteLine("=== 基线 ===");
        DumpState(hwnd, "基线");

        Console.WriteLine();
        Console.WriteLine($"[操作] 最小化 → 等待 2s");
        ShowWindow(hwnd, SwMinimize);
        Thread.Sleep(2000);
        DumpState(hwnd, "最小化后");

        if (holdSeconds > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"[观察] 保持最小化 {holdSeconds}s，每秒采样 UI 响应/内存/CPU");
            ObserveProcess(process, hwnd, holdSeconds);
        }

        Console.WriteLine();
        Console.WriteLine("[操作] 模拟点击任务栏图标恢复（SW_RESTORE + SetForegroundWindow）");
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);

        bool restoredVisible = false;
        bool foregroundHeld = true;
        List<string> thieves = [];
        for (int i = 0; i < rounds; i++)
        {
            Thread.Sleep(250);
            bool iconic = IsIconic(hwnd);
            bool visible = IsWindowVisible(hwnd);
            IntPtr fg = GetForegroundWindow();
            bool isSelf = fg == hwnd;
            restoredVisible |= !iconic && visible;
            if (!isSelf)
            {
                foregroundHeld = false;
                string desc = Describe(fg);
                if (!thieves.Contains(desc))
                {
                    thieves.Add(desc);
                }
            }

            Console.WriteLine(
                $"  +{(i + 1) * 250,4}ms  iconic={iconic,-5} visible={visible,-5} " +
                $"foreground={(isSelf ? "自身" : Describe(fg))}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 结论 ===");
        Console.WriteLine($"窗口已恢复显示（非最小化且可见）：{restoredVisible}");
        Console.WriteLine($"恢复后前台保持为自身：{foregroundHeld}");
        if (thieves.Count > 0)
        {
            Console.WriteLine("抢占前台 / 前台不属于本窗口的情形：");
            foreach (string t in thieves)
            {
                Console.WriteLine($"  - {t}");
            }
        }

        Console.WriteLine($"UI 线程响应：{MeasureUiResponse(hwnd)}ms（>1000ms 视为卡顿）");
        Console.WriteLine($"IsHungAppWindow：{IsHungAppWindow(hwnd)}");

        // 恢复后的稳态开销：预览泵是否重新跑起来、CPU 是否被拖满
        int afterSeconds = int.TryParse(ReadOption(args, "--after"), out int a) ? Math.Clamp(a, 0, 60) : 5;
        if (afterSeconds > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"[观察] 恢复后 {afterSeconds}s 稳态开销");
            ObserveProcess(process, hwnd, afterSeconds);
        }

        bool ok = restoredVisible && foregroundHeld;
        Console.WriteLine();
        Console.WriteLine(ok
            ? "=== 窗口恢复行为正常 ==="
            : "=== 复现异常：恢复后窗口不可见或前台被抢占 ===");

        // 恢复后的 z-order 快照：确认目标窗口上方是否压着置顶（TopMost）窗口
        // —— 「任务栏点击后窗口没出现」经常是被别的 TopMost 窗口盖住，而不是没恢复。
        Console.WriteLine();
        Console.WriteLine("=== 恢复后可见顶层窗口（按 z-order 从上到下，最多 8 个） ===");
        DumpZOrder(hwnd, 8);

        // 恢复后把窗口内容抓成 BMP：确认「窗口框架回来了，但内容区没绘制」这类问题
        string? shotPath = ReadOption(args, "--shot");
        if (shotPath is not null)
        {
            Thread.Sleep(1200);
            Console.WriteLine();
            Console.WriteLine($"=== 恢复后窗口内容抓图 → {shotPath} ===");
            Console.WriteLine(SaveWindowShot(hwnd, shotPath));
        }

        return ok ? 0 : 2;
    }

    /// <summary>
    /// 最小化 → 恢复压力测试（P2.5「恢复后只有空框」的量化验证）。
    /// 用法：<c>minimize-loop [--process MaaYys] [--cycles 20] [--gap 900] [--settle 1000] [--recheck 1500]</c>
    ///
    /// 每轮：最小化 → 停留 gap → 恢复并置前 → 等 settle → PrintWindow 判定客户区内容是否真的画出来了
    /// → 再等 recheck 复检一次。据此区分三类异常：
    ///   · 首采空白、复检正常 → 恢复慢（渲染表面重建延迟）；
    ///   · 首采与复检都空白   → 表面重建失败，即用户看到的「窗口在、只有一个空框」；
    ///   · 未恢复 / 前台丢失   → 窗口根本没被拉起来。
    /// </summary>
    public static int RunMinimizeLoop(string[] args)
    {
        string processName = ReadOption(args, "--process") ?? "MaaYys";
        int cycles = int.TryParse(ReadOption(args, "--cycles"), out int c) ? Math.Clamp(c, 1, 200) : 20;
        int gap = int.TryParse(ReadOption(args, "--gap"), out int g) ? Math.Clamp(g, 100, 20000) : 900;
        int settle = int.TryParse(ReadOption(args, "--settle"), out int s) ? Math.Clamp(s, 100, 20000) : 1000;
        int recheck = int.TryParse(ReadOption(args, "--recheck"), out int r) ? Math.Clamp(r, 0, 20000) : 1500;
        string shotDir = ReadOption(args, "--shot-dir") ?? "debug/minloop";

        Process? process = Process.GetProcessesByName(processName)
            .Where(p => p.MainWindowHandle != IntPtr.Zero)
            .OrderByDescending(p => p.StartTime)
            .FirstOrDefault();
        if (process is null)
        {
            Console.WriteLine($"[失败] 未找到带窗口的进程 {processName}");
            return 1;
        }

        IntPtr hwnd = process.MainWindowHandle;
        Console.WriteLine($"[目标] pid={process.Id} hwnd=0x{hwnd:X} title=\"{GetTitle(hwnd)}\"");
        Console.WriteLine(
            $"[参数] 轮次 {cycles} | 最小化停留 {gap}ms | 恢复等待 {settle}ms | 复检等待 {recheck}ms");

        // 先确保从可见状态开始
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
        Thread.Sleep(600);
        WindowSample baseline = SampleWindow(hwnd, null);
        Console.WriteLine($"[基线] {baseline.Describe()}");
        if (baseline.IsBlank)
        {
            Console.WriteLine("[提示] 基线本身就是空白，先人工确认窗口内容正常再跑压力测试");
        }

        int slow = 0;
        int stuck = 0;
        int notRestored = 0;
        int foregroundLost = 0;
        var elapsed = Stopwatch.StartNew();

        for (int i = 1; i <= cycles; i++)
        {
            ShowWindow(hwnd, SwMinimize);
            Thread.Sleep(gap);

            ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
            Thread.Sleep(settle);

            bool iconic = IsIconic(hwnd);
            bool visible = IsWindowVisible(hwnd);
            bool self = GetForegroundWindow() == hwnd;
            WindowSample first = SampleWindow(hwnd, null);
            bool restored = !iconic && visible;

            if (!restored)
            {
                notRestored++;
            }

            if (!self)
            {
                foregroundLost++;
            }

            string verdict;
            if (!restored)
            {
                verdict = "✗ 未恢复";
            }
            else if (first.IsBlank)
            {
                // 首采空白：先落盘留证，再等一会儿复检，判断是「慢」还是「卡死」
                string path = Path.Combine(shotDir, $"blank-{i:D2}.bmp");
                SampleWindow(hwnd, path);
                Thread.Sleep(recheck);
                WindowSample again = SampleWindow(hwnd, null);
                if (again.IsBlank)
                {
                    stuck++;
                    verdict = $"✗ 空白且复检仍空（{again.Describe()}）→ 表面重建失败";
                }
                else
                {
                    slow++;
                    verdict = $"△ 首采空白，{recheck}ms 后恢复（慢）";
                }
            }
            else
            {
                verdict = "✓ 正常";
            }

            Console.WriteLine(
                $"[{i,3}/{cycles}] iconic={iconic,-5} visible={visible,-5} " +
                $"fg={(self ? "自身" : "他人")} | {first.Describe()} | {verdict}");
        }

        elapsed.Stop();

        Console.WriteLine();
        Console.WriteLine("=== 汇总 ===");
        Console.WriteLine($"轮次 {cycles}（耗时 {elapsed.Elapsed.TotalSeconds:F1}s）");
        Console.WriteLine($"  正常           {cycles - slow - stuck - notRestored,3}");
        Console.WriteLine($"  恢复慢（首采空） {slow,3}  ← 渲染表面重建延迟");
        Console.WriteLine($"  空白且复检仍空   {stuck,3}  ← 用户可见缺陷「只有空框」");
        Console.WriteLine($"  未恢复（仍最小化）{notRestored,3}");
        Console.WriteLine($"  前台被他人占用   {foregroundLost,3}");
        if (slow + stuck > 0)
        {
            Console.WriteLine($"  首个空白帧截图 → {Path.GetFullPath(shotDir)}");
        }

        bool pass = stuck == 0 && notRestored == 0;
        Console.WriteLine();
        Console.WriteLine(pass
            ? "=== 通过：没有出现「恢复后内容空白」 ==="
            : "=== 失败：出现恢复后内容空白 / 未恢复 ===");
        return pass ? 0 : 2;
    }

    /// <summary>窗口内容采样结果。</summary>
    private readonly record struct WindowSample(
        bool Ok, int Width, int Height, double Mean, double Variance, double NonDarkPercent)
    {
        /// <summary>
        /// 空白判定：抓取成功、几乎全黑、方差趋近 0。
        /// 正常界面（暗色主题）非暗像素占比通常在 60% 以上、方差数千；
        /// 表面没画出来时 PrintWindow 拿到的是全 0。
        /// </summary>
        public bool IsBlank => Ok && NonDarkPercent < 3.0 && Variance < 40.0;

        public string Describe() => Ok
            ? $"{Width}×{Height}；平均亮度 {Mean:F1}；亮度方差 {Variance:F0}；" +
              $"非暗像素 {NonDarkPercent:F1}%"
            : "PrintWindow 抓取失败（无法判定）";
    }

    /// <summary>
    /// 用 PrintWindow(PW_RENDERFULLCONTENT) 抓窗口内容并保存为 24 位 BMP，返回内容统计。
    /// WPF 窗口渲染在 D3D 上，普通 BitBlt 抓到的是空白；PW_RENDERFULLCONTENT 才会走 DWM 合成结果。
    /// 统计项用于判断「窗口恢复了但内容没画出来」：内容空白时色彩方差极小、非暗比例异常低。
    /// </summary>
    private static string SaveWindowShot(IntPtr hwnd, string path) => SampleWindow(hwnd, path).Describe();

    /// <summary>抓窗口内容并统计（<paramref name="bmpPath"/> 非空时另存 BMP）。</summary>
    private static WindowSample SampleWindow(IntPtr hwnd, string? bmpPath)
    {
        if (!GetWindowRect(hwnd, out Win32Diag.RECT rect))
        {
            return new WindowSample(false, 0, 0, 0, 0, 0);
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return new WindowSample(false, width, height, 0, 0, 0);
        }

        IntPtr windowDc = GetWindowDC(hwnd);
        IntPtr memDc = CreateCompatibleDC(windowDc);
        IntPtr bitmap = CreateCompatibleBitmap(windowDc, width, height);
        IntPtr previous = SelectObject(memDc, bitmap);
        bool printed = PrintWindow(hwnd, memDc, PwRenderFullContent);

        var header = new BitmapInfoHeader
        {
            biSize = Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = width,
            biHeight = height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        byte[] pixels = new byte[width * height * 4];
        int lines = GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref header, 0);

        SelectObject(memDc, previous);
        DeleteObject(bitmap);
        DeleteDC(memDc);
        ReleaseDC(hwnd, windowDc);

        if (!printed || lines == 0)
        {
            return new WindowSample(false, width, height, 0, 0, 0);
        }

        // 统计：非暗比例 + 颜色方差（空白窗口方差极小；有画面时方差明显偏大）
        int nonBlack = 0;
        long sum = 0;
        long sumSq = 0;
        long total = (long)width * height;
        for (int i = 0; i + 4 <= pixels.Length; i += 4)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            int luma = (r * 299 + g * 587 + b * 114) / 1000;
            sum += luma;
            sumSq += (long)luma * luma;
            if (luma > 12)
            {
                nonBlack++;
            }
        }

        double mean = (double)sum / total;
        double variance = Math.Max(0, (double)sumSq / total - mean * mean);
        double nonDarkPercent = 100.0 * nonBlack / total;

        if (bmpPath is not null)
        {
            WriteBmp24(bmpPath, pixels, width, height);
        }

        return new WindowSample(true, width, height, mean, variance, nonDarkPercent);
    }

    /// <summary>把 32 位 BGRA 自上而下像素写成 24 位 BMP（BMP 行序自下而上，每行 4 字节对齐）。</summary>
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
        // GetDIBits 传正数 biHeight 时返回的像素本身就是自下而上（首行是最底行），
        // 与 BMP 存储顺序一致，直接顺序写出即可，不能再翻转一次。
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

    private static void DumpZOrder(IntPtr target, int max)
    {
        int index = 0;
        bool found = false;
        EnumWindows(
            (h, _) =>
            {
                if (!IsWindowVisible(h) || GetTitle(h).Length == 0)
                {
                    return true;
                }

                index++;
                bool topMost = (GetWindowLongPtr(h, GwlExStyle) & WsExTopmost) != 0;
                string mark = h == target ? "  <<< 目标窗口" : string.Empty;
                Console.WriteLine(
                    $"  #{index}  {Describe(h)}{(topMost ? " [置顶]" : string.Empty)}{mark}");
                found |= h == target;
                return index < max;
            },
            IntPtr.Zero);

        if (!found)
        {
            Console.WriteLine("  （目标窗口未出现在前若干层，可能被压在更深层）");
        }
    }

    /// <summary>
    /// 每秒采样：UI 线程响应耗时、工作集、CPU 增量。
    /// 用于验证「最小化挂后台时预览采集是否仍在空转」以及「恢复后 CPU 是否被拖满」。
    /// </summary>
    private static void ObserveProcess(Process process, IntPtr hwnd, int seconds)
    {
        TimeSpan lastCpu = process.TotalProcessorTime;
        for (int i = 1; i <= seconds; i++)
        {
            Thread.Sleep(1000);
            process.Refresh();
            TimeSpan cpu = process.TotalProcessorTime;
            long respond = MeasureUiResponse(hwnd, 500);
            Console.WriteLine(
                $"  +{i,3}s  ui-response={(respond < 0 ? "超时(>500ms)" : respond + "ms"),-14} " +
                $"workingSet={process.WorkingSet64 / 1024 / 1024,5}MB " +
                $"private={(process.PrivateMemorySize64 / 1024 / 1024),5}MB " +
                $"cpu={(cpu - lastCpu).TotalMilliseconds,6:F0}ms/1s " +
                $"hung={IsHungAppWindow(hwnd)}");
            lastCpu = cpu;
        }
    }

    private static void DumpState(IntPtr hwnd, string label)
    {
        Console.WriteLine(
            $"  [{label}] iconic={IsIconic(hwnd)} visible={IsWindowVisible(hwnd)} " +
            $"enabled={IsWindowEnabled(hwnd)} rect={GetRectText(hwnd)}");
        Console.WriteLine($"          前台={Describe(GetForegroundWindow())}");
    }

    private static void DumpProcessWindows(int pid)
    {
        int count = 0;
        EnumWindows(
            (h, _) =>
            {
                GetWindowThreadProcessId(h, out uint owner);
                if (owner != pid)
                {
                    return true;
                }

                count++;
                IntPtr ownerWindow = GetWindow(h, GwOwner);
                Console.WriteLine(
                    $"  hwnd=0x{h:X8} class=\"{GetClass(h)}\" title=\"{GetTitle(h)}\" " +
                    $"visible={IsWindowVisible(h),-5} iconic={IsIconic(h),-5} owner=0x{ownerWindow:X}");
                return true;
            },
            IntPtr.Zero);

        if (count == 0)
        {
            Console.WriteLine("  （未枚举到顶层窗口）");
        }
    }

    private static long MeasureUiResponse(IntPtr hwnd) => MeasureUiResponse(hwnd, 3000);

    private static long MeasureUiResponse(IntPtr hwnd, uint timeoutMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        IntPtr ok = SendMessageTimeout(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, timeoutMs, out _);
        sw.Stop();
        return ok == IntPtr.Zero ? -1 : sw.ElapsedMilliseconds;
    }

    private static string GetRectText(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out Win32Diag.RECT rect))
        {
            return "n/a";
        }

        return $"{rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}";
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "无";
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        string procName = "?";
        try
        {
            procName = Process.GetProcessById((int)pid).ProcessName;
        }
        catch (ArgumentException)
        {
            // 进程已退出
        }

        return $"0x{hwnd:X} \"{GetTitle(hwnd)}\" [{procName}:{pid}]";
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const uint PwRenderFullContent = 0x00000002;

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
        IntPtr hdc, IntPtr bitmap, uint start, uint lines, byte[] bits,
        ref BitmapInfoHeader header, uint usage);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32Diag.RECT rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
