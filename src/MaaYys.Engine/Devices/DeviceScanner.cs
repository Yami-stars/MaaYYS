namespace MaaYys.Engine.Devices;

/// <summary>可连接设备类型。</summary>
public enum DeviceKind
{
    Win32 = 0,
    Adb = 1,
}

/// <summary>统一的设备描述（GUI 设备列表的一项）。</summary>
public sealed record DeviceDescriptor(DeviceKind Kind, string DisplayName, string Detail)
{
    /// <summary>Win32 窗口句柄（Adb 时为零）。</summary>
    public IntPtr WindowHandle { get; init; } = IntPtr.Zero;

    /// <summary>ADB 设备信息（Win32 时为 null）。</summary>
    public AdbDeviceInfo? Adb { get; init; }

    /// <summary>
    /// 设备独占登记键（多实例防冲突）：Win32 用窗口句柄、ADB 用 host:port。
    /// 同一台设备被两个实例同时连接时输入会互相干扰，连接前据此判定。
    /// </summary>
    public string LeaseKey => Kind == DeviceKind.Win32
        ? $"win32:0x{WindowHandle.ToInt64():X}"
        : $"adb:{Adb?.Address ?? Detail}";

    public override string ToString() => DisplayName;
}

/// <summary>
/// 设备自动探测（P2.3）：Win32 桌面窗口 + ADB 设备。
/// 设计约定：探测失败不抛异常，返回空列表并给出诊断串——未装 adb 的用户仍可用 Win32 模式。
/// </summary>
public static class DeviceScanner
{
    /// <summary>
    /// 枚举桌面窗口（按标题/类名正则过滤，null 表示不限制）。
    /// </summary>
    public static IReadOnlyList<DeviceDescriptor> ScanWin32(string? titleRegex = null, string? classRegex = null)
    {
        List<DeviceDescriptor> result = [];
        foreach (DesktopWindowInfo window in MaaDesktopWindows.FindAll(classRegex, titleRegex))
        {
            string name = string.IsNullOrWhiteSpace(window.WindowName) ? window.ClassName : window.WindowName;
            result.Add(new DeviceDescriptor(
                DeviceKind.Win32,
                name,
                $"class=[{window.ClassName}] hWnd=0x{window.Handle.ToInt64():X}")
            {
                WindowHandle = window.Handle,
            });
        }

        return result;
    }

    /// <summary>
    /// 枚举 ADB 设备（含模拟器预设）。无 adb 环境时返回空列表。
    /// </summary>
    public static IReadOnlyList<DeviceDescriptor> ScanAdb()
    {
        List<DeviceDescriptor> result = [];
        List<AdbDeviceInfo> devices;
        try
        {
            devices = MaaAdbDevices.FindAll();
        }
        catch (Exception)
        {
            // 无 adb / Toolkit 不可用时降级为空列表（调用方据 Detail 提示用户）
            return result;
        }

        foreach (AdbDeviceInfo device in devices)
        {
            result.Add(new DeviceDescriptor(
                DeviceKind.Adb,
                string.IsNullOrWhiteSpace(device.Name) ? device.Address : device.Name,
                $"{device.Address} · adb={device.AdbPath}")
            {
                Adb = device,
            });
        }

        return result;
    }

    /// <summary>同时枚举 Win32 与 ADB 设备。</summary>
    public static IReadOnlyList<DeviceDescriptor> ScanAll(
        string? titleRegex = null,
        string? classRegex = null,
        bool includeAdb = true)
    {
        List<DeviceDescriptor> all = [.. ScanWin32(titleRegex, classRegex)];
        if (includeAdb)
        {
            all.AddRange(ScanAdb());
        }

        return all;
    }
}
