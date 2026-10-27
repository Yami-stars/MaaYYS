using System.Text.Json;

namespace MaaYys.Engine.Logging;

/// <summary>
/// MaaFramework 原生事件 → 日志条目（P5.5）。
/// <para>
/// 消息名形如 <c>Tasker.Task.Succeeded</c> / <c>Node.Recognition.Starting</c> / <c>Resource.Loading.*</c>，
/// details_json 里带 task_id / entry / node_name 等字段。这里把它们翻成中文正文，
/// 并把原始 JSON 原样留在 <see cref="LogEntry.Details"/>（排查时要看真值）。
/// </para>
/// </summary>
public static class FrameworkLogTranslator
{
    /// <summary>节点级事件（量最大，默认不落日志库）。</summary>
    public static bool IsNodeEvent(string? message)
        => message is not null && message.StartsWith("Node.", StringComparison.Ordinal);

    /// <summary>
    /// 级别映射：任务/资源失败为错误；节点级一律调试（识别失败在流程里是常态，不是错误）；
    /// 其余成功/启动类为信息。
    /// </summary>
    public static LogLevel LevelOf(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return LogLevel.Info;
        }

        if (IsNodeEvent(message))
        {
            return LogLevel.Debug;
        }

        return message switch
        {
            "Tasker.Task.Failed" or "Resource.Loading.Failed" => LogLevel.Error,
            "Tasker.Task.Starting" or "Tasker.Task.Started"
                or "Tasker.Task.Succeeded"
                or "Resource.Loading.Starting" or "Resource.Loading.Succeeded" => LogLevel.Info,
            _ => message.Contains(".Failed", StringComparison.Ordinal) ? LogLevel.Warn : LogLevel.Debug,
        };
    }

    /// <summary>
    /// 翻译一条原生事件；返回 null 表示按当前策略不记录（节点级事件且未开启）。
    /// </summary>
    public static LogEntry? Translate(string? message, string? detailsJson, LogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        if (IsNodeEvent(message) && !options.NodeEvents)
        {
            return null;
        }

        LogLevel level = LevelOf(message);
        return new LogEntry
        {
            Level = level,
            Source = "Engine",
            Category = message,
            Message = Describe(message, detailsJson),
            Details = string.IsNullOrWhiteSpace(detailsJson) ? null : detailsJson,
        };
    }

    /// <summary>把消息名 + details 拼成一句中文。</summary>
    public static string Describe(string message, string? detailsJson)
    {
        JsonElement? root = TryParse(detailsJson);
        long taskId = ReadLong(root, "task_id", "taskId", "id");
        string task = taskId > 0 ? $"任务 #{taskId}" : "任务";
        string? entry = ReadString(root, "entry", "name", "task_entry");
        string? node = ReadString(root, "node_name", "node", "name");
        string? path = ReadString(root, "path", "bundle", "resource_dir");
        string? hash = ReadString(root, "hash");

        switch (message)
        {
            case "Tasker.Task.Starting":
            case "Tasker.Task.Started":
                return entry is null ? $"{task} 开始" : $"{task} 开始：{entry}";

            case "Tasker.Task.Succeeded":
                return entry is null ? $"{task} 完成" : $"{task} 完成：{entry}";

            case "Tasker.Task.Failed":
                return entry is null ? $"{task} 失败" : $"{task} 失败：{entry}";

            case "Resource.Loading.Starting":
                return path is null ? "开始加载资源" : $"开始加载资源：{path}";

            case "Resource.Loading.Succeeded":
                return "资源加载完成";

            case "Resource.Loading.Failed":
                return path is null ? "资源加载失败" : $"资源加载失败：{path}";

            case "Node.Recognition.Starting":
                return node is null ? "识别开始" : $"识别开始：{node}";

            case "Node.Recognition.Succeeded":
                return node is null ? "识别成功" : $"识别成功：{node}";

            case "Node.Recognition.Failed":
                return node is null ? "识别未命中" : $"识别未命中：{node}";

            case "Node.Action.Starting":
                return node is null ? "动作开始" : $"动作开始：{node}";

            case "Node.Action.Succeeded":
                return node is null ? "动作完成" : $"动作完成：{node}";

            case "Node.Action.Failed":
                return node is null ? "动作失败" : $"动作失败：{node}";

            case "Node.RecognitionNode.Starting":
            case "Node.RecognitionNode.Succeeded":
            case "Node.RecognitionNode.Failed":
                return node is null
                    ? $"识别节点 {message[(message.LastIndexOf('.') + 1)..]}"
                    : $"识别节点 {node} · {message[(message.LastIndexOf('.') + 1)..]}";

            default:
                // 未知消息保留原文（框架升级新增事件时不至于丢信息），有 hash 时附上
                return hash is null ? message : $"{message} · {hash}";
        }
    }

    private static JsonElement? TryParse(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Clone：JsonDocument 释放后元素失效
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? root, params string[] keys)
    {
        if (root is not { } element)
        {
            return null;
        }

        foreach (string key in keys)
        {
            if (!element.TryGetProperty(key, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                string? text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static long ReadLong(JsonElement? root, params string[] keys)
    {
        if (root is not { } element)
        {
            return 0;
        }

        foreach (string key in keys)
        {
            if (!element.TryGetProperty(key, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out long parsed))
            {
                return parsed;
            }
        }

        return 0;
    }
}
