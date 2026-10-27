using System.Runtime.InteropServices;

namespace MaaYys.App.Services;

/// <summary>
/// 防休眠（P6.2 运行设置）：队列执行期间告诉系统「有人在干活」，别睡。
/// <para>
/// 为什么需要：挂机长跑最典型的失败就是「跑了一半机器睡过去，回来发现卡在某个界面」。
/// 用 <c>SetThreadExecutionState</c> 而不是改电源计划 —— 后者会永久改动用户的系统设置，
/// 前者只在进程持有期间生效，进程退出系统自动恢复。
/// </para>
/// <para>
/// 引用计数：多个调用方（手动队列 / 定时队列）可能先后 Acquire，最后一个 Dispose 才真正释放。
/// </para>
/// </summary>
public static class SleepGuard
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private static readonly object Gate = new();
    private static int _holders;
    private static bool _active;

    /// <summary>当前是否处于「阻止休眠」状态（界面显示用）。</summary>
    public static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                return _active;
            }
        }
    }

    /// <summary>
    /// 开始阻止系统休眠；返回的令牌释放时自动结束（可重复 Acquire）。
    /// 平台不支持时静默降级为空操作。
    /// </summary>
    public static IDisposable Acquire()
    {
        lock (Gate)
        {
            _holders++;
            if (!_active)
            {
                _active = TrySet(EsContinuous | EsSystemRequired);
            }
        }

        return new Token();
    }

    private static void Release()
    {
        lock (Gate)
        {
            if (_holders > 0)
            {
                _holders--;
            }

            if (_holders == 0 && _active)
            {
                _active = !TrySet(EsContinuous);
            }
        }
    }

    private static bool TrySet(uint flags)
    {
        try
        {
            // 返回 0 表示调用失败（例如在非 Windows 环境），此时不认为自己生效
            return SetThreadExecutionState(flags) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private sealed class Token : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release();
        }
    }
}
