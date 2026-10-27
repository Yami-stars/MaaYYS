using System.Net.Http;
using System.Text;
using MaaYys.Engine.Config;

namespace MaaYys.Engine.Notification;

/// <summary>外部通知的触发类型（与设置里的 OnComplete / OnFailure / OnStuck 一一对应）。</summary>
public enum NotificationTrigger
{
    /// <summary>队列正常跑完。</summary>
    Completed,

    /// <summary>队列有任务失败。</summary>
    Failed,

    /// <summary>检测到卡住（卡住超时命中）。</summary>
    Stuck,

    /// <summary>用户在设置页手动点的「发送测试消息」（绕过触发门控，只要启用了外部通知就发）。</summary>
    Test,
}

/// <summary>一次通知的上下文（标题 / 正文 / 时间 / 触发类型），用于渲染模板。</summary>
public sealed class NotificationContext
{
    public NotificationContext(string title, string message, DateTimeOffset time, NotificationTrigger trigger)
    {
        Title = title ?? string.Empty;
        Message = message ?? string.Empty;
        Time = time;
        Trigger = trigger;
    }

    public string Title { get; }

    public string Message { get; }

    public DateTimeOffset Time { get; }

    public NotificationTrigger Trigger { get; }
}

/// <summary>发送结果：没发（未启用/被门控）、成功、失败各是什么。</summary>
public sealed class NotificationResult
{
    /// <summary>是否真的发出了请求（启用且通过门控且地址非空）。</summary>
    public bool Sent { get; init; }

    /// <summary>发出去但网络/HTTP 层失败（已降级处理，不会抛到调用方）。</summary>
    public bool Failed { get; init; }

    /// <summary>失败原因（Failed 时才有）。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 外部通知发送器（P6.3 首批）：目前只实现 **Webhook** 一个通道——向配置好的 URL 发一次
/// HTTP 请求（POST/PUT），请求体由模板渲染、可带自定义请求头。
/// <para>
/// 设计原则：
/// 1. 纯逻辑、无 UI 依赖；<see cref="HttpClient"/> 可注入，便于单测用本地监听端做真实验收。
/// 2. **任何失败都降级处理**（返回 <see cref="NotificationResult.Failed"/>），绝不抛异常——
///    通知是「锦上添花」，不能因为它挂了把任务队列拖死。
/// 3. 触发门控（OnComplete/OnFailure/OnStuck）在这里统一判断；<see cref="NotificationTrigger.Test"/>
///    绕过门控，让用户在没有真实事件时也能验证「能不能收到」。
/// </para>
/// </summary>
public sealed class NotificationService
{
    /// <summary>共享 HttpClient（单连接池，避免短生命周期 client 的套接字耗尽）。</summary>
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// 按配置发送一次外部通知。返回的结果里 <see cref="NotificationResult.Sent"/> 为 false 表示
    /// 没发（未启用 / 被门控 / 地址为空），<see cref="NotificationResult.Failed"/> 为 true 表示
    /// 发了但失败（已降级）。
    /// </summary>
    /// <param name="settings">通知设置（取其中的外部通知与 Webhook 段）。</param>
    /// <param name="context">本次通知的标题/正文/时间/触发类型。</param>
    /// <param name="client">可注入的 HttpClient（测试用本地监听端）；为空用共享实例。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<NotificationResult> SendAsync(
        NotificationSettings settings,
        NotificationContext context,
        HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.ExternalReady())
        {
            return new NotificationResult();
        }

        // 触发门控：Test 永远放行；其余按对应开关
        if (context.Trigger != NotificationTrigger.Test)
        {
            bool allowed = context.Trigger switch
            {
                NotificationTrigger.Completed => settings.OnComplete,
                NotificationTrigger.Failed => settings.OnFailure,
                NotificationTrigger.Stuck => settings.OnStuck,
                _ => false,
            };

            if (!allowed)
            {
                return new NotificationResult();
            }
        }

        string url = settings.WebhookUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return new NotificationResult();
        }

        HttpClient http = client ?? SharedClient;
        string body = Substitute(settings.EffectiveBodyTemplate(), context);

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(settings.WebhookMethod), url);

            if (string.Equals(settings.WebhookMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                // GET 不带请求体，参数走 query 也可，但模板仍允许渲染成 body 被忽略
            }
            else
            {
                request.Content = new StringContent(body, Encoding.UTF8, settings.WebhookContentType);
            }

            ApplyCustomHeaders(request, settings.WebhookHeaders);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new NotificationResult { Sent = true, Failed = true, Error = $"HTTP {(int)response.StatusCode} {response.StatusCode}" };
            }

            return new NotificationResult { Sent = true };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return new NotificationResult { Sent = true, Failed = true, Error = ex.Message };
        }
    }

    /// <summary>把模板里的 {title}/{message}/{time}/{trigger} 替换成实际值（大小写不敏感）。公开以便设置页预览与单测复用。</summary>
    public static string Substitute(string template, NotificationContext context)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template ?? string.Empty;
        }

        string trigger = context.Trigger switch
        {
            NotificationTrigger.Completed => "completed",
            NotificationTrigger.Failed => "failed",
            NotificationTrigger.Stuck => "stuck",
            _ => "test",
        };

        return template
            .Replace("{title}", context.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{message}", context.Message, StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", context.Time.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{trigger}", trigger, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>解析 `Key: Value` 多行头，用 TryAddWithoutValidation 避免非法头名直接抛。</summary>
    private static void ApplyCustomHeaders(HttpRequestMessage request, string headers)
    {
        if (string.IsNullOrWhiteSpace(headers))
        {
            return;
        }

        foreach (string raw in headers.Split('\n'))
        {
            string line = raw.Trim('\r').Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
