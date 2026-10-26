using System.Runtime.InteropServices;
using MaaYys.Engine.Native;

namespace MaaYys.Engine;

/// <summary>
/// 控制器上的「启动/关闭应用」操作（P3.5）。
/// <para>
/// 为什么要有这层：渠道服的包名写在 pipeline 节点的 <c>package</c> 字段里，而框架规定
/// <c>package</c> 只在 <c>action</c> 为 <c>StartApp</c>/<c>StopApp</c> 时才合法。
/// base 的「启动游戏」是 <c>DirectHit</c>（桌面端语义：游戏已经在跑，不需要脚本去启动），
/// 所以渠道层写的 package 会被框架当未知字段丢弃 —— 渠道服的差异在资源侧是「看不见」的。
/// 因此包名由 <see cref="Resources.ChannelLayerResolver"/> 直读文件拿到，再由这里显式投递。
/// </para>
/// <para>
/// Adb 控制器真正实现了这两个操作（<c>am start</c> / <c>am force-stop</c>）；
/// Win32、Null 控制器没有实现，会返回 <see cref="MaaStatus.Invalid"/> 或 failed —— 不会崩，
/// 但也不该被当成成功。调用方（P5 执行链）只在 Adb 控制器上用。
/// </para>
/// </summary>
public static class MaaControllerApps
{
    /// <summary>启动应用。intent 可以是纯包名，也可以是 <c>包名/Activity</c>。</summary>
    public static MaaStatus StartApp(MaaControllerHandle handle, string? intent)
        => Post(handle, intent, start: true);

    /// <summary>关闭应用。必须是<b>纯包名</b>：<c>am force-stop</c> 不接受 activity。</summary>
    public static MaaStatus StopApp(MaaControllerHandle handle, string? package)
        => Post(handle, package, start: false);

    /// <summary>启动应用并等待完成；返回是否成功。</summary>
    public static bool TryStartApp(MaaControllerHandle handle, string? intent)
        => StartApp(handle, intent) == MaaStatus.Succeeded;

    /// <summary>关闭应用并等待完成；返回是否成功。</summary>
    public static bool TryStopApp(MaaControllerHandle handle, string? package)
        => StopApp(handle, package) == MaaStatus.Succeeded;

    private static MaaStatus Post(MaaControllerHandle handle, string? text, bool start)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return MaaStatus.Invalid;
        }

        IntPtr ptr = MaaFramework.ToUtf8Native(text);
        try
        {
            long id = start
                ? MaaFrameworkApi.MaaControllerPostStartApp(handle, ptr)
                : MaaFrameworkApi.MaaControllerPostStopApp(handle, ptr);

            return id == 0 ? MaaStatus.Invalid : (MaaStatus)MaaFrameworkApi.MaaControllerWait(handle, id);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }
}
