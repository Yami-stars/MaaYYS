using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaaYys.App.Services;
using MaaYys.Engine;
using MaaYys.Engine.Config;
using MaaYys.Engine.Devices;
using MaaYys.Engine.Logging;
using MaaYys.Engine.Resources;
using MaaYys.Engine.Session;

namespace MaaYys.App.ViewModels;

/// <summary>实例连接状态（标签条圆点着色依据）。</summary>
public enum InstanceState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Error = 3,
}

/// <summary>
/// 一个实例标签页的运行态宿主（P2.4）：持有该实例自己的引擎会话与帧采集泵。
/// 多开约束：
/// - 实例之间**互不复用** engine（句柄/资源/任务器都独立），但同一台设备只允许一个实例连接（<see cref="DeviceLease"/>）；
/// - 只有「当前激活标签」跑预览采集泵，切到后台的标签保持连接、停止抓帧，避免多实例抢 CPU。
/// </summary>
public sealed class InstanceViewModel : ViewModelBase, IDisposable
{
    private readonly InstanceStore _store;
    private readonly ResourceProvider? _resources;
    private readonly SynchronizationContext _uiContext;
    private readonly ILogSink? _logSink;
    private readonly LogOptions? _logOptions;
    private MaaYysEngine? _engine;
    private FramePump? _pump;
    private PreviewBitmap? _preview;
    private IDisposable? _frameSubscription;
    private string? _leasedKey;
    private bool _isActive;
    private bool _disposed;

    /// <summary>最近一帧（P5.5 截图卡片来源）：只在帧回调里换引用，错误日志需要时才做缩放。</summary>
    private FrameData? _latestFrame;

    /// <summary>已连接的游戏窗口句柄：用于运行期巡检「幽灵化」（见 GameWindowVisibility）。</summary>
    private IntPtr _windowHandle;

    private DispatcherTimer? _ghostWatch;

    public InstanceViewModel(
        InstanceDefinition definition,
        InstanceStore store,
        SynchronizationContext uiContext,
        ResourceProvider? resources = null,
        ILogSink? log = null,
        LogOptions? logOptions = null)
    {
        Definition = definition;
        _store = store;
        _uiContext = uiContext;
        _resources = resources;
        _logSink = log;
        _logOptions = logOptions;

        ConnectCommand = new RelayCommand(_ => _ = ConnectAsync(), _ => !IsBusy && State != InstanceState.Connected);
        DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => State == InstanceState.Connected);
        SaveScreenshotCommand = new RelayCommand(_ => SaveScreenshot(), _ => State == InstanceState.Connected);

        // 主窗口最小化 / 失焦时停掉预览采集（省 CPU，避免恢复窗口时卡顿）
        PreviewSuspension.Changed += OnPreviewSuspensionChanged;
    }

    /// <summary>配置快照（就是清单里的对象，字段变更后调 <see cref="NotifyDefinitionChanged"/>）。</summary>
    public InstanceDefinition Definition { get; }

    public string Id => Definition.Id;

    public string DisplayName => Definition.Name;

    public string DeviceSummary => Definition.DeviceSummary;

    /// <summary>标签条副标题：已连接时给实时帧率，否则给配置摘要。</summary>
    public string TabSubtitle => State == InstanceState.Connected
        ? $"已连接 · {ActualFps:F0}fps"
        : Definition.DeviceSummary;

    // ---- 运行态 ----

    private InstanceState _state = InstanceState.Disconnected;
    private string _resourceStatus = "未加载资源";

    /// <summary>资源加载状态文案（连接后按实例配置的资源集加载；失败不影响预览）。</summary>
    public string ResourceStatus
    {
        get => _resourceStatus;
        private set => SetProperty(ref _resourceStatus, value);
    }

    /// <summary>当前已加载的资源集（未加载为 null）。</summary>
    public ResourceChain? ActiveResource { get; private set; }

    public InstanceState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(TabSubtitle));
            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveScreenshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string StateText => State switch
    {
        InstanceState.Connecting => "连接中",
        InstanceState.Connected => "已连接",
        InstanceState.Error => "异常",
        _ => "未连接",
    };

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    private string _statusText = "未连接";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _diagnostic = string.Empty;
    public string Diagnostic
    {
        get => _diagnostic;
        private set => SetProperty(ref _diagnostic, value);
    }

    private string _frameInfo = "无画面";
    public string FrameInfo
    {
        get => _frameInfo;
        private set => SetProperty(ref _frameInfo, value);
    }

    private double _actualFps;
    public double ActualFps
    {
        get => _actualFps;
        private set
        {
            if (SetProperty(ref _actualFps, value))
            {
                OnPropertyChanged(nameof(TabSubtitle));
            }
        }
    }

    private ImageSource? _previewSource;
    public ImageSource? PreviewSource
    {
        get => _previewSource;
        private set => SetProperty(ref _previewSource, value);
    }

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand SaveScreenshotCommand { get; }

    /// <summary>当前引擎会话（P5 任务执行复用）。</summary>
    public MaaYysEngine? Engine => _engine;

    /// <summary>
    /// 是否当前激活标签。只有激活标签跑预览采集泵（后台标签保持连接但不抓帧）。
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetProperty(ref _isActive, value))
            {
                return;
            }

            UpdatePumpState();

            if (!value)
            {
                // 切到后台：停泵并释放预览缓冲（保留文字提示，画面等切回来再抓）
                ClearPreview();
                FrameInfo = "后台实例（切换到此标签恢复预览）";
            }
        }
    }

    // ---- 连接 ----

    private async Task ConnectAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        IReadOnlyList<string> errors = Definition.Validate();
        if (errors.Count > 0)
        {
            State = InstanceState.Error;
            StatusText = $"配置无效：{errors[0]}";
            LogConnect(LogLevel.Error, $"配置无效：{errors[0]}");
            return;
        }

        IsBusy = true;
        State = InstanceState.Connecting;
        PreviewSource = null;
        Diagnostic = string.Empty;
        LogConnect(LogLevel.Info, $"开始连接（{Definition.DeviceSummary}）");
        try
        {
            StatusText = $"正在定位设备（{Definition.DeviceSummary}）…";
            DeviceDescriptor? device = await ResolveDeviceAsync(Definition).ConfigureAwait(true);
            if (device is null)
            {
                State = InstanceState.Error;
                StatusText = Definition.DeviceKind == InstanceDeviceKind.Win32
                    ? $"未找到匹配窗口（标题正则「{Definition.Win32TitleRegex}」），请确认游戏已启动"
                    : $"未找到 ADB 设备（{Definition.AdbAddress}），请确认模拟器已启动且 adb 已连接";
                LogConnect(LogLevel.Warn, StatusText);
                return;
            }

            if (!DeviceLease.TryAcquire(device.LeaseKey, Id, out string? holder))
            {
                string holderName = _store.Find(holder ?? string.Empty)?.Name ?? holder ?? "其他实例";
                State = InstanceState.Error;
                StatusText = $"设备已被实例「{holderName}」占用（同一设备不能多实例同时连接）";
                LogConnect(LogLevel.Warn, StatusText);
                return;
            }

            _leasedKey = device.LeaseKey;

            MaaYysEngine engine = device.Kind == DeviceKind.Win32
                ? MaaYysEngine.CreateWin32(device.WindowHandle, BuildWin32Options())
                : MaaYysEngine.CreateAdb(device.Adb ?? throw new InvalidOperationException("ADB 设备信息缺失"));

            _engine = engine;
            Diagnostic = engine.ConnectDiagnostic;

            // P5.5：日志管线（实例名打标签 + 最近一帧作为错误截图来源）
            if (_logSink is not null)
            {
                LogOptions options = _logOptions ?? new LogOptions();
                engine.Log = new EngineLogPipeline(
                    new ScopedLogSink(_logSink, DisplayName),
                    options,
                    () => _latestFrame is null ? null : ThumbnailFactory.Create(_latestFrame, options.ThumbnailWidth));
            }

            EngineConnectResult result = await engine.ConnectAsync().ConfigureAwait(true);
            if (!result.Success)
            {
                CleanupConnection();
                State = InstanceState.Error;
                StatusText = $"连接失败：{result.Error}";
                LogConnect(LogLevel.Error, StatusText);
                return;
            }

            State = InstanceState.Connected;
            StatusText = $"已连接 · {engine.Controller} · {engine.Uuid}";
            LogConnect(LogLevel.Info, $"已连接（{engine.Controller}）");

            // 记住命中窗口：下次连接优先选它（游戏重开后按正则重扫）
            if (device.Kind == DeviceKind.Win32)
            {
                Definition.Win32WindowHandle = device.WindowHandle.ToInt64();
                _store.Save();

                _windowHandle = device.WindowHandle;

                // 连接时控制器会把最小化的游戏窗口 SW_RESTORE 回原位，但不会还原最小化期间
                // 被设上的 LAYERED/TRANSPARENT/alpha=0 —— 结果窗口在渲染、截图抓得到、桌面全透明。
                if (GameWindowVisibility.EnsureVisible(_windowHandle, out string fixDetail))
                {
                    Diagnostic = $"已修复游戏窗口可见性：{fixDetail}";
                }

                StartGhostWatch();
            }

            // P2.5：按实例配置的资源集加载 pipeline（base ← 渠道层，后加载覆盖同名节点）
            await LoadResourceAsync(engine).ConfigureAwait(true);

            UpdatePumpState();
        }
        catch (Exception ex)
        {
            CleanupConnection();
            State = InstanceState.Error;
            StatusText = $"连接异常：{ex.Message}";
            LogConnect(LogLevel.Error, StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 写一条「连接」日志（P6.1）：主界面日志面板要能看见连接失败，
    /// 否则面板在设备未启动时永远只有启动那一行，等于摆设。
    /// </summary>
    private void LogConnect(LogLevel level, string message)
        => _logSink.Write(level, "Engine", $"实例「{DisplayName}」{message}", "connect");

    /// <summary>
    /// 按实例配置的资源集加载资源链（P2.5）。失败只记录状态，**不影响已建立的连接与预览**——
    /// 没有 pipeline 资源时仍可用于调试设备/截图。
    /// </summary>
    private async Task LoadResourceAsync(MaaYysEngine engine)
    {
        ResourceChain? chain = _resources?.Resolve(Definition.ResourceName);
        string? fallbackNote = null;
        if (chain is null && _resources is { Chains.Count: > 0 })
        {
            // 实例默认值是 "base"，而真实清单里通常叫「官服/官服2/B站服…」：回退到第一个可用资源集
            chain = _resources.Chains.FirstOrDefault(c => c.IsUsable) ?? _resources.Chains[0];
            fallbackNote = $"配置的资源集「{Definition.ResourceName}」不在清单中，已回退到「{chain.DisplayName}」";
        }

        if (chain is null)
        {
            ResourceStatus = _resources?.Loaded == true
                ? $"资源集「{Definition.ResourceName}」不存在（可选：{string.Join(" / ", _resources.Chains.Select(c => c.Name))}）"
                : "未找到 interface.json：仅预览模式，无法执行任务";
            ActiveResource = null;
            return;
        }

        if (!chain.IsUsable)
        {
            ResourceStatus = $"资源集「{chain.DisplayName}」没有可加载的包";
            ActiveResource = null;
            return;
        }

        ResourceStatus = $"正在加载 {chain.DisplayName}…";
        ResourceLoadResult result = await engine.LoadResourceChainAsync(chain.BundlePaths).ConfigureAwait(true);
        if (result.Success)
        {
            ActiveResource = chain;

            // P3.5：渠道身份在界面上要能看见。包名只在 Adb 控制器下有意义
            // （Win32 桌面端由用户自己开游戏，脚本不去启动进程）。
            ChannelPackages packages = ChannelLayerResolver.ResolvePackages(chain);
            ResourceStatus = $"资源已加载：{chain.DisplayName}（{result.LoadedBundles.Count} 个包）"
                             + (packages.IsEmpty ? string.Empty : $" · 渠道包名 {packages.Stop ?? "—"}")
                             + (chain.IsComplete ? string.Empty : $" · ⚠ 缺 {chain.MissingPaths.Count} 个目录")
                             + (fallbackNote is null ? string.Empty : $" · {fallbackNote}");

            // 回退命中的资源集写回配置，避免每次连接都走回退
            if (fallbackNote is not null)
            {
                Definition.ResourceName = chain.Name;
                _store.Save();
            }
        }
        else
        {
            ActiveResource = null;
            ResourceStatus = $"资源加载失败：{result.Error}";
        }
    }

    /// <summary>
    /// 热切换资源集（P2.5）：不重连、不重启，直接替换引擎内部的资源实例。
    /// 成功后会同步写回实例定义并持久化。
    /// </summary>
    public async Task<(bool Success, string Message)> SwitchResourceAsync(
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        if (_engine is null || State != InstanceState.Connected)
        {
            return (false, "请先连接实例再切换资源集");
        }

        ResourceChain? chain = _resources?.Resolve(resourceName);
        if (chain is null)
        {
            return (false, $"资源集「{resourceName}」不存在");
        }

        if (!chain.IsUsable)
        {
            return (false, $"资源集「{chain.DisplayName}」没有可加载的包");
        }

        ResourceStatus = $"正在切换到 {chain.DisplayName}…";
        ResourceLoadResult result = await _engine.LoadResourceChainAsync(chain.BundlePaths, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            ResourceStatus = $"切换失败（仍使用 {ActiveResource?.DisplayName ?? "旧资源"}）：{result.Error}";
            return (false, result.Error ?? "未知错误");
        }

        ActiveResource = chain;
        ResourceStatus = $"已切换到 {chain.DisplayName}（{result.LoadedBundles.Count} 个包）";

        Definition.ResourceName = resourceName;
        Definition.UpdatedAt = DateTimeOffset.Now;
        _store.Save();
        OnPropertyChanged(nameof(Definition));
        return (true, ResourceStatus);
    }

    /// <summary>
    /// 组装 Win32 控制器参数：实例自己的设置优先，没填（空串）时回落到
    /// interface.json 的 <c>controller[].win32</c> 段（P3.6）。
    /// </summary>
    private Win32EngineOptions BuildWin32Options()
    {
        Win32Defaults? fallback = _resources?.Win32Defaults;
        string screencap = string.IsNullOrWhiteSpace(Definition.ScreencapMethod)
            ? fallback?.Screencap ?? string.Empty
            : Definition.ScreencapMethod;
        string input = string.IsNullOrWhiteSpace(Definition.InputMethod)
            ? fallback?.Mouse ?? string.Empty
            : Definition.InputMethod;

        return new Win32EngineOptions
        {
            PreferredScreencapMethod = MaaWin32MethodNames.ParseScreencap(screencap),
            MouseMethod = MaaWin32MethodNames.ParseInput(input),
            KeyboardMethod = MaaWin32MethodNames.ParseInput(input),
        };
    }

    /// <summary>
    /// 按实例配置定位设备：Win32 用标题/类名正则（优先上次句柄），ADB 按地址精确匹配。
    /// 扫描放后台线程，避免阻塞 UI。
    /// </summary>
    private async Task<DeviceDescriptor?> ResolveDeviceAsync(InstanceDefinition definition)
    {
        bool isWin32 = definition.DeviceKind == InstanceDeviceKind.Win32;
        Win32Defaults? fallback = _resources?.Win32Defaults;

        // 实例的正则为空时回落到 interface.json（P3.6）
        string? titleRegex = isWin32
            ? NullIfEmpty(definition.Win32TitleRegex) ?? fallback?.TitleRegex
            : null;
        string? classRegex = isWin32
            ? NullIfEmpty(definition.Win32ClassRegex) ?? fallback?.ClassRegex
            : null;
        string adbAddress = definition.AdbAddress;

        IReadOnlyList<DeviceDescriptor> devices = await Task.Run(
            () => DeviceScanner.ScanAll(titleRegex, classRegex, includeAdb: !isWin32)).ConfigureAwait(true);

        if (isWin32)
        {
            return devices.FirstOrDefault(d => d.Kind == DeviceKind.Win32 && d.WindowHandle.ToInt64() == definition.Win32WindowHandle)
                   ?? devices.FirstOrDefault(d => d.Kind == DeviceKind.Win32);
        }

        return devices.FirstOrDefault(d => d.Kind == DeviceKind.Adb &&
                                           string.Equals(d.Adb?.Address, adbAddress, StringComparison.OrdinalIgnoreCase))
               ?? devices.FirstOrDefault(d => d.Kind == DeviceKind.Adb &&
                                              d.Detail.Contains(adbAddress, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 统一判定预览是否该跑：当前激活标签 + 会话已连接 + 窗口未最小化。
    /// 三个条件任一不满足就停泵（后台标签/最小化都不抓帧）。
    /// </summary>
    private void UpdatePumpState()
    {
        bool shouldRun = _isActive
                         && State == InstanceState.Connected
                         && !PreviewSuspension.IsSuspended
                         && _engine is not null;

        if (shouldRun)
        {
            if (_pump is null)
            {
                StartFramePump(_engine!);
            }
        }
        else
        {
            StopFramePump();
        }

        OnPropertyChanged(nameof(TabSubtitle));
    }

    private void OnPreviewSuspensionChanged(object? sender, EventArgs e) => UpdatePumpState();

    /// <summary>
    /// 启动幽灵窗口巡检：每 2 秒检查游戏窗口是否「已非最小化却完全透明」。
    /// 用独立定时器而不是挂在帧回调上——窗口最小化时抓不到帧、帧回调不跑，挂在帧上的巡检就永远不会触发。
    /// </summary>
    private void StartGhostWatch()
    {
        if (_ghostWatch is not null || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        _ghostWatch = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _ghostWatch.Tick += OnGhostWatchTick;
        _ghostWatch.Start();
    }

    private void StopGhostWatch()
    {
        if (_ghostWatch is null)
        {
            return;
        }

        _ghostWatch.Stop();
        _ghostWatch.Tick -= OnGhostWatchTick;
        _ghostWatch = null;
    }

    private void OnGhostWatchTick(object? sender, EventArgs e)
    {
        // 显式判断：只有「确实连着游戏窗口」才巡检。
        // 不依赖「定时器只在连接期间存在」这种隐式约束——状态可能因异常路径残留。
        if (_windowHandle == IntPtr.Zero || _engine is null || State != InstanceState.Connected)
        {
            return;
        }

        if (GameWindowVisibility.EnsureVisible(_windowHandle, out string detail))
        {
            Diagnostic = $"运行中修复游戏窗口可见性：{detail}";
        }
    }

    private void StartFramePump(MaaYysEngine engine)
    {
        StopFramePump();

        _pump = new FramePump(_uiContext);
        _frameSubscription = _pump.Frames.Subscribe(frame =>
        {
            try
            {
                _latestFrame = frame;
                // 复用同一个 WriteableBitmap 原地写入：每帧新建 BitmapSource 会产生
                // 2.7MB/帧 的 LOH 分配，最小化挂后台时实测把 CPU 打到 100%+（GC 抖动）。
                WriteableBitmap bitmap = (_preview ??= new PreviewBitmap()).Update(frame);
                if (!ReferenceEquals(PreviewSource, bitmap))
                {
                    PreviewSource = bitmap;
                }

                ActualFps = _pump?.ActualFps ?? 0;
                FrameInfo = $"{frame.Width}×{frame.Height} · 第 {frame.Sequence} 帧 · 非黑 {frame.NonBlackRatio:P1}"
                    + (frame.HasSevereBlackBorder
                        ? $" · ⚠ 黑边 右{frame.Stats.BlackBorderRight}px 下{frame.Stats.BlackBorderBottom}px"
                        : string.Empty);
            }
            catch (NotSupportedException ex)
            {
                FrameInfo = $"位图转换失败：{ex.Message}";
            }
        });

        _pump.Start(() => engine.CaptureFrame(), Definition.PreviewFps);
    }

    private void StopFramePump()
    {
        _frameSubscription?.Dispose();
        _frameSubscription = null;
        _pump?.Dispose();
        _pump = null;
        _latestFrame = null;   // 停泵即断开引用，避免日志卡片拿到过期的旧帧
        ActualFps = 0;
    }

    private void Disconnect()
    {
        StopFramePump();
        CleanupConnection();
        State = InstanceState.Disconnected;
        ClearPreview();
        FrameInfo = "无画面";
        StatusText = "已断开";
        LogConnect(LogLevel.Info, "已断开连接");
    }

    private void ClearPreview()
    {
        _preview?.Clear();
        _preview = null;
        PreviewSource = null;
    }

    private void CleanupConnection()
    {
        StopFramePump();
        ClearPreview();
        _engine?.Dispose();
        _engine = null;

        // 句柄只在连接期间有效：断开后不能再对可能已销毁的窗口做巡检
        StopGhostWatch();
        _windowHandle = IntPtr.Zero;

        ActiveResource = null;
        ResourceStatus = "未加载资源";

        if (_leasedKey is not null)
        {
            DeviceLease.Release(_leasedKey, Id);
            _leasedKey = null;
        }
    }

    private void SaveScreenshot()
    {
        if (_engine is null)
        {
            return;
        }

        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"shot-{Definition.Name}-{DateTime.Now:HHmmss}.bmp");
            ScreenshotResult? result = _engine.SaveScreenshot(path);
            StatusText = result is null ? "截图保存失败" : $"已保存 {path}（{result.Width}×{result.Height}）";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>清单更新后刷新显示字段（引擎/采集泵不受影响）。</summary>
    public void NotifyDefinitionChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DeviceSummary));
        OnPropertyChanged(nameof(TabSubtitle));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PreviewSuspension.Changed -= OnPreviewSuspensionChanged;
        CleanupConnection();
        DeviceLease.ReleaseAll(Id);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
