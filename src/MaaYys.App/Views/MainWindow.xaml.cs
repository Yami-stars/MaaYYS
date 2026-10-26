using System.Windows;
using MaaYys.App.Services;
using MaaYys.App.ViewModels;

namespace MaaYys.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // 最小化时暂停预览采集：预览泵在窗口不可见时照常抓帧 + 写位图，
        // 实测会把进程 CPU 打到 100%+，并让再次点击任务栏恢复窗口时明显迟滞。
        // 还原（含从任务栏还原、最大化）时恢复采集。
        StateChanged += (_, _) =>
        {
            bool minimized = WindowState == WindowState.Minimized;
            PreviewSuspension.IsSuspended = minimized;

            if (!minimized)
            {
                // 从最小化恢复：强制整窗重绘。
                // （P2.5 二次修复）纯托管层的 InvalidateVisual 在渲染线程仍挂起时不起作用，
                // 会偶发「窗口框架回来了、客户区却全空、透过去看到桌面」。改为 Win32 强制重绘 +
                // 内容自愈守卫，详见 WindowRenderRecovery。
                WindowRenderRecovery.Reanchor(this);
            }
        };

        Activated += (_, _) => PreviewSuspension.IsSuspended = false;

        Closing += (_, _) =>
        {
            PreviewSuspension.IsSuspended = false;
            _viewModel.Dispose();
        };

        // 渲染表面自愈守卫：订阅 StateChanged，恢复后持续确认内容真的画出来了
        WindowRenderRecovery.Attach(this);
    }
}
