namespace MaaYys.App.Services;

/// <summary>
/// 全局预览暂停开关（P2.5）：主窗口最小化 / 不在前台时置 true，让各预览采集泵停掉。
///
/// 为什么需要：预览泵的抓图 + 位图写入在窗口最小化期间照旧运行（实测挂后台时进程
/// 持续吃 100%~260% CPU）。既浪费资源，也会让最小化后再次点击任务栏时的窗口恢复
/// 明显迟滞（UI 线程被位图写入与 GC 抢占），表现为「点了没反应」。
/// </summary>
public static class PreviewSuspension
{
    private static bool _suspended;

    /// <summary>暂停状态变化（在设置线程上触发，订阅方自行切回 UI 线程）。</summary>
    public static event EventHandler? Changed;

    public static bool IsSuspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value)
            {
                return;
            }

            _suspended = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
