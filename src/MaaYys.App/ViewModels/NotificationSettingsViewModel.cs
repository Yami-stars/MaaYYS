using System.Windows;
using System.Windows.Input;
using MaaYys.App.Services;
using MaaYys.Engine.Config;
using MaaYys.Engine.Notification;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 通知设置（P6.3 首批）：应用内通知（卡住超时 / 提醒间隔）+ 外部通知（Webhook）。
/// <para>
/// 与「运行设置」的停滞超时区别：那里是**停机**，这里是**只提醒不打断**。
/// 本批只做 Webhook 一个外部通道；Telegram / 邮件 / 企业微信 / 飞书 / 钉钉 等多通道留待后续批次。
/// </para>
/// </summary>
public sealed class NotificationSettingsViewModel : SettingsSectionViewModel
{
    public NotificationSettingsViewModel(InstanceWorkspace? workspace)
        : base(workspace)
    {
        ResetCommand = new RelayCommand(_ => ResetToDefaults(), _ => Persisted);
        SendTestCommand = new RelayCommand(_ => _ = SendTestAsync(), _ => Persisted);
    }

    public ICommand ResetCommand { get; }

    public ICommand SendTestCommand { get; }

    public IReadOnlyList<string> WebhookMethods { get; } = NotificationSettings.WebhookMethods;

    // ---- 应用内通知 ----

    /// <summary>卡住超时（秒，0 = 关闭）。</summary>
    public int StuckTimeoutSeconds
    {
        get => Settings.Notification.StuckTimeoutSeconds;
        set
        {
            if (value == Settings.Notification.StuckTimeoutSeconds)
            {
                return;
            }

            Settings.Notification.StuckTimeoutSeconds = value;
            SaveSettings();
        }
    }

    /// <summary>提醒间隔（分钟，0 = 关闭）。</summary>
    public int ReminderIntervalMinutes
    {
        get => Settings.Notification.ReminderIntervalMinutes;
        set
        {
            if (value == Settings.Notification.ReminderIntervalMinutes)
            {
                return;
            }

            Settings.Notification.ReminderIntervalMinutes = value;
            SaveSettings();
        }
    }

    // ---- 外部通知总开关与触发 ----

    public bool ExternalEnabled
    {
        get => Settings.Notification.ExternalEnabled;
        set
        {
            if (value == Settings.Notification.ExternalEnabled)
            {
                return;
            }

            Settings.Notification.ExternalEnabled = value;
            SaveSettings();
            OnPropertyChanged(nameof(ExternalStatus));
        }
    }

    public bool OnComplete
    {
        get => Settings.Notification.OnComplete;
        set
        {
            if (value == Settings.Notification.OnComplete)
            {
                return;
            }

            Settings.Notification.OnComplete = value;
            SaveSettings();
            OnPropertyChanged(nameof(ExternalStatus));
        }
    }

    public bool OnFailure
    {
        get => Settings.Notification.OnFailure;
        set
        {
            if (value == Settings.Notification.OnFailure)
            {
                return;
            }

            Settings.Notification.OnFailure = value;
            SaveSettings();
            OnPropertyChanged(nameof(ExternalStatus));
        }
    }

    public bool OnStuck
    {
        get => Settings.Notification.OnStuck;
        set
        {
            if (value == Settings.Notification.OnStuck)
            {
                return;
            }

            Settings.Notification.OnStuck = value;
            SaveSettings();
            OnPropertyChanged(nameof(ExternalStatus));
        }
    }

    // ---- Webhook 通道 ----

    public string WebhookUrl
    {
        get => Settings.Notification.WebhookUrl;
        set
        {
            if (string.Equals(value, Settings.Notification.WebhookUrl, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Notification.WebhookUrl = value ?? string.Empty;
            SaveSettings();
            OnPropertyChanged(nameof(ExternalStatus));
        }
    }

    public int WebhookMethodIndex
    {
        get
        {
            int index = Array.IndexOf(NotificationSettings.WebhookMethods, Settings.Notification.WebhookMethod);
            return index < 0 ? 0 : index;
        }

        set
        {
            int index = Math.Clamp(value, 0, WebhookMethods.Count - 1);
            string method = WebhookMethods[index];
            if (string.Equals(method, Settings.Notification.WebhookMethod, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Notification.WebhookMethod = method;
            SaveSettings();
        }
    }

    public string WebhookContentType
    {
        get => Settings.Notification.WebhookContentType;
        set
        {
            if (string.Equals(value, Settings.Notification.WebhookContentType, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Notification.WebhookContentType = value ?? string.Empty;
            SaveSettings();
        }
    }

    public string WebhookHeaders
    {
        get => Settings.Notification.WebhookHeaders;
        set
        {
            if (string.Equals(value, Settings.Notification.WebhookHeaders, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Notification.WebhookHeaders = value ?? string.Empty;
            SaveSettings();
        }
    }

    public string WebhookBodyTemplate
    {
        get => Settings.Notification.WebhookBodyTemplate;
        set
        {
            if (string.Equals(value, Settings.Notification.WebhookBodyTemplate, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Notification.WebhookBodyTemplate = value ?? string.Empty;
            SaveSettings();
        }
    }

    /// <summary>外部通知就绪状态（总开关 + 地址 + 至少勾一个触发），页面顶部一句话。</summary>
    public string ExternalStatus
    {
        get
        {
            NotificationSettings n = Settings.Notification;
            if (!n.ExternalEnabled)
            {
                return "外部通知已关闭（开启后按下方触发条件发送 Webhook）";
            }

            if (string.IsNullOrWhiteSpace(n.WebhookUrl))
            {
                return "外部通知已启用，但还没填 Webhook 地址";
            }

            if (!n.OnComplete && !n.OnFailure && !n.OnStuck)
            {
                return "外部通知已启用，但没有任何触发条件被勾选";
            }

            return "外部通知就绪：队列完成 / 失败 / 卡住时会向 Webhook 发请求";
        }
    }

    /// <summary>请求体预览（合并默认模板）：让用户在保存前看一眼到底会发什么。</summary>
    public string BodyPreview
    {
        get
        {
            var context = new NotificationContext(
                "标题示例",
                "正文示例：配置组「日常」已跑完",
                DateTimeOffset.Now,
                NotificationTrigger.Test);
            return Engine.Notification.NotificationService.Substitute(
                Settings.Notification.EffectiveBodyTemplate(), context);
        }
    }

    private async Task SendTestAsync()
    {
        if (Workspace?.Notifier is null)
        {
            Message = "当前环境不支持发送（无工作区）";
            return;
        }

        NotificationSettings n = Settings.Notification;
        if (!n.ExternalReady())
        {
            Message = "未发送：外部通知未就绪 —— " + ExternalStatus;
            return;
        }

        Message = "正在发送测试消息…";
        await Workspace.Notifier.NotifyAsync(
            NotificationTrigger.Test,
            "MaaYys 测试消息",
            "如果你收到了这条消息，说明外部通知配置正确。",
            default).ConfigureAwait(false);

        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            Message = "测试消息已发出（成功与否见下方运行日志；接收端应已收到一条 POST 请求）"));
    }

    private void ResetToDefaults()
    {
        var defaults = new NotificationSettings();
        NotificationSettings n = Settings.Notification;
        n.StuckTimeoutSeconds = defaults.StuckTimeoutSeconds;
        n.ReminderIntervalMinutes = defaults.ReminderIntervalMinutes;
        n.ExternalEnabled = defaults.ExternalEnabled;
        n.OnComplete = defaults.OnComplete;
        n.OnFailure = defaults.OnFailure;
        n.OnStuck = defaults.OnStuck;
        n.WebhookUrl = defaults.WebhookUrl;
        n.WebhookMethod = defaults.WebhookMethod;
        n.WebhookContentType = defaults.WebhookContentType;
        n.WebhookHeaders = defaults.WebhookHeaders;
        n.WebhookBodyTemplate = defaults.WebhookBodyTemplate;
        SaveSettings();
        OnPropertyChanged(nameof(ExternalStatus));
        OnPropertyChanged(nameof(WebhookMethodIndex));
        Message = "已恢复默认通知设置";
        Log("通知设置已恢复默认");
    }
}
