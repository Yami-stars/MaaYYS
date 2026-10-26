using System.Runtime.InteropServices;
using MaaYys.Engine.Native;

namespace MaaYys.Engine.Devices;

/// <summary>ADB 设备信息（MaaToolkitAdbDevice 的托管投影）。</summary>
/// <param name="Name">设备名（模拟器预设名或 adb 序列号）。</param>
/// <param name="AdbPath">adb 可执行文件路径。</param>
/// <param name="Address">adb 连接地址（如 127.0.0.1:16384）。</param>
/// <param name="ScreencapMethods">该设备支持的截图方法位掩码。</param>
/// <param name="InputMethods">该设备支持的输入方法位掩码。</param>
/// <param name="Config">设备附加配置 JSON（模拟器预设携带，如 minitouch 路径）。</param>
public sealed record AdbDeviceInfo(
    string Name,
    string AdbPath,
    string Address,
    ulong ScreencapMethods,
    ulong InputMethods,
    string Config)
{
    public override string ToString() => $"{Name} ({Address})";
}

/// <summary>
/// ADB 设备枚举（MaaToolkit.dll 的 MaaToolkitAdbDevice 系列 API，P2.3）。
/// 与 MaaPiCli 行为一致：Find 会合并「adb devices 实际结果」与「内置模拟器预设」。
/// 无 adb 环境时返回空列表而非抛异常——GUI 需要能降级到仅 Win32。
/// </summary>
public static class MaaAdbDevices
{
    /// <summary>枚举全部 ADB 设备（含模拟器预设）。</summary>
    public static List<AdbDeviceInfo> FindAll() => FindCore(null);

    /// <summary>用指定 adb 路径枚举设备。</summary>
    public static List<AdbDeviceInfo> FindSpecified(string adbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        return FindCore(adbPath);
    }

    private static List<AdbDeviceInfo> FindCore(string? adbPath)
    {
        MaaToolkitAdbDeviceListHandle list = new(MaaFrameworkApi.MaaToolkitAdbDeviceListCreate());
        if (list.IsInvalid)
        {
            throw new InvalidOperationException("MaaToolkitAdbDeviceListCreate 返回空句柄");
        }

        using (list)
        {
            bool found;
            if (adbPath is null)
            {
                found = MaaFrameworkApi.MaaToolkitAdbDeviceFind(list) != MaaDef.False;
            }
            else
            {
                IntPtr pathPtr = MaaFramework.ToUtf8Native(adbPath);
                try
                {
                    found = MaaFrameworkApi.MaaToolkitAdbDeviceFindSpecified(pathPtr, list) != MaaDef.False;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }
            }

            if (!found)
            {
                return [];
            }

            List<AdbDeviceInfo> devices = [];
            ulong count = list.Count;
            for (ulong i = 0; i < count; i++)
            {
                IntPtr device = MaaFrameworkApi.MaaToolkitAdbDeviceListAt(list, i);
                if (device == IntPtr.Zero)
                {
                    continue;
                }

                devices.Add(new AdbDeviceInfo(
                    MaaFramework.FromUtf8Native(MaaFrameworkApi.MaaToolkitAdbDeviceGetName(device)) ?? string.Empty,
                    MaaFramework.FromUtf8Native(MaaFrameworkApi.MaaToolkitAdbDeviceGetAdbPath(device)) ?? string.Empty,
                    MaaFramework.FromUtf8Native(MaaFrameworkApi.MaaToolkitAdbDeviceGetAddress(device)) ?? string.Empty,
                    MaaFrameworkApi.MaaToolkitAdbDeviceGetScreencapMethods(device),
                    MaaFrameworkApi.MaaToolkitAdbDeviceGetInputMethods(device),
                    MaaFramework.FromUtf8Native(MaaFrameworkApi.MaaToolkitAdbDeviceGetConfig(device)) ?? string.Empty));
            }

            return devices;
        }
    }
}

/// <summary>ADB 截图方法位掩码（MaaDef.h，P2.3 起可用）。</summary>
[Flags]
public enum MaaAdbScreencapMethod : ulong
{
    None = 0,
    EncodeToFileAndPull = 1UL << 0,
    Encode = 1UL << 1,
    RawWithGzip = 1UL << 2,
    RawByNetcat = 1UL << 3,
    MinicapDirect = 1UL << 4,
    MinicapStream = 1UL << 5,
    EmulatorExtras = 1UL << 6,

    /// <summary>默认可用集合（排除 RawByNetcat / Minicap*，对齐 MaaDef.h 的 Default 宏）。</summary>
    Default = 1UL | (1UL << 1) | (1UL << 2) | (1UL << 6),
}

/// <summary>ADB 输入方法位掩码（MaaDef.h）。</summary>
[Flags]
public enum MaaAdbInputMethod : ulong
{
    None = 0,
    AdbShell = 1UL << 0,
    MinitouchAndAdbKey = 1UL << 1,
    Maatouch = 1UL << 2,
    EmulatorExtras = 1UL << 3,

    /// <summary>默认可用集合（对齐 MaaDef.h 的 Default 宏）。</summary>
    Default = 1UL | (1UL << 1) | (1UL << 2) | (1UL << 3),
}
