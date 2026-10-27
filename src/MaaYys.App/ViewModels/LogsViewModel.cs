using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaaYys.App.Services;
using MaaYys.Engine.Logging;

namespace MaaYys.App.ViewModels;

/// <summary>日志表格的一行（构造后只读，界面可直接绑定）。</summary>
public sealed class LogRowViewModel
{
    public LogRowViewModel(LogEntry entry, LogOptions options)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);

        Entry = entry;
        TimeText = options.FormatTime(entry.Time);
        Level = entry.Level;
        LevelText = LogLevels.Abbreviation(entry.Level);
        Source = entry.Source;
        Scope = entry.Scope;
        Message = entry.Message;
        Details = entry.Details;
        ToolTipText = string.IsNullOrWhiteSpace(entry.Details)
            ? $"{TimeText}  {LevelText}  {entry.Message}"
            : $"{TimeText}  {LevelText}{Environment.NewLine}{entry.Message}{Environment.NewLine}{Environment.NewLine}{entry.Details}";

        if (entry.Thumbnail is not null)
        {
            try
            {
                Thumbnail = FrameBitmapConverter.ToBitmap(entry.Thumbnail);
            }
            catch (NotSupportedException)
            {
                Thumbnail = null;   // 通道数不支持时降级为无图，不影响日志展示
            }
        }
    }

    public LogEntry Entry { get; }

    public string TimeText { get; }

    public LogLevel Level { get; }

    public string LevelText { get; }

    public string Source { get; }

    public string Scope { get; }

    public string Message { get; }

    public string? Details { get; }

    public string ToolTipText { get; }

    public BitmapSource? Thumbnail { get; }

    public bool HasThumbnail => Thumbnail is not null;

    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
}

/// <summary>
/// 日志页（P5.5）：级别/来源/关键字过滤、时间格式、自动滚动、截图卡片、导出（文本 / CSV + 截图目录）。
/// <para>
/// 性能设计（目标：长跑 2h 不卡顿）：
/// 1. 不订阅逐条事件，而是 200ms 轮询 <see cref="LogBuffer.Version"/>，无变化时零开销；
/// 2. 界面只保留最近 <c>MaxRows</c> 行，积压过多时整体重建而不是逐条插入；
/// 3. ListBox 开启虚拟化 + Recycling，滚动不重建容器。
/// </para>
/// </summary>
public sealed class LogsViewModel : ViewModelBase, IDisposable
{
    /// <summary>界面保留行数（缓冲里仍有全量，导出按过滤条件从缓冲取）。</summary>
    private const int MaxRows = 2000;

    private static readonly LogLevel[] LevelThresholds =
    [
        LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error,
    ];

    private readonly InstanceWorkspace? _workspace;
    private readonly DispatcherTimer? _timer;

    private long _seenVersion = -1;
    private long _seenSeq;
    private int _levelIndex = 2;
    private int _sourceIndex;
    private int _timeFormatIndex;
    private string _keyword = string.Empty;
    private bool _autoScroll = true;
    private bool _paused;
    private bool _nodeEvents;
    private string _message = string.Empty;
    private bool _disposed;

    public LogsViewModel()
        : this(null)
    {
    }

    public LogsViewModel(InstanceWorkspace? workspace)
    {
        _workspace = workspace;
        _sourceIndex = 0;
        _nodeEvents = workspace?.Configurations.LogOptions.NodeEvents ?? false;

        int formatIndex = workspace is null
            ? 1
            : Array.IndexOf(LogOptions.TimeFormatPresets, workspace.Configurations.LogOptions.TimeFormat);
        _timeFormatIndex = formatIndex >= 0 ? formatIndex : 1;

        ClearCommand = new RelayCommand(_ => Clear(), _ => Rows.Count > 0);
        ExportCommand = new RelayCommand(_ => Export(), _ => _workspace is not null);
        CopyCommand = new RelayCommand(_ => CopyToClipboard(), _ => Rows.Count > 0);
        OpenImageCommand = new RelayCommand(p => OpenImage(p as LogRowViewModel));

        if (workspace is not null)
        {
            workspace.Log.Write(LogLevel.Debug, "System", "日志页已打开", "lifecycle");
        }

        Rebuild();

        if (workspace is not null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200),
            };
            _timer.Tick += OnTick;
            _timer.Start();
        }
    }

    /// <summary>一批新行已追加（界面据此滚动到底部，避免每行都触发一次 ScrollIntoView）。</summary>
    public event EventHandler? BatchAppended;    public ObservableCollection<LogRowViewModel> Rows { get; } = [];

    public ObservableCollection<string> LevelChoices { get; } =
        [.. LevelThresholds.Select(LogLevels.ThresholdLabel)];

    public ObservableCollection<string> SourceChoices { get; } =
        ["全部来源", "Engine", "Agent", "Runner", "Timer", "System"];

    public ObservableCollection<string> TimeFormats { get; } = [.. LogOptions.TimeFormatPresets];

    public ICommand ClearCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand OpenImageCommand { get; }

    /// <summary>级别阈值（下标 → 日志级别）。</summary>
    public int LevelIndex
    {
        get => _levelIndex;
        set
        {
            if (SetProperty(ref _levelIndex, Math.Clamp(value, 0, LevelThresholds.Length - 1)))
            {
                Rebuild();
            }
        }
    }

    public int SourceIndex
    {
        get => _sourceIndex;
        set
        {
            if (SetProperty(ref _sourceIndex, Math.Max(value, 0)))
            {
                Rebuild();
            }
        }
    }

    public int TimeFormatIndex
    {
        get => _timeFormatIndex;
        set
        {
            if (!SetProperty(ref _timeFormatIndex, Math.Clamp(value, 0, TimeFormats.Count - 1)))
            {
                return;
            }

            ApplyOptions(options => options.TimeFormat = TimeFormats[_timeFormatIndex]);
            Rebuild();   // 时间文本是预格式化的，换格式必须重建行
        }
    }

    /// <summary>关键字（匹配消息 / 详情 / 分类 / 来源 / 实例）。</summary>
    public string Keyword
    {
        get => _keyword;
        set
        {
            if (SetProperty(ref _keyword, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    /// <summary>新增内容时自动滚到底部。</summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    /// <summary>暂停刷新（日志仍继续记录，取消暂停后一次性补齐）。</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (SetProperty(ref _paused, value) && !value)
            {
                Refresh();
            }
        }
    }

    /// <summary>记录节点级事件（Node.*，量大，排查时才开）。</summary>
    public bool NodeEvents
    {
        get => _nodeEvents;
        set
        {
            if (SetProperty(ref _nodeEvents, value))
            {
                ApplyOptions(options => options.NodeEvents = value);
            }
        }
    }

    public string CountText
    {
        get
        {
            LogBuffer? buffer = _workspace?.Log;
            if (buffer is null)
            {
                return "日志不可用（未加载工作区）";
            }

            return $"显示 {Rows.Count} 条 · 缓冲 {buffer.Count} 条"
                + (buffer.Dropped > 0 ? $" · 已淘汰 {buffer.Dropped} 条" : string.Empty);
        }
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    private LogLevel MinLevel => LevelThresholds[_levelIndex];

    private string? SourceFilter => _sourceIndex <= 0 ? null : SourceChoices[_sourceIndex];

    private void OnTick(object? sender, EventArgs e) => Refresh();

    /// <summary>拉增量（无变化时立刻返回）。</summary>
    public void Refresh()
    {
        LogBuffer? buffer = _workspace?.Log;
        if (buffer is null || _paused || _disposed)
        {
            return;
        }

        if (buffer.Version == _seenVersion)
        {
            return;
        }

        LogEntry[] fresh = buffer.QuerySince(_seenSeq, MinLevel, SourceFilter, Keyword);
        _seenVersion = buffer.Version;
        _seenSeq = buffer.LastSeq;

        if (fresh.Length == 0)
        {
            RaiseCounts();
            return;
        }

        // 积压过多（切页/暂停后追赶）时整体重建，避免逐条插入触发大量布局
        if (fresh.Length > 300)
        {
            Rebuild();
            return;
        }

        foreach (LogEntry entry in fresh)
        {
            Rows.Add(new LogRowViewModel(entry, FormatOptions()));
        }

        TrimRows();
        RaiseCounts();
        BatchAppended?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按当前过滤条件重建列表（取缓冲里最新 <see cref="MaxRows"/> 条）。</summary>
    public void Rebuild()
    {
        LogBuffer? buffer = _workspace?.Log;
        Rows.Clear();

        if (buffer is null)
        {
            RaiseCounts();
            return;
        }

        LogRowViewModel row;
        foreach (LogEntry entry in buffer.Query(MinLevel, SourceFilter, Keyword, MaxRows))
        {
            row = new LogRowViewModel(entry, FormatOptions());
            Rows.Add(row);
        }

        _seenVersion = buffer.Version;
        _seenSeq = buffer.LastSeq;
        RaiseCounts();
        BatchAppended?.Invoke(this, EventArgs.Empty);
    }

    private void TrimRows()
    {
        while (Rows.Count > MaxRows)
        {
            Rows.RemoveAt(0);
        }
    }

    private LogOptions FormatOptions()
    {
        LogOptions options = _workspace?.Configurations.LogOptions.Clone() ?? new LogOptions();
        options.TimeFormat = TimeFormats[_timeFormatIndex];
        return options;
    }

    /// <summary>改策略并落盘（容量/节点开关/时间格式都要跨重启保留）。</summary>
    private void ApplyOptions(Action<LogOptions> mutate)
    {
        if (_workspace is null)
        {
            return;
        }

        LogOptions updated = _workspace.Configurations.LogOptions.Clone();
        mutate(updated);
        _workspace.Configurations.UpdateLogOptions(updated);
    }

    private void Clear()
    {
        _workspace?.Log.Clear();
        Rows.Clear();
        _seenSeq = 0;
        _seenVersion = -1;
        Message = "已清空当前日志";
        RaiseCounts();

        if (_workspace is not null)
        {
            // 清空动作本身也留一条，避免「点了没反应」的错觉
            _workspace.Log.Write(LogLevel.Info, "System", "日志已由用户清空", "lifecycle");
        }
    }

    /// <summary>按当前过滤条件导出（含截图目录）；CSV 走同一批条目。</summary>
    private void Export()
    {
        LogBuffer? buffer = _workspace?.Log;
        if (buffer is null)
        {
            Message = "日志不可用：未加载工作区";
            return;
        }

        LogEntry[] entries = buffer.Query(MinLevel, SourceFilter, Keyword);
        if (entries.Length == 0)
        {
            Message = "当前过滤条件下没有可导出的日志";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出运行日志",
            Filter = "文本日志 (*.txt)|*.txt|CSV 表格 (*.csv)|*.csv",
            FileName = $"maayys-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        LogOptions options = FormatOptions();
        bool csv = string.Equals(Path.GetExtension(dialog.FileName), ".csv", StringComparison.OrdinalIgnoreCase);

        try
        {
            int shots = ExportShots(entries, dialog.FileName);
            string header = $"MaaYys 运行日志 · {entries.Length} 条 · 导出 {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                + $" · 级别≥{LogLevels.Label(MinLevel)}"
                + (SourceFilter is null ? string.Empty : $" · 来源 {SourceFilter}")
                + (string.IsNullOrWhiteSpace(Keyword) ? string.Empty : $" · 关键字「{Keyword}」");

            string text = csv
                ? LogExporter.ToCsv(entries, options)
                : LogExporter.ToText(entries, options, header);

            // BOM：Excel 直接双击 CSV 不乱码
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(true));

            Message = $"已导出 {entries.Length} 条到 {dialog.FileName}" + (shots > 0 ? $"（截图 {shots} 张）" : string.Empty);
            buffer.Write(LogLevel.Info, "System", Message, "lifecycle");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Message = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>把截图写成 PNG（文件名与文本里的 <c>shot-xxxxxx.png</c> 一致）。</summary>
    private static int ExportShots(LogEntry[] entries, string logPath)
    {
        var withShots = entries.Where(e => e.Thumbnail is not null).ToList();
        if (withShots.Count == 0)
        {
            return 0;
        }

        string directory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(logPath))!,
            Path.GetFileNameWithoutExtension(logPath) + "_shots");
        Directory.CreateDirectory(directory);

        int written = 0;
        foreach (LogEntry entry in withShots)
        {
            try
            {
                BitmapSource bitmap = FrameBitmapConverter.ToBitmap(entry.Thumbnail!);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new FileStream(Path.Combine(directory, LogExporter.ShotFileName(entry)), FileMode.Create);
                encoder.Save(stream);
                written++;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
            {
                // 单张写失败不影响整份导出
            }
        }

        return written;
    }

    private void CopyToClipboard()
    {
        LogBuffer? buffer = _workspace?.Log;
        if (buffer is null)
        {
            return;
        }

        LogEntry[] entries = buffer.Query(MinLevel, SourceFilter, Keyword, MaxRows);
        if (entries.Length == 0)
        {
            Message = "当前过滤条件下没有可复制的日志";
            return;
        }

        try
        {
            Clipboard.SetText(LogExporter.ToText(entries, FormatOptions()));
            Message = $"已复制 {entries.Length} 条到剪贴板";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // 剪贴板被其他进程占用是常态，不当作错误弹出
            Message = "复制失败：剪贴板被占用，请稍后重试";
        }
    }

    private void OpenImage(LogRowViewModel? row)
    {
        if (row?.Thumbnail is null)
        {
            return;
        }

        var window = new Views.LogImageView(row.Thumbnail, row.TimeText)
        {
            Owner = Application.Current?.MainWindow,
        };

        window.ShowDialog();
    }

    private void RaiseCounts() => OnPropertyChanged(nameof(CountText));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
    }
}
