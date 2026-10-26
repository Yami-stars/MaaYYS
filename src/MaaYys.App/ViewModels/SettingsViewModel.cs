using System.IO;
using System.Text.Json;
using System.Windows.Input;
using MaaYys.App.Services;
using MaaYys.Engine.Cleanup;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 设置页。
/// <para>
/// 只有两件<b>用户该看见</b>的事：
/// <list type="bullet">
/// <item><b>清理图片缓存</b>——一个按钮，清的是运行时截图（<c>debug/</c>、<c>cache/</c> 里的图片），
/// 参照 MAA「清空图片缓存」的做法：点一下、确认、直接删。</item>
/// <item><b>资源整理状态</b>——只读。过期资源（旧版本留下的不可达节点、未引用模板/模型、<c>*.bak</c>）
/// 在资源版本更新时自动清理，不需要用户决定。</item>
/// </list>
/// 「哪些节点不可达」这类判断全在引擎（<see cref="ResourceAudit"/> / <see cref="ResourcePruner"/>），
/// 界面不参与判定，也不提供勾选——用户不该为「旧版本的垃圾」做选择题。
/// </para>
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly InstanceWorkspace? _workspace;
    private bool _busy;
    private string _cacheText = "正在统计…";
    private string _cacheDetail = string.Empty;
    private string _maintenanceText = "正在读取…";
    private string _message = string.Empty;
    private bool _hasError;

    public SettingsViewModel()
        : this(null)
    {
    }

    public SettingsViewModel(InstanceWorkspace? workspace)
    {
        _workspace = workspace;
        ClearCacheCommand = new RelayCommand(_ => _ = ClearCacheAsync(), _ => !IsBusy);

        RefreshCache();
        RefreshMaintenance();
    }

    public ICommand ClearCacheCommand { get; }

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

    /// <summary>图片缓存占用（一行大字：<c>8 张图片 · 11.2 MB（debug）</c>）。</summary>
    public string CacheText
    {
        get => _cacheText;
        private set => SetProperty(ref _cacheText, value);
    }

    /// <summary>补充说明（扫描目录的绝对路径）。</summary>
    public string CacheDetail
    {
        get => _cacheDetail;
        private set => SetProperty(ref _cacheDetail, value);
    }

    /// <summary>资源整理状态（只读）。</summary>
    public string MaintenanceText
    {
        get => _maintenanceText;
        private set => SetProperty(ref _maintenanceText, value);
    }

    /// <summary>操作结果提示。</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(ShowMessage));
                OnPropertyChanged(nameof(ShowError));
            }
        }
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

    /// <summary>成功提示的可见性（WPF 自带转换器不支持取反，直接给两个语义属性）。</summary>
    public bool ShowMessage => Message.Length > 0 && !HasError;

    public bool ShowError => Message.Length > 0 && HasError;

    /// <summary>确认框文案（与 MAA 一致：先讲清「正常情况下不用清」）。</summary>
    public static string ConfirmText =>
        "确认删除所有图片缓存？\n\n"
        + "· 删除范围：debug、cache 目录下的全部图片（截图、调试用图）；\n"
        + "· 日志文件（*.log）会保留，排查问题需要的记录不受影响；\n"
        + "· 图片缓存会随运行重新生成，删除后部分界面需要重新截图，属正常现象。\n\n"
        + "注意：此操作直接删除，不进入回收站，删除后无法恢复。";

    // ---- 缓存 ----

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

    /// <summary>按钮入口：先弹确认框，用户点「是」才动手。</summary>
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

    // ---- 资源整理状态（只读） ----

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

    private string StatePath => _workspace?.Resources.InterfacePath is { } interfacePath
        ? Path.Combine(Path.GetDirectoryName(interfacePath)!, "config", "resource-state.json")
        : ResourceMaintenance.DefaultStatePath;
}
