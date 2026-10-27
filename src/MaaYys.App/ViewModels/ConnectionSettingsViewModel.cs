using System.Windows.Input;
using MaaYys.App.Services;
using MaaYys.Engine.Config;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 连接设置（P6.2）：Win32 控制器的截图 / 输入方式与预览帧率。
/// <para>
/// 定位是**全局默认值**：新建实例按它初始化；已有实例仍以「多实例」页的单独配置为准
/// （多开不同游戏时各不相同）。要一键统一，用「应用到全部实例」。
/// </para>
/// <para>
/// 静音没有做：MaaFramework 的 Win32 控制器没有音量相关接口，
/// 硬塞一个「静音」开关只会是假的开关 —— 需要静音的场合请用系统音量控制。
/// </para>
/// </summary>
public sealed class ConnectionSettingsViewModel : SettingsSectionViewModel
{
    public ConnectionSettingsViewModel(InstanceWorkspace? workspace)
        : base(workspace)
    {
        ApplyToAllCommand = new RelayCommand(_ => ApplyToAllInstances(), _ => Persisted);
    }

    /// <summary>可选截图方式（与 interface.json controller 一致）。</summary>
    public IReadOnlyList<string> ScreencapMethods => InstanceDefinition.Win32ScreencapMethods;

    /// <summary>可选输入方式。</summary>
    public IReadOnlyList<string> InputMethods => InstanceDefinition.Win32InputMethods;

    public string ScreencapMethod
    {
        get => Settings.Connection.ScreencapMethod;
        set
        {
            if (string.Equals(value, Settings.Connection.ScreencapMethod, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Connection.ScreencapMethod = value;
            SaveSettings();
            Log($"截图方式改为 {value}（下次连接生效）");
        }
    }

    public string InputMethod
    {
        get => Settings.Connection.InputMethod;
        set
        {
            if (string.Equals(value, Settings.Connection.InputMethod, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Connection.InputMethod = value;
            SaveSettings();
            Log($"鼠标方式改为 {value}（下次连接生效）");
        }
    }

    public string KeyboardMethod
    {
        get => Settings.Connection.KeyboardMethod;
        set
        {
            if (string.Equals(value, Settings.Connection.KeyboardMethod, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Connection.KeyboardMethod = value;
            SaveSettings();
            Log($"键盘方式改为 {value}（下次连接生效）");
        }
    }

    public string Win32TitleRegex
    {
        get => Settings.Connection.Win32TitleRegex;
        set
        {
            if (string.Equals(value, Settings.Connection.Win32TitleRegex, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Connection.Win32TitleRegex = value ?? string.Empty;
            SaveSettings();
        }
    }

    /// <summary>预览帧率（0 = 关闭预览，省 CPU）。</summary>
    public int PreviewFps
    {
        get => Settings.Connection.PreviewFps;
        set
        {
            if (value == Settings.Connection.PreviewFps)
            {
                return;
            }

            Settings.Connection.PreviewFps = value;
            SaveSettings();
        }
    }

    /// <summary>预览帧率说明（0 与普通值文案不同，避免用户以为坏了）。</summary>
    public string PreviewFpsHint => PreviewFps == 0
        ? "已关闭实时预览：主界面「多实例」页不会刷新画面，最省 CPU。"
        : $"每秒刷新 {PreviewFps} 帧；调高更流畅但更吃 CPU。";

    public ICommand ApplyToAllCommand { get; }

    /// <summary>把三种方式一次性写进所有实例并落盘（多开时不用一个个改）。</summary>
    private void ApplyToAllInstances()
    {
        if (Workspace is null)
        {
            return;
        }

        int touched = 0;
        foreach (InstanceViewModel instance in Workspace.Instances)
        {
            if (instance.Definition.DeviceKind != InstanceDeviceKind.Win32)
            {
                continue;
            }

            instance.Definition.ScreencapMethod = Settings.Connection.ScreencapMethod;
            instance.Definition.InputMethod = Settings.Connection.InputMethod;
            instance.Definition.PreviewFps = Settings.Connection.PreviewFps;
            instance.NotifyDefinitionChanged();
            touched++;
        }

        if (touched > 0)
        {
            Workspace.Store.Save();
        }

        Message = touched == 0
            ? "没有桌面端实例需要应用"
            : $"已应用到 {touched} 个桌面端实例（下次连接生效）";
        Log(Message);
    }
}
