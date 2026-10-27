using System.Text.Json.Serialization;

namespace MaaYys.Engine.Config;

/// <summary>
/// 通知设置（P6.3 首批）：应用内通知（卡住超时 / 提醒间隔）+ 外部通知（Webhook 通道）。
/// <para>
/// 与「运行设置」里的停滞超时不同：运行设置的停滞超时是**让执行器判卡死并停止任务**，
/// 这里是**只发通知、不打断任务**——两者可以独立开关。
/// </para>
/// <para>
/// 外部通知本批只落地 **Webhook** 一个通道（HTTP POST/PUT），因为本机就能用本地监听端
/// 真实收到请求、命中「外部通知可真实收到测试消息」的验收标准。Telegram / 邮件 /
/// 企业微信 / 飞书 / 钉钉 等多通道属于后续批次，不做假开关。
/// </para>
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>Webhook 默认请求体模板：JSON，含四个可替换变量。</summary>
    public const string DefaultBodyTemplate = """
        {
          "title": "{title}",
          "message": "{message}",
          "time": "{time}",
          "trigger": "{trigger}"
        }
        """;

    /// <summary>允许的 HTTP 方法（写死两种，避免 GET 带 Body 这种怪用法）。</summary>
    public static readonly string[] WebhookMethods = ["POST", "PUT"];

    // ---- 应用内通知 ----

    /// <summary>卡住超时（秒，0 = 关闭）：运行期间超过该时长没有任何任务状态变化即提示「可能卡住」。</summary>
    [JsonPropertyName("stuck_timeout_seconds")]
    public int StuckTimeoutSeconds { get; set; }

    /// <summary>提醒间隔（分钟，0 = 关闭）：运行期间每隔该时长提示一次「仍在运行」。</summary>
    [JsonPropertyName("reminder_interval_minutes")]
    public int ReminderIntervalMinutes { get; set; }

    // ---- 外部通知总开关与触发条件 ----

    /// <summary>是否启用外部通知（关掉后即使下面三个触发勾上都不再发送）。</summary>
    [JsonPropertyName("external_enabled")]
    public bool ExternalEnabled { get; set; }

    /// <summary>队列正常跑完时发送。</summary>
    [JsonPropertyName("on_complete")]
    public bool OnComplete { get; set; } = true;

    /// <summary>队列有任务失败时发送。</summary>
    [JsonPropertyName("on_failure")]
    public bool OnFailure { get; set; } = true;

    /// <summary>检测到卡住（卡住超时命中）时发送。</summary>
    [JsonPropertyName("on_stuck")]
    public bool OnStuck { get; set; } = true;

    // ---- Webhook 通道配置 ----

    /// <summary>Webhook 地址（空 = 不发）。</summary>
    [JsonPropertyName("webhook_url")]
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>HTTP 方法（POST / PUT）。</summary>
    [JsonPropertyName("webhook_method")]
    public string WebhookMethod { get; set; } = "POST";

    /// <summary>Content-Type（空时默认 application/json）。</summary>
    [JsonPropertyName("webhook_content_type")]
    public string WebhookContentType { get; set; } = "application/json";

    /// <summary>自定义请求头（多行，每行 `Key: Value`；空行忽略）。</summary>
    [JsonPropertyName("webhook_headers")]
    public string WebhookHeaders { get; set; } = string.Empty;

    /// <summary>请求体模板（空 = 用 <see cref="DefaultBodyTemplate"/>）。支持 {title}/{message}/{time}/{trigger}。</summary>
    [JsonPropertyName("webhook_body_template")]
    public string WebhookBodyTemplate { get; set; } = string.Empty;

    public void Sanitize()
    {
        StuckTimeoutSeconds = Math.Clamp(StuckTimeoutSeconds, 0, 3600);
        ReminderIntervalMinutes = Math.Clamp(ReminderIntervalMinutes, 0, 1440);
        WebhookMethod = NormalizeMethod(WebhookMethod);
        WebhookUrl = (WebhookUrl ?? string.Empty).Trim();
        WebhookContentType = string.IsNullOrWhiteSpace(WebhookContentType) ? "application/json" : WebhookContentType.Trim();
        WebhookHeaders = WebhookHeaders ?? string.Empty;
        WebhookBodyTemplate = WebhookBodyTemplate ?? string.Empty;
    }

    public void CopyFrom(NotificationSettings? other)
    {
        if (other is null)
        {
            return;
        }

        StuckTimeoutSeconds = other.StuckTimeoutSeconds;
        ReminderIntervalMinutes = other.ReminderIntervalMinutes;
        ExternalEnabled = other.ExternalEnabled;
        OnComplete = other.OnComplete;
        OnFailure = other.OnFailure;
        OnStuck = other.OnStuck;
        WebhookUrl = other.WebhookUrl;
        WebhookMethod = other.WebhookMethod;
        WebhookContentType = other.WebhookContentType;
        WebhookHeaders = other.WebhookHeaders;
        WebhookBodyTemplate = other.WebhookBodyTemplate;
    }

    /// <summary>把任意输入规整为合法方法（未知值回落 POST，不抛异常）。</summary>
    public static string NormalizeMethod(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            foreach (string method in WebhookMethods)
            {
                if (string.Equals(method, value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return method;
                }
            }
        }

        return "POST";
    }

    /// <summary>真正要发出的请求体（合并默认模板）。</summary>
    public string EffectiveBodyTemplate() => string.IsNullOrWhiteSpace(WebhookBodyTemplate) ? DefaultBodyTemplate : WebhookBodyTemplate;

    /// <summary>是否真的会发外部请求：总开关 + 地址非空 + 至少有一个触发勾上。</summary>
    public bool ExternalReady() => ExternalEnabled
                                    && !string.IsNullOrWhiteSpace(WebhookUrl)
                                    && (OnComplete || OnFailure || OnStuck);
}
