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

        // ---- [4] 创建控制器并连接 ----
        Console.WriteLine("[4] 创建 Win32 控制器（方法取 interface.json 配置）:");
        MaaWin32Controller controller;
        try
        {
            controller = MaaWin32Controller.Create(
                target.Handle,
                MaaWin32MethodNames.ParseScreencap(w32.Screencap),
                MaaWin32MethodNames.ParseInput(w32.Mouse),
                MaaWin32MethodNames.ParseInput(w32.Keyboard));
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
