using MaaYys.Engine.Devices;
using MaaYys.Engine.Session;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>设备探测与帧采集泵测试（P2.3）。</summary>
public sealed class DeviceTests
{
    [Fact]
    public void ScanAdb_ShouldNotThrow_WhenNoEmulator()
    {
        // 无 adb / 无模拟器时应降级为空列表，而不是抛异常（GUI 仍可用 Win32）
        IReadOnlyList<DeviceDescriptor> devices = DeviceScanner.ScanAdb();
        Assert.NotNull(devices);
    }

    [Fact]
    public void ScanWin32_WithUnmatchedRegex_ShouldReturnEmpty()
    {
        IReadOnlyList<DeviceDescriptor> devices = DeviceScanner.ScanWin32("__maayys_no_such_window__");
        Assert.Empty(devices);
    }

    [Fact]
    public void ScanWin32_WithInvalidRegex_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => DeviceScanner.ScanWin32("["));
    }

    [Fact]
    public void ScanAll_ShouldCombineSources()
    {
        IReadOnlyList<DeviceDescriptor> devices = DeviceScanner.ScanAll("__maayys_no_such_window__", null, includeAdb: true);
        Assert.NotNull(devices);
        Assert.DoesNotContain(devices, d => d.Kind == DeviceKind.Win32);
    }

    [Fact]
    public async Task FramePump_ShouldDeliverFrames_AndStopCleanly()
    {
        using var pump = new FramePump();
        int callbacks = 0;
        using IDisposable subscription = pump.Frames.Subscribe(_ => Interlocked.Increment(ref callbacks));

        pump.Start(CreateFrame, fps: 20);
        await Task.Delay(400);
        pump.Stop();

        Assert.True(pump.FrameCount > 0, "未收到任何帧");
        Assert.True(callbacks > 0, "订阅者未收到帧");
        Assert.False(pump.IsRunning);

        int frozen = Volatile.Read(ref callbacks);
        await Task.Delay(200);
        Assert.Equal(frozen, Volatile.Read(ref callbacks));
    }

    [Fact]
    public void FramePump_SetFps_ShouldClampAndUpdate()
    {
        using var pump = new FramePump();
        pump.SetFps(1000);
        Assert.Equal(60, pump.TargetFps);

        pump.SetFps(0);
        Assert.Equal(1, pump.TargetFps);
    }

    [Fact]
    public void FramePump_TargetFps_ShouldExposeStartedValue()
    {
        using var pump = new FramePump();
        pump.Start(CreateFrame, fps: 7);
        Assert.Equal(7, pump.TargetFps);
        pump.Stop();
    }

    [Fact]
    public void FramePump_AfterDispose_ShouldThrowOnStart()
    {
        var pump = new FramePump();
        pump.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pump.Start(CreateFrame));
    }

    private static FrameData CreateFrame() => new(
        [0, 0, 0],
        1,
        1,
        3,
        DateTimeOffset.Now.Ticks,
        DateTimeOffset.Now,
        new ScreenshotResult(1, 1, 3, 16, 3, 0, 1.0, 0, 0));
}
