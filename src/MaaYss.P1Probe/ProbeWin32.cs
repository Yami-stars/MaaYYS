using System.Runtime.InteropServices;
using MaaYss.Engine;
using MaaYss.Engine.Config;
using MaaYss.Engine.Native;

namespace MaaYss.P1Probe;

/// <summary>
/// P1.4 Win32 控制链路验证：连接阴阳师桌面版窗口，验证 PrintWindow 后台截图与 SendMessage 后台输入。
/// 流程表验收标准：窗口枚举命中、截图成功、点击有响应。
/// 安全策略（用户选定 A）：默认仅验证到截图；点击/滑动须显式传参执行，执行前打印坐标。
/// 用法：
///   win32 [interface.json 路径] [--hwnd 0x1234] [--click x,y] [--swipe x1,y1,x2,y2,ms]
///   [--mouse-method name] [--children]
/// mouse-method 可选：SendMessage(默认) / PostMessage / Seize / LegacyEvent /
///   SendMessageWithCursorPos / PostMessageWithCursorPos / SendMessageWithWindowPos / PostMessageWithWindowPos
/// </summary>
internal static class ProbeWin32
{
    private const string DefaultInterfacePath =
        @"D:\YYS\MaaYYs-win32-win-x86_64-v3.13.6-2608192116-MXU\interface.json";

    public static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        Console.WriteLine("=== P1.4 Win32 控制链路验证 ===");

        // ---- 参数解析 ----
        string? interfacePath = null;
        IntPtr forcedHwnd = IntPtr.Zero;
        (int X, int Y)? click = null;
        (int X1, int Y1, int X2, int Y2, int DurationMs)? swipe = null;
        ulong? mouseMethodOverride = null;
        ulong? screencapMethodOverride = null;
        bool listChildren = false;
        (int X, int Y)? rawClick = null;
        (int X, int Y)? screenClick = null;
        (int W, int H)? resize = null;
        bool forceForeground = false;

        foreach (string arg in args.Skip(1))
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                string[] parts = arg.Split('=', 2);
                string flag = parts[0];
                string value = parts.Length > 1 ? parts[1] : string.Empty;
                switch (flag)
                {
                    case "--hwnd":
                        forcedHwnd = new IntPtr(Convert.ToInt64(value, 16));
                        break;
                    case "--click":
                        click = ParsePoint(value);
                        break;
                    case "--swipe":
                        swipe = ParseSwipe(value);
                        break;
                    case "--mouse-method":
                        mouseMethodOverride = MaaWin32MethodNames.ParseInput(value);
                        Console.WriteLine($"[0] 鼠标输入方法覆盖: {value} (0x{mouseMethodOverride:X})");
                        break;
                    case "--screencap-method":
                        screencapMethodOverride = MaaWin32MethodNames.ParseScreencap(value);
                        Console.WriteLine($"[0] 截图方法覆盖: {value} (0x{screencapMethodOverride:X})");
                        break;
                    case "--children":
                        listChildren = true;
                        break;
                    case "--raw-win32-click":
                        rawClick = ParsePoint(value);
                        Console.WriteLine($"[0] 直接 Win32 合成点击（绕过 MaaFramework）: {value}");
                        break;
                    case "--screen-click":
                        screenClick = ParsePoint(value);
                        Console.WriteLine($"[0] 绝对屏幕坐标点击（绕过 MaaFramework 与坐标映射）: {value}");
                        break;
                    case "--resize":
                        resize = ParseSize(value);
                        Console.WriteLine($"[0] 窗口尺寸调整（外框）: {value}");
                        break;
                    case "--foreground":
                        forceForeground = true;
                        break;
                    default:
                        Console.Error.WriteLine($"未知参数: {arg}");
                        return 2;
                }
            }
            else
            {
                interfacePath = arg;
            }
        }

        var path = string.IsNullOrWhiteSpace(interfacePath) ? DefaultInterfacePath : interfacePath;

        // ---- [1] 读取 Win32 控制器配置 ----
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"interface.json 不存在: {path}");
            return 1;
        }

        var errors = new List<string>();
        var config = InterfaceConfig.Load(path, errors);
        var win32Controller = config.Controllers.FirstOrDefault(c => c.Type == "Win32");
        if (win32Controller?.Win32 is null)
        {
            Console.Error.WriteLine("interface.json 中未找到 Win32 控制器配置");
            return 1;
        }

        var w32 = win32Controller.Win32;
        Console.WriteLine($"[1] Win32 控制器配置（来自 {Path.GetFileName(path)}）:");
        Console.WriteLine($"    class_regex  = {w32.ClassRegex}");
        Console.WriteLine($"    window_regex = {w32.WindowRegex}");
        Console.WriteLine($"    mouse={w32.Mouse} keyboard={w32.Keyboard} screencap={w32.Screencap}");
        Console.WriteLine($"    display_short_side = {win32Controller.DisplayShortSide}");

        // ---- [2] 枚举桌面窗口，按正则过滤 ----
        Console.WriteLine("[2] 枚举桌面窗口并匹配 class_regex / window_regex:");
        List<DesktopWindowInfo> matched;
        try
        {
            matched = MaaDesktopWindows.FindAll(w32.ClassRegex, w32.WindowRegex);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"    正则编译失败: {ex.Message}");
            return 1;
        }

        if (matched.Count == 0)
        {
            Console.Error.WriteLine("    未命中任何窗口。桌面可见窗口（前 20 个，供诊断）:");
            foreach (var w in MaaDesktopWindows.FindAll().Take(20))
            {
                Console.WriteLine($"      {w}");
            }

            Console.Error.WriteLine("    提示：确认游戏已启动且窗口标题/类名与正则匹配。");
            return 1;
        }

        Console.WriteLine($"    命中 {matched.Count} 个窗口:");
        foreach (var w in matched)
        {
            Console.WriteLine($"      {w}");
        }

        // ---- [3] 选择目标窗口 ----
        DesktopWindowInfo target;
        if (forcedHwnd != IntPtr.Zero)
        {
            var explicitTarget = matched.FirstOrDefault(w => w.Handle == forcedHwnd);
            if (explicitTarget is null)
            {
                Console.Error.WriteLine($"    --hwnd 0x{forcedHwnd.ToInt64():X} 不在命中列表中，终止（避免误连）");
                return 1;
            }

            target = explicitTarget;
            Console.WriteLine($"[3] 使用 --hwnd 指定窗口: {target}");
        }
        else if (matched.Count == 1)
        {
            target = matched[0];
            Console.WriteLine($"[3] 唯一命中，自动选择: {target}");
        }
        else
        {
            target = matched[0];
            Console.WriteLine($"[3] 多个命中，自动选择第一个（GUI 场景应由用户选择）: {target}");
        }

        // ---- [3.5] 子窗口枚举（诊断：部分游戏输入实际由子窗口处理）----
        if (listChildren)
        {
            Console.WriteLine("[3.5] 枚举目标窗口的子窗口:");
            var children = MaaDesktopWindows.FindChildren(target.Handle);
            if (children.Count == 0)
            {
                Console.WriteLine("    （无子窗口）");
            }
            else
            {
                foreach (var c in children)
                {
                    Console.WriteLine($"      {c}");
                }
            }
        }

        // ---- [3.6] 窗口状态诊断（输入无效时定位窗口层原因）----
        Console.WriteLine("[3.6] 窗口状态诊断:");
        Console.WriteLine($"    IsIconic(最小化)     = {Win32Diag.IsIconic(target.Handle)}");
        Console.WriteLine($"    IsWindowVisible      = {Win32Diag.IsWindowVisible(target.Handle)}");
        Console.WriteLine($"    IsWindowEnabled      = {Win32Diag.IsWindowEnabled(target.Handle)}");
        Console.WriteLine($"    GetForegroundWindow  = 0x{Win32Diag.GetForegroundWindow():X}{(Win32Diag.GetForegroundWindow() == target.Handle ? " (本窗口在前台)" : string.Empty)}");
        if (Win32Diag.GetWindowRect(target.Handle, out Win32Diag.RECT wr) && Win32Diag.GetClientRect(target.Handle, out Win32Diag.RECT cr))
        {
            Console.WriteLine($"    WindowRect  = ({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom})  {wr.Right - wr.Left}x{wr.Bottom - wr.Top}");
            Console.WriteLine($"    ClientRect  = (0,0)-({cr.Right},{cr.Bottom})  {cr.Right}x{cr.Bottom}");
            Console.WriteLine($"    非客户区(边框/标题栏) = {wr.Right - wr.Left - cr.Right} x {wr.Bottom - wr.Top - cr.Bottom}");
        }

        // ---- [3.7] 窗口尺寸调整（验证坐标映射是否随分辨率变化）----
        if (resize is { } rz)
        {
            Console.WriteLine($"[3.7] 调整窗口尺寸 -> {rz.W}x{rz.H}（外框）:");
            int style = Win32Diag.GetWindowLong(target.Handle, Win32Diag.GWL_STYLE);
            Console.WriteLine($"    WS_THICKFRAME(可拉伸) = {((style & unchecked((int)Win32Diag.WS_THICKFRAME)) != 0)}");
            Console.WriteLine($"    WS_MAXIMIZEBOX        = {((style & unchecked((int)Win32Diag.WS_MAXIMIZEBOX)) != 0)}");

            bool ok = Win32Diag.SetWindowPos(
                target.Handle, IntPtr.Zero, wr.Left, wr.Top, rz.W, rz.H,
                Win32Diag.SWP_NOZORDER | Win32Diag.SWP_NOACTIVATE);
            Console.WriteLine($"    SetWindowPos -> {ok}{(ok ? string.Empty : $" (lastError={Marshal.GetLastWin32Error()})")}");

            Thread.Sleep(2000); // 等待游戏重排布局
            if (Win32Diag.GetWindowRect(target.Handle, out Win32Diag.RECT wr2) &&
                Win32Diag.GetClientRect(target.Handle, out Win32Diag.RECT cr2))
            {
                Console.WriteLine($"    调整后 WindowRect = ({wr2.Left},{wr2.Top})-({wr2.Right},{wr2.Bottom})  {wr2.Right - wr2.Left}x{wr2.Bottom - wr2.Top}");
                Console.WriteLine($"    调整后 ClientRect = {cr2.Right}x{cr2.Bottom}");
            }
        }

        // ---- [4] 创建控制器并连接 ----
        Console.WriteLine("[4] 创建 Win32 控制器（截图方法 FramePool 优先，黑边严重时回退 PrintWindow）:");
        MaaWin32Controller controller;
        try
        {
            if (screencapMethodOverride is { } explicitScreencap)
            {
                // 显式指定时不做回退，保证"测哪个方法就是哪个方法"
                controller = MaaWin32Controller.Create(
                    target.Handle,
                    explicitScreencap,
                    mouseMethodOverride ?? MaaWin32MethodNames.ParseInput(w32.Mouse),
                    MaaWin32MethodNames.ParseInput(w32.Keyboard));
                Console.WriteLine($"    截图方法（显式指定，不回退）: {MaaWin32MethodNames.ScreencapName(explicitScreencap)}");
            }
            else
            {
                controller = MaaWin32Controller.CreateWithFallback(
                    target.Handle,
                    out ulong usedScreencap,
                    out string selectionDiag,
                    mouseMethodOverride ?? MaaWin32MethodNames.ParseInput(w32.Mouse),
                    MaaWin32MethodNames.ParseInput(w32.Keyboard),
                    MaaWin32MethodNames.ParseScreencap(w32.Screencap));
                Console.WriteLine($"    截图方法选择过程: {selectionDiag}");
                Console.WriteLine($"    => 实际采用: {MaaWin32MethodNames.ScreencapName(usedScreencap)}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"    创建失败: {ex.Message}");
            return 1;
        }

        using (controller)
        {
            var connectStatus = controller.Connect();
            Console.WriteLine($"    PostConnection -> {connectStatus}");
            if (connectStatus != MaaStatus.Succeeded)
            {
                Console.Error.WriteLine("    连接失败");
                return 1;
            }

            Console.WriteLine($"    Connected = {controller.Connected}");
            Console.WriteLine($"    Uuid      = {controller.Uuid ?? "<null>"}");
            var info = controller.InfoJson;
            Console.WriteLine($"    Info      = {info ?? "<null>"}");

            // ---- [5] 截图（PrintWindow 后台，不抢焦点）----
            Console.WriteLine("[5] 截图验证（PrintWindow 后台截图，不抢焦点）:");
            if (win32Controller.DisplayShortSide is { } shortSide)
            {
                bool setOk = controller.SetScreenshotTargetShortSide(shortSide);
                Console.WriteLine($"    SetOption(ScreenshotTargetShortSide={shortSide}) -> {(setOk ? "OK" : "FAIL")}");
            }

            var shotDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "debug");
            var shotPath = Path.Combine(shotDir, $"win32-screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.bmp");
            var shot = controller.SaveScreenshot(shotPath);
            if (shot is null)
            {
                Console.Error.WriteLine("    截图失败（Screencap 未成功或缓存图为空）");
                return 1;
            }

            Console.WriteLine($"    截图保存: {Path.GetFullPath(shotPath)}");
            Console.WriteLine($"    尺寸: {shot.Width}x{shot.Height} | 通道: {shot.Channels} | CV 类型: {shot.CvType} | 字节: {shot.PixelBytes}");
            Console.WriteLine($"    平均亮度: {shot.AverageBrightness:F1}/255 | 非黑像素比例: {shot.NonBlackRatio:P1}");
            Console.WriteLine($"    黑边: 右 {shot.BlackBorderRight}px / 底 {shot.BlackBorderBottom}px（占 {shot.BlackBorderRight / (double)shot.Width:P1} / {shot.BlackBorderBottom / (double)shot.Height:P1}）");
            if (shot.HasSevereBlackBorder)
            {
                Console.WriteLine("    ⚠ 严重黑边：截图未覆盖完整客户区，框架映射的点击坐标会整体偏移");
            }

            bool hasContent = shot.NonBlackRatio > 0.01 && shot.AverageBrightness > 5;
            Console.WriteLine($"    画面内容判定: {(hasContent ? "✓ 非空白（抓到了真实画面）" : "✗ 疑似空白/纯黑（窗口可能最小化或被遮挡）")}");

            // ---- [6] 原始分辨率对比 ----
            Console.WriteLine("[6] 原始分辨率（未缩放）:");
            if (controller.TryGetResolution(out int rawW, out int rawH))
            {
                Console.WriteLine($"    {rawW}x{rawH}");
                if (win32Controller.DisplayShortSide is { } side)
                {
                    Console.WriteLine($"    display_short_side={side} → 截图短边应为 {side}（实际 {Math.Min(shot.Width, shot.Height)}）");
                }
                else
                {
                    Console.WriteLine("    未设置 display_short_side，截图应为原始尺寸");
                }
            }
            else
            {
                Console.WriteLine("    不可用（未连接或尚未截图）");
            }

            // ---- [7] 显式输入验证（策略 A）----
            if (click is { } c)
            {
                if (forceForeground)
                {
                    Console.WriteLine("[7] --foreground: 先将游戏窗口切到前台再点击");
                    Win32Diag.SetForegroundWindow(target.Handle);
                    Thread.Sleep(400);
                }

                Console.WriteLine($"[7] 显式点击 --click {c.X},{c.Y}（SendMessage 后台点击，不抢焦点）:");
                var clickStatus = controller.Click(c.X, c.Y);
                Console.WriteLine($"    PostClick({c.X},{c.Y}) -> {clickStatus}");
                if (clickStatus != MaaStatus.Succeeded)
                {
                    Console.Error.WriteLine("    点击失败");
                    return 1;
                }

                // 点击后重新截图，通过画面变化侧面验证输入有响应
                var shot2 = controller.SaveScreenshot(shotPath.Replace(".bmp", "-after-click.bmp"));
                if (shot2 is not null)
                {
                    Console.WriteLine($"    点击后截图: {shot2.Width}x{shot2.Height} | 平均亮度 {shot2.AverageBrightness:F1} | 非黑比例 {shot2.NonBlackRatio:P1}");
                    Console.WriteLine("    （若游戏画面发生变化即证明输入链路有响应）");
                }
            }
            else
            {
                Console.WriteLine("[7] 未传 --click，跳过点击验证（策略 A：点击须显式执行）");
            }

            if (swipe is { } s)
            {
                Console.WriteLine($"[8] 显式滑动 --swipe {s.X1},{s.Y1} -> {s.X2},{s.Y2} ({s.DurationMs}ms):");
                var swipeStatus = controller.Swipe(s.X1, s.Y1, s.X2, s.Y2, s.DurationMs);
                Console.WriteLine($"    PostSwipe -> {swipeStatus}");
                if (swipeStatus != MaaStatus.Succeeded)
                {
                    Console.Error.WriteLine("    滑动失败");
                    return 1;
                }
            }

            // ---- [9] 直接 Win32 合成点击（绕过 MaaFramework，诊断框架输入链路）----
            if (rawClick is { } rc)
            {
                Console.WriteLine($"[9] 直接 Win32 合成点击 --raw-win32-click {rc.X},{rc.Y}（SetCursorPos + mouse_event）:");
                if (!controller.TryGetResolution(out int rw, out int rh))
                {
                    Console.Error.WriteLine("    无法获取原始分辨率");
                    return 1;
                }

                // 截图空间 → 游戏渲染空间 → 屏幕空间
                int gx = (int)Math.Round(rc.X * rw / (double)shot.Width);
                int gy = (int)Math.Round(rc.Y * rh / (double)shot.Height);
                if (!Win32Diag.ClientToScreen(target.Handle, new Win32Diag.POINT { X = gx, Y = gy }, out Win32Diag.POINT screenPt))
                {
                    Console.Error.WriteLine("    ClientToScreen 失败");
                    return 1;
                }

                Console.WriteLine($"    截图({rc.X},{rc.Y}) → 游戏({gx},{gy}) → 屏幕({screenPt.X},{screenPt.Y})");
                Win32Diag.SetForegroundWindow(target.Handle);
                Thread.Sleep(300);
                Win32Diag.SetCursorPos(screenPt.X, screenPt.Y);
                Thread.Sleep(80);
                Win32Diag.mouse_event(Win32Diag.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(50);
                Win32Diag.mouse_event(Win32Diag.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

                Thread.Sleep(600); // 等待游戏响应
                var shot3 = controller.SaveScreenshot(shotPath.Replace(".bmp", "-after-rawclick.bmp"));
                if (shot3 is not null)
                {
                    Console.WriteLine($"    点击后截图: {shot3.Width}x{shot3.Height} | 平均亮度 {shot3.AverageBrightness:F1} | 非黑比例 {shot3.NonBlackRatio:P1}");
                }
            }

            // ---- [10] 绝对屏幕坐标点击（诊断：完全绕过框架坐标映射）----
            if (screenClick is { } sc)
            {
                Console.WriteLine($"[10] 绝对屏幕坐标点击 --screen-click {sc.X},{sc.Y}:");
                Win32Diag.SetForegroundWindow(target.Handle);
                Thread.Sleep(300);
                Win32Diag.SetCursorPos(sc.X, sc.Y);
                Thread.Sleep(80);
                Win32Diag.mouse_event(Win32Diag.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(50);
                Win32Diag.mouse_event(Win32Diag.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

                Thread.Sleep(600); // 等待游戏响应
                var shot4 = controller.SaveScreenshot(shotPath.Replace(".bmp", "-after-screenclick.bmp"));
                if (shot4 is not null)
                {
                    Console.WriteLine($"    点击后截图: {shot4.Width}x{shot4.Height} | 平均亮度 {shot4.AverageBrightness:F1} | 非黑比例 {shot4.NonBlackRatio:P1}");
                }
            }

            Console.WriteLine("=== P1.4 验证完成 ===");
            return 0;
        }
    }

    private static (int X, int Y) ParsePoint(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new FormatException($"坐标格式应为 x,y，收到: {value}");
        }

        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static (int W, int H) ParseSize(string value)
    {
        string[] parts = value.Split('x', 'X', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new FormatException($"尺寸格式应为 WxH，收到: {value}");
        }

        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static (int X1, int Y1, int X2, int Y2, int DurationMs) ParseSwipe(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 5)
        {
            throw new FormatException($"滑动格式应为 x1,y1,x2,y2,durationMs，收到: {value}");
        }

        return (int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), int.Parse(parts[4]));
    }
}
