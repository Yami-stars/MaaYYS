using System.Runtime.InteropServices;

namespace MaaYys.App.Services;

/// <summary>
/// 关闭本进程的 Windows「效率模式」/ 后台节流（EcoQoS）。
///
/// 为什么需要：Win11 会把后台窗口的进程标记为 EcoQoS，渲染线程与 UI 线程被降频、甚至进入
/// 深度等待。实测表现就是「点了任务栏窗口弹出来了，但内容是空的，要等一会儿或动一下窗口才画出来」
/// —— 与最小化后渲染表面重建慢的现象完全一致。GUI 应用应显式声明不要被这类策略节流。
/// </summary>
public static class ProcessPowerMode
{
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingExecutionSpeed = 0x1;

    /// <summary>申请「不启用节流」。失败不影响主流程（老系统上该 API 可能不存在）。</summary>
    public static void DisableEfficiencyMode()
    {
        try
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = 1,
                // ControlMask 指定要控制的策略位；StateMask 置 0 表示关闭该策略
                ControlMask = PowerThrottlingExecutionSpeed,
                StateMask = 0,
            };

            SetProcessInformation(
                GetCurrentProcess(),
                ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        catch (DllNotFoundException)
        {
            // Win10 1709 以下没有该 API，忽略
        }
        catch (EntryPointNotFoundException)
        {
            // 同上
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr process, int informationClass, ref ProcessPowerThrottlingState information, int size);
}
