using System.Runtime.InteropServices;
using MaaYys.Engine.Native;

namespace MaaYys.Engine;

/// <summary>框架事件数据（来自 MaaEventCallback 的 message / details_json）。</summary>
/// <param name="Message">消息字符串，如 "Tasker.Task.Succeeded"。</param>
/// <param name="DetailsJson">JSON 详情，可能为 null。</param>
public sealed class MaaFrameworkEventArgs(string? message, string? detailsJson) : EventArgs
{
    public string? Message { get; } = message;
    public string? DetailsJson { get; } = detailsJson;
}

/// <summary>MaaFramework 运行库的公共访问入口。</summary>
public static class MaaFramework
{
    /// <summary>运行库版本号（MaaVersion，UTF-8 字符串）。</summary>
    public static string Version => MaaFrameworkApi.Version();

    /// <summary>已定位的 MaaFramework.dll 绝对路径；未定位时为 null（走系统默认解析）。</summary>
    public static string? NativeLibraryPath => NativeLoader.Locate("MaaFramework.dll");

    /// <summary>将托管字符串转为原生 UTF-8 非托管内存（调用方负责 FreeCoTaskMem 释放）。</summary>
    internal static IntPtr ToUtf8Native(string? value)
        => value is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(value);

    /// <summary>将原生 UTF-8 字符串指针转为托管字符串。</summary>
    internal static string? FromUtf8Native(IntPtr ptr)
        => ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
}
