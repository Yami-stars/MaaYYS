using System.Diagnostics;
using MaaYys.Engine;
using MaaYys.Engine.Devices;
using MaaYys.Engine.Session;

namespace MaaYys.P1Probe;

/// <summary>
/// P2.3 设备管理验证：设备探测 → 连接 → 帧采集泵实测帧率。
/// 用法：devices [--regex 阴阳师] [--fps 10] [--seconds 3] [--adb-only] [--win-only]
/// </summary>
internal static class ProbeDevices
{
    public static async Task<int> RunAsync(string[] args)
    {
        string regex = ReadOption(args, "--regex") ?? "阴阳师";
        int fps = int.TryParse(ReadOption(args, "--fps"), out int f) ? f : 10;
        int seconds = int.TryParse(ReadOption(args, "--seconds"), out int s) ? s : 3;
        bool adbOnly = args.Contains("--adb-only");
        bool winOnly = args.Contains("--win-only");

        Console.WriteLine("=== P2.3 设备管理验证 ===");
        Console.WriteLine($"[1] 探测：Win32 标题正则「{regex}」；ADB={(adbOnly || !winOnly ? "是" : "否")}");

        IReadOnlyList<DeviceDescriptor> devices = DeviceScanner.ScanAll(
            winOnly ? null : regex,
            null,
            includeAdb: !winOnly);
        if (adbOnly)
        {
            devices = [.. devices.Where(d => d.Kind == DeviceKind.Adb)];
        }

        Console.WriteLine($"    发现 {devices.Count} 个设备");
        foreach (DeviceDescriptor device in devices)
        {
            Console.WriteLine($"    - [{device.Kind}] {device.DisplayName} | {device.Detail}");
        }

        DeviceDescriptor? target = devices.FirstOrDefault(d => d.Kind == DeviceKind.Win32)
                                   ?? devices.FirstOrDefault();
        if (target is null)
        {
            Console.Error.WriteLine("未找到可连接设备（请确认游戏/模拟器已启动）");
            return 1;
        }

        Console.WriteLine($"[2] 连接目标：{target.DisplayName}（{target.Kind}）");
        using MaaYysEngine engine = target.Kind == DeviceKind.Win32
            ? MaaYysEngine.CreateWin32(target.WindowHandle)
            : MaaYysEngine.CreateAdb(target.Adb!);

        Console.WriteLine($"    截图方法：{MaaWin32MethodNames.ScreencapName(engine.ScreencapMethod)}");
        Console.WriteLine($"    选择过程：{engine.ConnectDiagnostic}");

        EngineConnectResult connect = await engine.ConnectAsync();
        Console.WriteLine($"    连接结果：{(connect.Success ? "成功" : "失败")} uuid={connect.Uuid} {connect.Error}");
        if (!connect.Success)
        {
            return 1;
        }

        int frameCount = 0;
        FrameData? last = null;
        var pump = new FramePump();
        using IDisposable subscription = pump.Frames.Subscribe(frame =>
        {
            frameCount++;
            last = frame;
        });

        Console.WriteLine($"[3] 帧采集：目标 {fps} fps，持续 {seconds}s");
        Stopwatch elapsed = Stopwatch.StartNew();
        pump.Start(() => engine.CaptureFrame(), fps);
        while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            await Task.Delay(250);
            Console.Write($"\r    已采集 {pump.FrameCount} 帧，实际 {pump.ActualFps:F1} fps   ");
        }

        // 验证运行时调帧率
        pump.SetFps(Math.Min(30, fps * 2));
        Console.WriteLine($"\n[4] 运行时调帧率 → {pump.TargetFps} fps");
        await Task.Delay(1200);
        pump.Stop();

        Console.WriteLine($"    订阅回调 {frameCount} 次；最终实际 {pump.ActualFps:F1} fps");
        if (last is not null)
        {
            Console.WriteLine($"    末帧：{last.Width}×{last.Height}×{last.Channels} 通道，{last.Stats.PixelBytes} 字节，" +
                              $"非黑 {last.NonBlackRatio:P1}，黑边 右{last.Stats.BlackBorderRight}px/下{last.Stats.BlackBorderBottom}px");
        }

        bool ok = pump.FrameCount > 0 && frameCount > 0;
        Console.WriteLine(ok ? "=== P2.3 验证通过 ===" : "=== P2.3 验证失败 ===");
        return ok ? 0 : 2;
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
