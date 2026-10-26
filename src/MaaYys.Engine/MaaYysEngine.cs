using MaaYys.Engine.Agent;
using MaaYys.Engine.Devices;
using MaaYys.Engine.Native;
using MaaYys.Engine.Resources;
using MaaYys.Engine.Session;

namespace MaaYys.Engine;

/// <summary>控制器类型。</summary>
public enum ControllerKind
{
    None = 0,
    Win32 = 1,
    Null = 2,
    Adb = 3,
}

/// <summary>Win32 控制器创建参数（对应 interface.json 的 win32 controller 字段）。</summary>
public sealed class Win32EngineOptions
{
    /// <summary>首选截图方法；null = FramePool（P1.4 实测结论）。</summary>
    public ulong? PreferredScreencapMethod { get; init; }

    /// <summary>鼠标输入方法，默认 SendMessage（后台点击）。</summary>
    public ulong MouseMethod { get; init; } = MaaWin32InputMethod.SendMessage;

    /// <summary>键盘输入方法，默认 SendMessage。</summary>
    public ulong KeyboardMethod { get; init; } = MaaWin32InputMethod.SendMessage;

    /// <summary>是否启用截图方法自动回退（连接失败/空图/全黑/黑边 &gt;10%）。</summary>
    public bool AutoFallback { get; init; } = true;
}

/// <summary>ADB 控制器创建参数（对应 interface.json 的 adb controller 字段，P2.3）。</summary>
public sealed class AdbEngineOptions
{
    /// <summary>截图方法位掩码；null = 用设备自报能力（MaaToolkit 探测结果）。</summary>
    public ulong? ScreencapMethods { get; init; }

    /// <summary>输入方法位掩码；null = 用设备自报能力。</summary>
    public ulong? InputMethods { get; init; }

    /// <summary>MaaAgentBinary 目录（模拟器 maatouch/minitouch 所需）；null 用框架默认。</summary>
    public string? AgentPath { get; init; }
}

/// <summary>连接结果。</summary>
public sealed record EngineConnectResult(
    bool Success,
    MaaStatus Status,
    string? Uuid,
    string Diagnostic,
    string? Error = null);

/// <summary>
/// 引擎会话：MaaResource / MaaController / MaaTasker 的统一生命周期宿主（P2.2）。
///
/// 设计约束（P1.6 评审 R6）：
/// - 上层只与本类交互，不接触任何原生句柄；资源、控制器、任务器全部由本类持有与释放。
/// - 释放顺序固定：Tasker → Controller → Resource（AgentClient 于 P4 接入，届时插在 Resource 之前）。
/// - 原生回调（框架工作线程）统一转成事件流：<see cref="Events"/> / <see cref="TaskStateChanges"/>；
///   若构造时存在 SynchronizationContext（WPF Dispatcher），事件会被派发回该上下文，UI 可直接订阅。
/// </summary>
public sealed class MaaYysEngine : IDisposable
{
    public static string Name => "MaaYys.Engine";

    private readonly MaaTasker _tasker;

    /// <summary>
    /// 当前资源实例。P2.5 起**可替换**：MaaFramework 没有「卸载 bundle」的 API，
    /// 切换渠道只能新建一个 MaaResource、按链加载完再重新绑定（见 <see cref="LoadResourceChainAsync"/>）。
    /// 读写都在 <see cref="_gate"/> 或构造/释放路径上进行。
    /// </summary>
    private MaaResource _resource;

    private readonly MaaWin32Controller? _win32Controller;
    private readonly MaaAdbController? _adbController;
    private readonly MaaNullController? _nullController;
    private readonly SynchronizationContext? _syncContext;
    private readonly EventStream<MaaFrameworkEventArgs> _events = new();
    private readonly EventStream<TaskStateChangedEventArgs> _taskStates = new();
    private readonly object _gate = new();

    private bool _disposed;
    private bool _connected;

    /// <summary>
    /// agent 会话（P4.1）：连接成功后拉起 Go agent 子进程并绑定自定义能力。
    /// 空控制器（干跑/测试）不创建；其余失败降级为 null，主流程不受影响。
    /// 注意：切换渠道导致资源被替换（LoadResourceChainAsync）后，此会话会失效，
    /// 需在资源重载后重新拉起（P6 资源热切换时处理）。
    /// </summary>
    private MaaAgentSession? _agentSession;

    private MaaYysEngine(
        MaaResource resource,
        MaaTasker tasker,
        MaaWin32Controller? win32,
        MaaAdbController? adb,
        MaaNullController? nullController,
        ControllerKind kind,
        ulong screencapMethod,
        string diagnostic,
        bool marshalToSynchronizationContext = true)
    {
        _resource = resource;
        _tasker = tasker;
        _win32Controller = win32;
        _adbController = adb;
        _nullController = nullController;
        Controller = kind;
        ScreencapMethod = screencapMethod;
        ConnectDiagnostic = diagnostic;
        _syncContext = marshalToSynchronizationContext ? SynchronizationContext.Current : null;

        // 原生事件 → 事件流（回调运行在框架工作线程）
        _tasker.EventReceived += OnTaskerEvent;
        _resource.EventReceived += OnResourceEvent;

        // 状态机 → 状态事件流
        Tasks.StateChanged += (_, e) => Publish(_taskStates, e);
    }

    /// <summary>使用 Win32 控制器创建会话（桌面端）。</summary>
    public static MaaYysEngine CreateWin32(IntPtr hWnd, Win32EngineOptions? options = null)
    {
        if (hWnd == IntPtr.Zero)
        {
            throw new ArgumentException("窗口句柄为空", nameof(hWnd));
        }

        options ??= new Win32EngineOptions();
        MaaResource resource = new();
        MaaTasker tasker = new();
        try
        {
            if (options.AutoFallback)
            {
                MaaWin32Controller controller = MaaWin32Controller.CreateWithFallback(
                    hWnd,
                    out ulong used,
                    out string diagnostic,
                    options.MouseMethod,
                    options.KeyboardMethod,
                    options.PreferredScreencapMethod);
                return new MaaYysEngine(resource, tasker, controller, null, null, ControllerKind.Win32, used, diagnostic);
            }

            MaaWin32Controller direct = MaaWin32Controller.Create(
                hWnd,
                options.PreferredScreencapMethod ?? MaaWin32ScreencapMethod.FramePool,
                options.MouseMethod,
                options.KeyboardMethod);
            return new MaaYysEngine(resource, tasker, direct, null, null, ControllerKind.Win32, direct.ScreencapMethod, "未启用自动回退");
        }
        catch
        {
            tasker.Dispose();
            resource.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 使用空控制器创建会话：无设备环境下跑通引擎链路（P1.2 用法），
    /// 也是单元测试（连接/断开/任务投递）的被测对象。
    /// </summary>
    /// <param name="marshalToSynchronizationContext">
    /// 是否把事件派发回创建时的 SynchronizationContext（WPF 必须开）。
    /// 控制台/测试宿主可关闭，事件即在触发线程同步送达，便于确定性断言。
    /// </param>
    public static MaaYysEngine CreateNull(bool marshalToSynchronizationContext = true)
    {
        MaaResource resource = new();
        MaaTasker tasker = new();
        try
        {
            MaaNullController controller = MaaNullController.Create();
            return new MaaYysEngine(resource, tasker, null, null, controller, ControllerKind.Null, 0,
                "空控制器（干跑，1x1 静态截图）", marshalToSynchronizationContext);
        }
        catch
        {
            tasker.Dispose();
            resource.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 使用 ADB 控制器创建会话（模拟器）。
    /// 方法位掩码优先取 options，其次取设备自报能力（MaaToolkit 探测），最后退回框架默认集合。
    /// </summary>
    public static MaaYysEngine CreateAdb(AdbDeviceInfo device, AdbEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        options ??= new AdbEngineOptions();

        ulong screencap = options.ScreencapMethods ?? device.ScreencapMethods;
        ulong input = options.InputMethods ?? device.InputMethods;
        if (screencap == 0)
        {
            screencap = (ulong)MaaAdbScreencapMethod.Default;
        }

        if (input == 0)
        {
            input = (ulong)MaaAdbInputMethod.Default;
        }

        MaaResource resource = new();
        MaaTasker tasker = new();
        try
        {
            MaaAdbController controller = MaaAdbController.Create(
                device.AdbPath, device.Address, screencap, input, device.Config, options.AgentPath);
            return new MaaYysEngine(resource, tasker, null, controller, null, ControllerKind.Adb, screencap,
                $"{device.Name} @ {device.Address}");
        }
        catch
        {
            tasker.Dispose();
            resource.Dispose();
            throw;
        }
    }

    /// <summary>控制器类型。</summary>
    public ControllerKind Controller { get; }

    /// <summary>实际使用的截图方法（Win32 / Adb 位掩码；空控制器为 0）。</summary>
    public ulong ScreencapMethod { get; }

    /// <summary>截图方法选择过程 / 创建诊断信息。</summary>
    public string ConnectDiagnostic { get; }

    /// <summary>任务状态机（可查询历史任务记录）。</summary>
    public TaskStateMachine Tasks { get; } = new();

    /// <summary>原生事件流（Resource.Loading.* / Tasker.Task.* / Node.*）。</summary>
    public IObservable<MaaFrameworkEventArgs> Events => _events;

    /// <summary>任务状态变更流（已按状态机校验）。</summary>
    public IObservable<TaskStateChangedEventArgs> TaskStateChanges => _taskStates;

    /// <summary>
    /// 会话是否已连接。语义为「引擎已完成 ConnectAsync 且底层控制器自报已连接」，
    /// 而不是直接读底层状态——空控制器的 connected 回调恒真，直接读会误报未连接即已连接。
    /// </summary>
    public bool Connected
    {
        get
        {
            if (_disposed || !_connected)
            {
                return false;
            }

            return ControllerConnected;
        }
    }

    /// <summary>是否已连接且任务器已绑定资源与控制器（可投递任务）。</summary>
    public bool Ready => !_disposed && _connected && _tasker.Inited;

    /// <summary>资源是否加载完成。</summary>
    public bool ResourceLoaded => !_disposed && _resource.Loaded;

    /// <summary>
    /// 读取节点**当前已合并**的定义（base ← 渠道覆盖层叠加后的结果），节点不存在返回 null。
    /// 用于验证「同名覆盖」是否真的生效（P2.5 验收）。
    /// </summary>
    public string? GetResourceNodeData(string nodeName)
    {
        if (_disposed || string.IsNullOrEmpty(nodeName))
        {
            return null;
        }

        lock (_gate)
        {
            return _resource.GetNodeData(nodeName);
        }
    }

    /// <summary>控制器 uuid（未连接返回 null）。</summary>
    public string? Uuid => _win32Controller?.Uuid ?? _adbController?.Uuid ?? _nullController?.Uuid;

    /// <summary>
    /// 连接控制器并绑定资源/控制器到任务器（幂等）。
    /// </summary>
    public async Task<EngineConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            if (_connected)
            {
                return new EngineConnectResult(true, MaaStatus.Succeeded, Uuid, ConnectDiagnostic);
            }
        }

        MaaStatus status;
        try
        {
            status = await Task.Run(ConnectController, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new EngineConnectResult(false, MaaStatus.Failed, null, ConnectDiagnostic, "连接已取消");
        }
        catch (Exception ex)
        {
            return new EngineConnectResult(false, MaaStatus.Failed, null, ConnectDiagnostic, ex.Message);
        }

        if (status != MaaStatus.Succeeded)
        {
            return new EngineConnectResult(false, status, null, ConnectDiagnostic, $"控制器连接失败（{status}）");
        }

        if (!_tasker.BindResource(_resource))
        {
            return new EngineConnectResult(false, MaaStatus.Failed, Uuid, ConnectDiagnostic, "BindResource 失败");
        }

        if (!_tasker.BindController(ControllerHandle))
        {
            return new EngineConnectResult(false, MaaStatus.Failed, Uuid, ConnectDiagnostic, "BindController 失败");
        }

        lock (_gate)
        {
            _connected = _tasker.Inited;
        }

        // P4.1：连接成功后拉起 agent 子进程，把自定义动作/识别能力挂到当前资源上。
        // 空控制器（干跑/测试）不拉起；其余失败降级为「无 agent」，主流程不受影响。
        if (_connected && Controller != ControllerKind.Null)
        {
            _agentSession = MaaAgentSession.TryStart(_resource);
            if (_agentSession is null)
            {
                Console.Error.WriteLine("[engine] agent 子进程启动失败，自定义动作/识别将不可用（已降级）");
            }
            else
            {
                Console.WriteLine(
                    $"[engine] agent 已连接：{_agentSession.ActionNames.Length} 动作 / {_agentSession.RecognitionNames.Length} 识别 @port {_agentSession.Port}");
            }
        }

        return new EngineConnectResult(_connected, MaaStatus.Succeeded, Uuid, ConnectDiagnostic,
            _connected ? null : "任务器未就绪（资源/控制器绑定不完整）");
    }

    /// <summary>断开连接（可重连；仅停止控制器与任务器状态，不释放资源）。</summary>
    public void Disconnect()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            if (!_connected)
            {
                return;
            }

            _connected = false;
        }

        Tasks.MarkStopped("连接已断开");
    }

    /// <summary>
    /// 加载资源包（可多次调用，后加载覆盖先加载的同名节点，对应 PI V2 的 base ← 渠道层语义）。
    /// </summary>
    public async Task<bool> LoadResourceAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        long id = _resource.PostBundle(bundlePath);
        if (id == 0)
        {
            throw new InvalidOperationException($"资源加载请求被拒绝：{bundlePath}");
        }

        MaaStatus status = await Task.Run(() => _resource.Wait(id), cancellationToken).ConfigureAwait(false);
        return status == MaaStatus.Succeeded;
    }

    /// <summary>当前已加载的资源包链（按加载顺序，后者覆盖前者同名节点）。</summary>
    public IReadOnlyList<string> ResourceChain { get; private set; } = [];

    /// <summary>
    /// 当前资源链上「启动/关闭游戏」用的包名（P3.5）。
    /// 直读文件而非走 <c>MaaResourceGetNodeData</c>：base 的「启动游戏」是 DirectHit，
    /// 框架会把渠道层写的 <c>package</c> 当未知字段丢掉，节点数据里读不出来。
    /// Adb 控制器下由调用方显式 <c>StartApp</c>，Win32 下这两个值没有意义。
    /// </summary>
    public ChannelPackages ChannelPackages => ChannelLayerResolver.ResolvePackages(
        new ResourceChain("current", "current", ResourceChain, []));

    /// <summary>
    /// 按链加载资源并**整体替换**当前资源（P2.5 渠道热切换）。
    ///
    /// 语义：MaaFramework 只提供 <c>MaaResourcePostBundle</c>，没有卸载能力；因此切换渠道 =
    /// 新建一个 <see cref="MaaResource"/> → 按顺序 PostBundle 全部 bundle（后加载覆盖同名节点）→
    /// 全部成功后再 <c>BindResource</c> 到任务器 → 释放旧资源。
    /// **任何一个 bundle 失败都不会动当前资源**，保证切换失败时不把已有会话搞挂。
    /// </summary>
    public async Task<ResourceLoadResult> LoadResourceChainAsync(
        IReadOnlyList<string> bundlePaths,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (bundlePaths is null || bundlePaths.Count == 0)
        {
            return new ResourceLoadResult(false, "资源链为空", []);
        }

        var fresh = new MaaResource();
        var loaded = new List<string>();
        try
        {
            foreach (string path in bundlePaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                long id = fresh.PostBundle(path);
                if (id == 0)
                {
                    throw new InvalidOperationException($"资源包请求被拒绝：{path}");
                }

                MaaStatus status = await Task.Run(() => fresh.Wait(id), cancellationToken).ConfigureAwait(false);
                if (status != MaaStatus.Succeeded)
                {
                    throw new InvalidOperationException($"资源包加载失败（{status}）：{path}");
                }

                loaded.Add(path);
            }

            if (loaded.Count == 0)
            {
                throw new InvalidOperationException("资源链里没有可加载的包");
            }
        }
        catch (OperationCanceledException)
        {
            fresh.Dispose();
            return new ResourceLoadResult(false, "资源加载已取消", loaded);
        }
        catch (Exception ex)
        {
            fresh.Dispose();
            return new ResourceLoadResult(false, ex.Message, loaded);
        }

        MaaResource previous;
        lock (_gate)
        {
            if (_disposed)
            {
                fresh.Dispose();
                return new ResourceLoadResult(false, "引擎已释放", loaded);
            }

            // 已连接：新资源必须能重新绑定到任务器，否则整体放弃，旧资源继续用
            if (_connected && !_tasker.BindResource(fresh))
            {
                fresh.Dispose();
                return new ResourceLoadResult(
                    false,
                    "新资源无法绑定到任务器（可能有任务正在运行，请先停止任务）",
                    loaded);
            }

            fresh.EventReceived += OnResourceEvent;
            previous = _resource;
            _resource = fresh;
            ResourceChain = loaded;
            previous.EventReceived -= OnResourceEvent;
        }

        previous.Dispose();
        return new ResourceLoadResult(true, null, loaded);
    }

    /// <summary>
    /// 投递任务并等待完成。状态流转经 <see cref="TaskStateMachine"/> 记录，事件经 <see cref="TaskStateChanges"/> 推送。
    /// </summary>
    public async Task<TaskRun> RunTaskAsync(
        string entry,
        string? pipelineOverride = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);

        if (!_tasker.Inited)
        {
            throw new InvalidOperationException("引擎未就绪：请先 ConnectAsync() 并加载资源");
        }

        long taskId = _tasker.PostTask(entry, pipelineOverride);
        if (taskId == 0)
        {
            return TaskRun.Rejected(entry, "PostTask 返回 0：资源/控制器未绑定");
        }

        TaskRun run = Tasks.Start(taskId, entry, pipelineOverride);

        // 框架不支持取消投递中的任务，只能请求停止；注册后让 Wait 尽快返回
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                _tasker.PostStop();
            }
            catch
            {
                // ignored
            }
        });

        try
        {
            MaaStatus finalStatus = await Task.Run(() => _tasker.Wait(taskId), CancellationToken.None)
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested && !run.IsTerminal)
            {
                Tasks.MarkStopped("任务已取消");
            }

            return Tasks.EnsureFinal(run, finalStatus);
        }
        catch (Exception ex)
        {
            return Tasks.EnsureFinal(run, MaaStatus.Failed, ex.Message);
        }
    }

    /// <summary>请求停止当前任务（不等待）。</summary>
    public bool Stop()
    {
        ThrowIfDisposed();
        try
        {
            long stopId = _tasker.PostStop();
            if (stopId == 0)
            {
                return false;
            }

            Tasks.MarkStopped("用户停止");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>是否仍有任务在运行。</summary>
    public bool Running => !_disposed && _tasker.Running;

    /// <summary>截图一次（Win32 / Adb；返回黑边/亮度的统计信息）。</summary>
    public ScreenshotResult? Capture() => _win32Controller?.Capture() ?? _adbController?.Capture();

    /// <summary>截图并保存为 BMP（排障用）。</summary>
    public ScreenshotResult? SaveScreenshot(string path) =>
        _win32Controller?.SaveScreenshot(path) ?? _adbController?.SaveScreenshot(path);

    /// <summary>点击（截图坐标系）。主界面操作前需自行置前台（P1.6 输入分层结论）。</summary>
    public MaaStatus Click(int x, int y) =>
        _win32Controller?.Click(x, y) ?? _adbController?.Click(x, y) ?? MaaStatus.Invalid;

    /// <summary>滑动（截图坐标系）。</summary>
    public MaaStatus Swipe(int x1, int y1, int x2, int y2, int durationMs)
        => _win32Controller?.Swipe(x1, y1, x2, y2, durationMs)
           ?? _adbController?.Swipe(x1, y1, x2, y2, durationMs)
           ?? MaaStatus.Invalid;

    /// <summary>采集一帧（含原始像素，P2.3 实时预览用）。</summary>
    public FrameData? CaptureFrame() =>
        _win32Controller?.CaptureFrame() ?? _adbController?.CaptureFrame();

    /// <summary>
    /// 启动游戏（P3.5 渠道服）。intent 缺省用当前资源链的渠道包名。
    /// 只有 Adb 控制器真正实现；Win32 / Null 返回 <see cref="MaaStatus.Invalid"/>（桌面端不需要脚本启动进程）。
    /// </summary>
    public MaaStatus StartApp(string? intent = null)
        => _adbController?.StartApp(intent ?? ChannelPackages.Start)
           ?? _win32Controller?.StartApp(intent ?? ChannelPackages.Start)
           ?? MaaStatus.Invalid;

    /// <summary>关闭游戏（P3.5 渠道服）。包名缺省用当前资源链的渠道包名。</summary>
    public MaaStatus StopApp(string? package = null)
        => _adbController?.StopApp(package ?? ChannelPackages.Stop)
           ?? _win32Controller?.StopApp(package ?? ChannelPackages.Stop)
           ?? MaaStatus.Invalid;

    private IntPtr ControllerHandle =>
        _win32Controller?.Handle ?? _adbController?.Handle ?? _nullController?.Handle ?? IntPtr.Zero;

    private bool ControllerConnected =>
        _win32Controller?.Connected ?? _adbController?.Connected ?? _nullController?.Connected ?? false;

    private MaaStatus ConnectController() =>
        _win32Controller?.Connect() ?? _adbController?.Connect() ?? _nullController!.Connect();

    private void OnTaskerEvent(object? sender, MaaFrameworkEventArgs e)
    {
        if (e.Message is not null)
        {
            Tasks.ApplyEvent(e.Message, e.DetailsJson);
        }

        Publish(_events, e);
    }

    private void OnResourceEvent(object? sender, MaaFrameworkEventArgs e) => Publish(_events, e);

    /// <summary>事件派发：优先回到构造时的 SynchronizationContext（WPF UI 线程）。</summary>
    private void Publish<T>(EventStream<T> stream, T value)
    {
        SynchronizationContext? context = _syncContext;
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            stream.Publish(value);
            return;
        }

        context.Post(_ => stream.Publish(value), null);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>释放顺序固定：Tasker → Controller → Resource（P1.6 评审 R6）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Tasks.MarkStopped("引擎释放");
            _tasker.EventReceived -= OnTaskerEvent;
            _resource.EventReceived -= OnResourceEvent;

            _tasker.Dispose();
            _win32Controller?.Dispose();
            _adbController?.Dispose();
            _nullController?.Dispose();
            _agentSession?.Dispose();
            _resource.Dispose();
        }
        finally
        {
            _events.Complete();
            _taskStates.Complete();
        }
    }
}
