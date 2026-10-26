using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaaYys.App.Services;
using MaaYys.Engine;
using MaaYys.Engine.Devices;
using MaaYys.Engine.Session;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 连接页（P2.3 设备管理）：设备自动探测、连接/断开、实时截图预览、帧率与输入方式可调。
/// 引擎在 UI 线程创建，保证事件回派到 Dispatcher。
/// </summary>
public sealed class ConnectionViewModel : ViewModelBase, IDisposable
{
    private readonly SynchronizationContext _uiContext;
    private MaaYysEngine? _engine;
    private FramePump? _pump;
    private PreviewBitmap? _preview;
    private IDisposable? _frameSubscription;
    private bool _disposed;

    /// <summary>已连接的游戏窗口句柄：用于运行期巡检「幽灵化」（见 GameWindowVisibility）。</summary>
    private IntPtr _windowHandle;

    private DispatcherTimer? _ghostWatch;

    public ConnectionViewModel()
    {
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        ConnectCommand = new RelayCommand(_ => _ = ConnectAsync(), _ => !IsConnected && !IsBusy && SelectedDevice is not null);
        DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => IsConnected);
        SaveScreenshotCommand = new RelayCommand(_ => SaveScreenshot(), _ => IsConnected);

        // 主窗口最小化时停掉预览采集（省 CPU，避免恢复窗口时卡顿）
        PreviewSuspension.Changed += OnPreviewSuspensionChanged;

        _ = RefreshAsync();
    }

    // ---- 设备探测 ----

    public ObservableCollection<DeviceDescriptor> Devices { get; } = [];

    private DeviceDescriptor? _selectedDevice;
    public DeviceDescriptor? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>窗口标题正则（对应 interface.json 的 win32 window_regex，默认「阴阳师」）。</summary>
    private string _titleRegex = "阴阳师";
    public string TitleRegex
    {
        get => _titleRegex;
        set => SetProperty(ref _titleRegex, value);
    }

    private bool _includeAdb = true;
    public bool IncludeAdb
    {
        get => _includeAdb;
        set => SetProperty(ref _includeAdb, value);
    }

    // ---- 连接参数 ----

    public IReadOnlyList<string> ScreencapMethods { get; } =
        ["FramePool", "PrintWindow", "GDI", "DXGI_DesktopDup", "DXGI_DesktopDup_Window"];

    public IReadOnlyList<string> InputMethods { get; } =
        ["SendMessage", "PostMessage", "PostMessageWithCursorPos", "Seize", "LegacyEvent"];

    public IReadOnlyList<int> FpsOptions { get; } = [2, 5, 10, 15, 30];

    private string _selectedScreencapMethod = "FramePool";
    public string SelectedScreencapMethod
    {
        get => _selectedScreencapMethod;
        set => SetProperty(ref _selectedScreencapMethod, value);
    }

    private string _selectedInputMethod = "SendMessage";
    public string SelectedInputMethod
    {
        get => _selectedInputMethod;
        set => SetProperty(ref _selectedInputMethod, value);
    }

    private int _targetFps = 5;
    public int TargetFps
    {
        get => _targetFps;
        set
        {
            if (SetProperty(ref _targetFps, value))
            {
                _pump?.SetFps(value);
            }
        }
    }

    // ---- 状态 / 预览 ----

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value))
            {
                return;
            }

            (ConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveScreenshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

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
        private set => SetProperty(ref _actualFps, value);
    }

    private ImageSource? _previewSource;
    public ImageSource? PreviewSource
    {
        get => _previewSource;
        private set => SetProperty(ref _previewSource, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand SaveScreenshotCommand { get; }

    /// <summary>当前引擎会话（P5 任务执行将复用同一实例）。</summary>
    public MaaYysEngine? Engine => _engine;

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在扫描设备…";
        try
        {
            string titleRegex = TitleRegex;
            bool includeAdb = IncludeAdb;
            IReadOnlyList<DeviceDescriptor> found = await Task.Run(
                () => DeviceScanner.ScanAll(titleRegex, null, includeAdb)).ConfigureAwait(true);

            string? previous = SelectedDevice?.Detail;
            Devices.Clear();
            foreach (DeviceDescriptor device in found)
            {
                Devices.Add(device);
            }

            SelectedDevice = Devices.FirstOrDefault(d => d.Detail == previous) ?? Devices.FirstOrDefault();
            StatusText = found.Count == 0
                ? $"未找到设备（标题正则「{TitleRegex}」，请确认游戏已启动）"
                : $"发现 {found.Count} 个设备";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConnectAsync()
    {
        if (SelectedDevice is null || IsConnected)
        {
            return;
        }

        IsBusy = true;
        StatusText = "连接中…";
        Diagnostic = string.Empty;
        try
        {
            _engine?.Dispose();
            _engine = null;

            DeviceDescriptor device = SelectedDevice;
            MaaYysEngine engine = device.Kind == DeviceKind.Win32
                ? MaaYysEngine.CreateWin32(device.WindowHandle, new Win32EngineOptions
                {
                    PreferredScreencapMethod = MaaWin32MethodNames.ParseScreencap(SelectedScreencapMethod),
                    MouseMethod = MaaWin32MethodNames.ParseInput(SelectedInputMethod),
                    KeyboardMethod = MaaWin32MethodNames.ParseInput(SelectedInputMethod),
                })
                : MaaYysEngine.CreateAdb(device.Adb ?? throw new InvalidOperationException("ADB 设备信息缺失"));

            _engine = engine;
            Diagnostic = engine.ConnectDiagnostic;

            EngineConnectResult result = await engine.ConnectAsync().ConfigureAwait(true);
            if (!result.Success)
            {
                StatusText = $"连接失败：{result.Error}";
                return;
            }

            IsConnected = true;
            StatusText = $"已连接 · {engine.Controller} · {engine.Uuid}";

            // 控制器连接时会把最小化的游戏窗口 SW_RESTORE 回原位，但不还原最小化期间
            // 被设上的 LAYERED/TRANSPARENT/alpha=0 —— 表现为「预览有画面、桌面全透明」。
            if (device.Kind == DeviceKind.Win32)
            {
                _windowHandle = device.WindowHandle;
                if (GameWindowVisibility.EnsureVisible(_windowHandle, out string fixDetail))
                {
                    Diagnostic = $"已修复游戏窗口可见性：{fixDetail}";
                }

                StartGhostWatch();
            }

            UpdatePumpState();
        }
        catch (Exception ex)
        {
            StatusText = $"连接异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnPreviewSuspensionChanged(object? sender, EventArgs e) => UpdatePumpState();

    /// <summary>
    /// 幽灵窗口巡检（每 2 秒）：游戏中途被最小化后，控制器恢复它时可能又留下 alpha=0 的透明外壳。
    /// 独立于帧回调——窗口最小化时抓不到帧，挂在帧上的检查永远不会跑。
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
        // 显式判断：未连接时绝不巡检（不依赖定时器生命周期这种隐式约束）
        if (_windowHandle == IntPtr.Zero || _engine is null || !IsConnected)
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
                // 复用同一个 WriteableBitmap 原地写入（每帧新建 BitmapSource 会造成
                // 2.7MB/帧 的 LOH 分配与高频 GC，最小化挂后台时 CPU 飙升）
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

        _pump.Start(() => engine.CaptureFrame(), TargetFps);
    }

    private void StopFramePump()
    {
        _frameSubscription?.Dispose();
        _frameSubscription = null;
        _pump?.Dispose();
        _pump = null;
        ActualFps = 0;
    }

    /// <summary>释放预览缓冲（断开/最小化时回收显存与非托管缓冲）。</summary>
    private void ClearPreview()
    {
        _preview?.Clear();
        _preview = null;
        PreviewSource = null;
    }

    /// <summary>
    /// 统一判定预览是否该跑：已连接 + 窗口未最小化。
    /// 由连接成功与 <see cref="PreviewSuspension"/> 变化触发。
    /// </summary>
    private void UpdatePumpState()
    {
        if (_disposed)
        {
            return;
        }

        bool shouldRun = IsConnected && !PreviewSuspension.IsSuspended && _engine is not null;
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
    }

    private void Disconnect()
    {
        StopFramePump();
        StopGhostWatch();
        _engine?.Dispose();
        _engine = null;
        _windowHandle = IntPtr.Zero;
        IsConnected = false;
        ClearPreview();
        FrameInfo = "无画面";
        StatusText = "已断开";
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
            string path = Path.Combine(dir, $"shot-{DateTime.Now:HHmmss}.bmp");
            ScreenshotResult? result = _engine.SaveScreenshot(path);
            StatusText = result is null ? "截图保存失败" : $"已保存 {path}（{result.Width}×{result.Height}）";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PreviewSuspension.Changed -= OnPreviewSuspensionChanged;
        StopGhostWatch();
        StopFramePump();
        ClearPreview();
        _engine?.Dispose();
        _engine = null;
    }
}
