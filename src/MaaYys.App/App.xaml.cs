using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaaYys.App.Services;
using MaaYys.App.Themes;
using MaaYys.App.ViewModels;
using MaaYys.App.Views;
using MaaYys.Engine.Cleanup;

namespace MaaYys.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // GUI 子系统进程没有控制台，异常必须落文件，否则崩溃后无迹可查
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog($"【致命】{args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) =>
            CrashLog($"【UI 线程未处理】{args.Exception}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
            CrashLog($"【未观察的 Task 异常】{args.Exception}");

        // 关掉 Windows 效率模式：否则最小化后进程被降频/挂起，恢复时渲染表面重建慢，
        // 表现为「窗口弹出来了但内容是空的」。
        ProcessPowerMode.DisableEfficiencyMode();

        // 与 App.xaml 默认字典保持一致，并初始化 ThemeManager 状态
        ThemeManager.Apply(ThemeKind.Dark);
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // 调试：--size 1280x720 指定窗口尺寸（P6.1 的验收条件就是「1280×720 布局无截断」，
        // 靠默认尺寸截图无法证明这一点）
        if (ReadOption(e.Args, "--size") is { } size && TryParseSize(size, out double requestedWidth, out double requestedHeight))
        {
            window.Width = requestedWidth;
            window.Height = requestedHeight;
        }

        // 调试：--light 以浅色主题截图，校验两套主题下新版布局都不漏字
        if (e.Args.Contains("--light"))
        {
            ThemeManager.Apply(MaaYys.App.Themes.ThemeKind.Light);
        }

        // 资源版本更新后，旧版本留下的不可达节点 / 未引用模板与模型 / *.bak 在后台自动清掉。
        // 截图模式不跑：那种模式进程几秒就退出，后台任务半途被杀没有意义。
        if (!e.Args.Contains("--screenshot"))
        {
            ResourceMaintenance.RunInBackground((window.DataContext as MainViewModel)?.InterfacePath);
        }

        // 调试能力（P2.3 起）：--page N 切页签、--screenshot path 渲染界面截图后退出。
        // 用于无人工干预地校验界面布局（P6 界面阶段会频繁使用）。
        int page = int.TryParse(ReadOption(e.Args, "--page"), out int parsed) ? parsed : 0;
        string? screenshot = ReadOption(e.Args, "--screenshot");
        if (screenshot is null)
        {
            // 诊断：验证「1px 尺寸抖动」兜底路径不会破坏窗口尺寸（P2.5 二次修复自检）
            if (e.Args.Contains("--diag-nudge"))
            {
                window.Dispatcher.InvokeAsync(
                    async () =>
                    {
                        await Task.Delay(3000);
                        WindowRenderRecovery.RunNudgeSelfTest(window);
                    },
                    DispatcherPriority.ApplicationIdle);
            }
            // 仅自动连接、程序保持运行：用于验证最小化/恢复等交互行为（无人值守诊断）。
            // 与真实操作路径一致：先切到实例页（预览泵只在实例页激活时跑）再连接。
            if (e.Args.Contains("--autoconnect"))
            {
                string? switchResource = ReadOption(e.Args, "--switch-resource");
                window.Dispatcher.InvokeAsync(
                    async () =>
                    {
                        ApplyTab(window.DataContext as MainViewModel, e.Args, page);
                        await Task.Delay(1200);
                        await ConnectCurrentInstanceAsync(window, switchResource);
                    },
                    DispatcherPriority.ApplicationIdle);
            }

            return;
        }

        string? instanceKey = ReadOption(e.Args, "--instance");
        string? closeInstance = ReadOption(e.Args, "--close-instance");
        int addInstances = e.Args.Count(a => a == "--add-instance");
        bool autoConnect = e.Args.Contains("--connect");
        window.Dispatcher.InvokeAsync(
            async () =>
            {
                MainViewModel? viewModel = window.DataContext as MainViewModel;
                ApplyTab(viewModel, e.Args, page);

                // 可选：清空当前配置组的队列（--clear-queue），用于截空队列状态
                if (e.Args.Contains("--clear-queue") && viewModel?.Tasks is { } emptyQueue)
                {
                    while (emptyQueue.Queue.Count > 0)
                    {
                        emptyQueue.Queue[^1].RemoveCommand.Execute(null);
                    }

                    await Task.Delay(250);
                }

                // 可选：往当前配置组的队列里加任务（--add-task 名称，可重复）
                // 走与「添加任务」下拉完全相同的一条路，用于无人值守校验任务链 / 状态总览
                foreach (string entryName in ReadOptions(e.Args, "--add-task"))
                {
                    viewModel?.Tasks.AddTaskCommand.Execute(entryName);
                    await Task.Delay(150);
                }

                // 可选：任务页自动选中某个任务（配合 --screenshot 校验参数面板）
                if (ReadOption(e.Args, "--task") is { } taskName && viewModel?.Tasks is { } tasksPage)
                {
                    tasksPage.SelectedQueueItem = tasksPage.Queue.FirstOrDefault(
                        q => string.Equals(q.DisplayName, taskName, StringComparison.Ordinal)
                             || string.Equals(q.Name, taskName, StringComparison.Ordinal));
                    await Task.Delay(400);
                }

                // 可选：切到「高级设置」二级页（--right-tab 1），校验全局选项与编译预览
                if (ReadOption(e.Args, "--right-tab") is { } rightTabRaw
                    && int.TryParse(rightTabRaw, out int rightTab)
                    && viewModel?.Tasks is { } rightTabPage)
                {
                    rightTabPage.SelectedRightTab = rightTab;
                    await Task.Delay(300);
                }

                // 可选：预置选项值（--set-option "任务|选项|取值"，可重复），走与界面相同的保存路径
                foreach (string raw in ReadOptions(e.Args, "--set-option"))
                {
                    string[] parts = raw.Split('|');
                    if (parts.Length == 3 && viewModel?.Tasks is { } page)
                    {
                        page.ApplyOption(parts[0], parts[1], parts[2]);
                        await Task.Delay(150);
                    }
                }

                // 可选：设置页直接清一次图片缓存（跳过确认框，供无人值守校验清理路径）
                if (e.Args.Contains("--clear-cache") && viewModel?.CurrentPage is SettingsViewModel settingsPage)
                {
                    await settingsPage.ClearAsync();
                    await Task.Delay(300);
                }

                // 可选：实例增删（P2.4 多开验证，走与 UI 按钮完全相同的命令）
                for (int i = 0; i < addInstances; i++)
                {
                    viewModel?.AddInstanceCommand.Execute(null);
                }

                if (addInstances > 0)
                {
                    await Task.Delay(300);
                }

                // 可选：按名称/Id 选中实例
                if (instanceKey is not null)
                {
                    viewModel?.SelectInstance(instanceKey);
                }

                // 等页面首轮异步加载（设备扫描）完成后再渲染
                await Task.Delay(1200);

                // 可选：自动连接，用于校验截图/预览链路。
                // 连接页有独立设备列表；实例页则连接当前激活实例。
                if (autoConnect)
                {
                    if (viewModel?.CurrentPage is ConnectionViewModel connection)
                    {
                        connection.ConnectCommand.Execute(null);
                        await Task.Delay(2500);
                    }
                    else if (viewModel?.CurrentInstance is { } instance)
                    {
                        instance.ConnectCommand.Execute(null);
                        await Task.Delay(3000);
                    }
                }

                // 可选：逐个连接所有实例（P2.4 多开/设备独占验证：第二个实例应报"设备已被占用"）
                if (e.Args.Contains("--connect-all") && viewModel is not null)
                {
                    foreach (InstanceViewModel instance in viewModel.Instances.ToList())
                    {
                        instance.ConnectCommand.Execute(null);
                        await Task.Delay(3600);
                    }
                }

                // 可选：删除实例（放在连接之后，可验证"删除已连接实例"会释放引擎与设备占用）
                if (closeInstance is not null && viewModel is not null)
                {
                    viewModel.SelectInstance(closeInstance);
                    viewModel.CloseInstanceCommand.Execute(viewModel.CurrentInstance);
                    await Task.Delay(600);
                }

                // 可选：滚到底部再截图（校验长页面下半部分）
                if (e.Args.Contains("--scroll-end"))
                {
                    foreach (System.Windows.Controls.ScrollViewer scroller in FindAll<System.Windows.Controls.ScrollViewer>(window))
                    {
                        scroller.ScrollToEnd();
                    }

                    await Task.Delay(500);
                }

                window.UpdateLayout();
                double scale = double.TryParse(ReadOption(e.Args, "--scale"), out double parsedScale)
                    ? Math.Clamp(parsedScale, 1, 4)
                    : 1;
                SaveRender(window, screenshot, scale);
                Shutdown();
            },
            DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 连接当前激活实例并等待结果，同时写入 <c>debug/autoconnect.log</c>
    /// （GUI 子系统进程没有控制台，只能落文件）。
    /// </summary>
    private static async Task ConnectCurrentInstanceAsync(Window window, string? switchResource = null)
    {
        if (window.DataContext is not MainViewModel viewModel || viewModel.CurrentInstance is null)
        {
            DiagLog("无可连接的实例（instances.json 为空）");
            return;
        }

        InstanceViewModel instance = viewModel.CurrentInstance;
        DiagLog($"连接实例「{instance.DisplayName}」…");
        instance.ConnectCommand.Execute(null);
        for (int i = 0; i < 60 && instance.State is InstanceState.Connecting or InstanceState.Disconnected; i++)
        {
            await Task.Delay(150);
        }

        // 资源链加载在连接成功后异步进行，等它落定再记录，否则抓到的是「正在加载…」的中间态
        for (int i = 0; i < 40 && instance.ResourceStatus.StartsWith("正在加载"); i++)
        {
            await Task.Delay(150);
        }

        DiagLog($"状态={instance.State} | {instance.StatusText} | {instance.FrameInfo} | 资源={instance.ResourceStatus}");

        // P2.5：资源集热切换验证（不重连、不重启）。支持逗号分隔多目标，便于验证双向切换。
        if (switchResource is not null && instance.State == InstanceState.Connected)
        {
            // 用 | 分隔：资源集名里本身可能含逗号（如「官服(雷电,mumu下载的选这个)」）
            foreach (string target in switchResource.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                (bool ok, string message) = await instance.SwitchResourceAsync(target);
                DiagLog($"热切换 → 「{target}」：{(ok ? "成功" : "失败")} | {message}");
            }
        }
    }

    private static void DiagLog(string message) => AppendLog("autoconnect.log", message);

    private static void CrashLog(string message) => AppendLog("crash.log", message);

    private static void AppendLog(string fileName, string message)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, fileName),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // 诊断日志写失败不影响主流程
        }
    }

    /// <summary>
    /// 把窗口渲染为 PNG。<paramref name="scale"/> &gt; 1 时按倍数分辨率渲染，
    /// 用于检查小字号/图标细节（截图默认 96 DPI，细小控件在缩略图上无法辨认）。
    /// </summary>
    private static void SaveRender(Window window, string path, double scale = 1)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(
            (int)(window.ActualWidth * dpi.DpiScaleX * scale),
            (int)(window.ActualHeight * dpi.DpiScaleY * scale),
            dpi.PixelsPerInchX * scale,
            dpi.PixelsPerInchY * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// 切页签（调试用）：<c>--tab 一键长草</c> 按名字，<c>--page N</c> 按下标。
    /// 名字优先 —— 页签重排后下标会漂移，脚本里写名字更稳。
    /// </summary>
    private static void ApplyTab(MainViewModel? viewModel, string[] args, int page)
    {
        if (viewModel is null)
        {
            return;
        }

        if (ReadOption(args, "--tab") is { } name && viewModel.SelectPageByName(name))
        {
            return;
        }

        viewModel.SelectPage(page);
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>解析 <c>1280x720</c> 形式的窗口尺寸。</summary>
    private static bool TryParseSize(string raw, out double width, out double height)
    {
        width = 0;
        height = 0;
        string[] parts = raw.Split('x', 'X');
        return parts.Length == 2
               && double.TryParse(parts[0], out width)
               && double.TryParse(parts[1], out height)
               && width >= 800
               && height >= 560;
    }

    /// <summary>读取可重复出现的选项（<c>--set-option a --set-option b</c>）。</summary>
    private static IEnumerable<string> ReadOptions(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                yield return args[i + 1];
            }
        }
    }

    /// <summary>深度优先找第一个指定类型的子元素（调试截图用）。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>找出全部指定类型的子元素（调试截图用：把页面上所有 ScrollViewer 滚到底）。</summary>
    private static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        List<T> found = [];
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(FindAll<T>(child));
        }

        return found;
    }
}
