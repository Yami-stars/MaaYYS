using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using MaaYys.App.Services;
using MaaYys.Engine.Cleanup;
using MaaYys.Engine.Logging;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 日志与缓存（P6.2）：内存日志策略、文件落盘、图片缓存清理、资源整理状态。
/// <para>
/// 日志分两层说清楚：<b>内存</b>决定「界面上能翻多少」，<b>文件</b>决定「关掉程序后还能不能回看」。
/// 默认只开内存（P5.5 行为）；开启落盘后按大小滚动，磁盘占用有上界。
/// </para>
/// </summary>
public sealed class StorageSettingsViewModel : SettingsSectionViewModel
{
    private bool _busy;
    private string _cacheText = "正在统计…";
    private string _cacheDetail = string.Empty;
    private string _maintenanceText = "正在读取…";
    private bool _hasError;

    public StorageSettingsViewModel(InstanceWorkspace? workspace)
        : base(workspace)
    {
        ClearCacheCommand = new RelayCommand(_ => _ = ClearCacheAsync(), _ => !IsBusy);
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
        FlushLogCommand = new RelayCommand(_ => FlushLog());

        // ShowMessage / ShowError 是 Message 的派生可见性，基类只通知 Message 本身
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Message))
            {
                OnPropertyChanged(nameof(ShowMessage));
                OnPropertyChanged(nameof(ShowError));
            }
        };

        RefreshCache();
        RefreshMaintenance();
    }

    public ICommand ClearCacheCommand { get; }

    public ICommand OpenLogFolderCommand { get; }

    public ICommand FlushLogCommand { get; }

    public IReadOnlyList<string> TimeFormats { get; } = [.. LogOptions.TimeFormatPresets];

    /// <summary>落盘级别可选项（不含 Trace：全量 Trace 落盘一天能写掉几个 GB）。</summary>
    public IReadOnlyList<string> FileLevels { get; } = ["调试", "信息", "警告", "错误"];

    private static readonly LogLevel[] FileLevelValues =
        [LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error];

    // ---- 内存日志 ----

    public int TimeFormatIndex
    {
        get
        {
            if (Workspace is null)
            {
                return 1;
            }

            int index = Array.IndexOf(LogOptions.TimeFormatPresets, Workspace.Configurations.LogOptions.TimeFormat);
            return index < 0 ? 1 : index;
        }

        set
        {
            if (Workspace is null || value < 0 || value >= TimeFormats.Count || value == TimeFormatIndex)
            {
                return;
            }

            SaveLogOptions(options => options.TimeFormat = TimeFormats[value]);
        }
    }

    /// <summary>内存里保留的条数（超出后丢最旧的）。</summary>
    public int LogCapacity
    {
        get => Workspace?.Configurations.LogOptions.Capacity ?? 20_000;
        set
        {
            if (Workspace is null || value == LogCapacity)
            {
                return;
            }

            SaveLogOptions(options => options.Capacity = value);
        }
    }

    /// <summary>是否记录节点级事件（量大，排查才开）。</summary>
    public bool LogNodeEvents
    {
        get => Workspace?.Configurations.LogOptions.NodeEvents ?? false;
        set
        {
            if (Workspace is null || value == LogNodeEvents)
            {
                return;
            }

            SaveLogOptions(options => options.NodeEvents = value);
        }
    }

    public bool LogThumbnails
    {
        get => Workspace?.Configurations.LogOptions.Thumbnails ?? true;
        set
        {
            if (Workspace is null || value == LogThumbnails)
            {
                return;
            }

            SaveLogOptions(options => options.Thumbnails = value);
        }
    }

    // ---- 文件日志 ----

    public bool FileLogEnabled
    {
        get => Settings.FileLog.Enabled;
        set
        {
            if (value == Settings.FileLog.Enabled)
            {
                return;
            }

            Settings.FileLog.Enabled = value;
            SaveSettings();
            Message = value
                ? $"已开启日志落盘：{Settings.FileLog.ResolveDirectory(AppContext.BaseDirectory)}"
                : "已关闭日志落盘（现有文件保留，不再写入）";
            Log(Message);
            OnPropertyChanged(nameof(FileLogPath));
        }
    }

    public string FileLogDirectory
    {
        get => Settings.FileLog.Directory;
        set
        {
            if (string.Equals(value, Settings.FileLog.Directory, StringComparison.Ordinal))
            {
                return;
            }

            Settings.FileLog.Directory = value ?? string.Empty;
            SaveSettings();
            Workspace?.FileLog.Restart();
            OnPropertyChanged(nameof(FileLogPath));
        }
    }

    public int FileLogMaxSizeMb
    {
        get => Settings.FileLog.MaxFileSizeMb;
        set
        {
            if (value == Settings.FileLog.MaxFileSizeMb)
            {
                return;
            }

            Settings.FileLog.MaxFileSizeMb = value;
            SaveSettings();
        }
    }

    public int FileLogMaxFiles
    {
        get => Settings.FileLog.MaxFiles;
        set
        {
            if (value == Settings.FileLog.MaxFiles)
            {
                return;
            }

            Settings.FileLog.MaxFiles = value;
            SaveSettings();
        }
    }

    /// <summary>落盘最低级别（下拉下标）。</summary>
    public int FileLogLevelIndex
    {
        get
        {
            int index = Array.IndexOf(FileLevelValues, Settings.FileLog.MinLevel);
            return index < 0 ? 1 : index;
        }

        set
        {
            if (value < 0 || value >= FileLevelValues.Length || value == FileLogLevelIndex)
            {
                return;
            }

            Settings.FileLog.MinLevel = FileLevelValues[value];
            SaveSettings();
        }
    }

    /// <summary>当前落盘文件（未开启 / 还没写过时给说明文字）。</summary>
    public string FileLogPath
    {
        get
        {
            if (!Settings.FileLog.Enabled)
            {
                return "未开启";
            }

            string directory = Settings.FileLog.ResolveDirectory(AppContext.BaseDirectory);
            string? current = Workspace?.FileLog.CurrentPath;
            return current is null
                ? $"{directory}（尚未写入）"
                : current;
        }
    }

    /// <summary>落盘异常提示（磁盘满 / 无权限时这里会变红）。</summary>
    public string? FileLogError => Workspace?.FileLog.LastError;

    // ---- 图片缓存 ----

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                (ClearCacheCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string CacheText
    {
        get => _cacheText;
        private set => SetProperty(ref _cacheText, value);
    }

    public string CacheDetail
    {
        get => _cacheDetail;
        private set => SetProperty(ref _cacheDetail, value);
    }

    public string MaintenanceText
    {
        get => _maintenanceText;
        private set => SetProperty(ref _maintenanceText, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (SetProperty(ref _hasError, value))
            {
                OnPropertyChanged(nameof(ShowMessage));
                OnPropertyChanged(nameof(ShowError));
            }
        }
    }

    public bool ShowMessage => Message.Length > 0 && !HasError;

    public bool ShowError => Message.Length > 0 && HasError;

    /// <summary>确认框文案（与 MAA 一致：先讲清「正常情况下不用清」）。</summary>
    public static string ConfirmText =>
        "确认删除所有图片缓存？\n\n"
        + "· 删除范围：debug、cache 目录下的全部图片（截图、调试用图）；\n"
        + "· 日志文件（*.log）会保留，排查问题需要的记录不受影响；\n"
        + "· 图片缓存会随运行重新生成，删除后部分界面需要重新截图，属正常现象。\n\n"
        + "注意：此操作直接删除，不进入回收站，删除后无法恢复。";

    public void RefreshCache()
    {
        try
        {
            ImageCacheSnapshot snapshot = ImageCache.Scan(AppContext.BaseDirectory);
            CacheText = snapshot.Summary;
            CacheDetail = snapshot.Directories.Count == 0
                ? $"{AppContext.BaseDirectory} 下还没有缓存目录"
                : $"{Path.Combine(AppContext.BaseDirectory, string.Join("、", snapshot.Directories))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CacheText = "统计失败";
            CacheDetail = ex.Message;
            HasError = true;
        }
    }

    private async Task ClearCacheAsync()
    {
        if (System.Windows.MessageBox.Show(
                ConfirmText,
                "清理图片缓存",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        await ClearAsync().ConfigureAwait(true);
    }

    /// <summary>真正执行（调试截图等无人值守场景可直接调用，跳过确认框）。</summary>
    public async Task ClearAsync()
    {
        IsBusy = true;
        HasError = false;
        Message = "正在清理…";

        try
        {
            ImageCacheClearOutcome outcome = await Task.Run(() => ImageCache.Clear(AppContext.BaseDirectory))
                .ConfigureAwait(true);

            RefreshCache();
            Message = outcome.Summary;
            HasError = outcome.Failed > 0;

            if (outcome.Errors.Count > 0)
            {
                Message += $"；{string.Join("；", outcome.Errors.Take(3))}";
            }

            Log(Message, HasError ? LogLevel.Warn : LogLevel.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HasError = true;
            Message = $"清理失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- 资源整理（只读） ----

    public void RefreshMaintenance()
    {
        string statePath = StatePath;
        if (!File.Exists(statePath))
        {
            MaintenanceText = "尚未整理。资源版本更新后会自动清理旧版本残留（不可达节点、未引用模板/模型、*.bak）";
            return;
        }

        try
        {
            ResourceState? state = JsonSerializer.Deserialize<ResourceState>(File.ReadAllText(statePath));
            MaintenanceText = state is null
                ? "状态文件无法解析"
                : $"上次整理：{state.PrunedAt:yyyy-MM-dd HH:mm}（资源 {state.Version}）\n{state.Summary}";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            MaintenanceText = $"状态文件读取失败：{ex.Message}";
        }
    }

    private string StatePath => Workspace?.Resources.InterfacePath is { } interfacePath
        ? Path.Combine(Path.GetDirectoryName(interfacePath)!, "config", "resource-state.json")
        : ResourceMaintenance.DefaultStatePath;

    // ---- 动作 ----

    private void OpenLogFolder()
    {
        string directory = Settings.FileLog.ResolveDirectory(AppContext.BaseDirectory);
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true })?.Dispose();
            Message = $"已打开：{directory}";
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message = $"打开目录失败：{ex.Message}";
        }
    }

    private void FlushLog()
    {
        Workspace?.FileLog.Flush();
        Message = Workspace?.FileLog.LastError is { } error
            ? $"刷盘失败：{error}"
            : $"已刷盘：{FileLogPath}";
        OnPropertyChanged(nameof(FileLogPath));
        OnPropertyChanged(nameof(FileLogError));
    }
}
