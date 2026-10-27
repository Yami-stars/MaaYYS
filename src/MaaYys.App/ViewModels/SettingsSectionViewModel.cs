using MaaYys.App.Services;
using MaaYys.Engine.Config;
using MaaYys.Engine.Logging;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 设置子页基类（P6.2）：所有子页都直接读写 <see cref="AppSettings"/> 里的**同一实例**，
/// 改完立即落盘并广播，因此不存在「点了保存才生效」这回事。
/// <para>
/// 为什么用 <see cref="ViewModelBase.OnPropertyChanged(string)"/> 传空串：
/// 一次设置改动往往牵动多个派生属性（例如改了重试次数，「摘要」也要跟着变），
/// 逐条 notify 既啰嗦又容易漏，空串表示「所有绑定重新取值」，代价只是一次界面重读。
/// </para>
/// </summary>
public abstract class SettingsSectionViewModel : ViewModelBase
{
    /// <summary>没有工作区时（设计器 / 单测）的兜底实例，避免到处判空。</summary>
    private static readonly AppSettings Fallback = new();

    protected SettingsSectionViewModel(InstanceWorkspace? workspace)
    {
        Workspace = workspace;
    }

    protected InstanceWorkspace? Workspace { get; }

    /// <summary>当前设置实例（有工作区时就是配置里那一份）。</summary>
    protected AppSettings Settings => Workspace?.Configurations.Settings ?? Fallback;

    /// <summary>是否需要真正落盘（无工作区时只改内存，用于设计器预览）。</summary>
    protected bool Persisted => Workspace is not null;

    /// <summary>夹取 → 落盘 → 广播 → 刷新界面。</summary>
    protected void SaveSettings()
    {
        Settings.Sanitized();

        if (Workspace is not null)
        {
            Workspace.Configurations.UpdateSettings(Settings);
            Workspace.NotifySettingsChanged();
        }

        OnPropertyChanged(string.Empty);
    }

    /// <summary>日志策略的保存入口（与日志页共用同一份 <see cref="LogOptions"/>）。</summary>
    protected void SaveLogOptions(Action<LogOptions> mutate)
    {
        if (Workspace is null)
        {
            return;
        }

        LogOptions updated = Workspace.Configurations.LogOptions.Clone();
        mutate(updated);
        Workspace.Configurations.UpdateLogOptions(updated);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>给用户看的一句话结果（各子页共用一个小提示区）。</summary>
    public string Message
    {
        get => _message;
        protected set => SetProperty(ref _message, value);
    }

    private string _message = string.Empty;

    /// <summary>写一条日志（设置改动留痕，排查「谁把重试关了」时有用）。</summary>
    protected void Log(string message, LogLevel level = LogLevel.Info)
        => Workspace?.Log.Write(level, "Settings", message, "settings");
}
