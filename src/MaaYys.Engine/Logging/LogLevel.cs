namespace MaaYys.Engine.Logging;

/// <summary>
/// 日志级别（P5.5）：数值越大越严重，过滤时用 <c>&gt;=</c> 比较即可。
/// </summary>
public enum LogLevel
{
    /// <summary>逐帧 / 逐节点级细节，默认不记录。</summary>
    Trace = 0,

    /// <summary>调试信息（节点识别成功、动作开始等）。</summary>
    Debug = 1,

    /// <summary>常规运行信息（连接、资源加载、任务开始/结束）。</summary>
    Info = 2,

    /// <summary>可继续执行的异常情况（跳过、超时重试、切渠道回退）。</summary>
    Warn = 3,

    /// <summary>失败（任务失败、资源加载失败、agent 握手失败）。</summary>
    Error = 4,
}

/// <summary>级别与文本的互转（供界面徽标、导出、agent 日志解析共用）。</summary>
public static class LogLevels
{
    /// <summary>短标签（固定 5 字符宽，便于等宽对齐）。</summary>
    public static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO",
    };

    /// <summary>中文名（界面下拉与徽标提示用）。</summary>
    public static string Label(LogLevel level) => level switch
    {
        LogLevel.Trace => "追踪",
        LogLevel.Debug => "调试",
        LogLevel.Info => "信息",
        LogLevel.Warn => "警告",
        LogLevel.Error => "错误",
        _ => "信息",
    };

    /// <summary>
    /// 宽松解析：兼容 <c>DEBUG</c> / <c>debug</c> / <c>WARNING</c> / <c>ERR</c> / <c>[ERROR]</c> 等写法。
    /// 无法识别时返回 false（调用方自行决定默认级别）。
    /// </summary>
    public static bool TryParse(string? text, out LogLevel level)
    {
        level = LogLevel.Info;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string token = text.Trim().Trim('[', ']', '(', ')', ':', ' ', '\t').ToUpperInvariant();
        level = token switch
        {
            "TRACE" or "TRC" or "VERBOSE" => LogLevel.Trace,
            "DEBUG" or "DBG" => LogLevel.Debug,
            "INFO" or "INF" or "INFORMATION" or "NOTICE" => LogLevel.Info,
            "WARN" or "WARNING" or "WRN" => LogLevel.Warn,
            "ERROR" or "ERR" or "FATAL" or "PANIC" or "CRITICAL" => LogLevel.Error,
            _ => LogLevel.Info,
        };

        return token is "TRACE" or "TRC" or "VERBOSE"
            or "DEBUG" or "DBG"
            or "INFO" or "INF" or "INFORMATION" or "NOTICE"
            or "WARN" or "WARNING" or "WRN"
            or "ERROR" or "ERR" or "FATAL" or "PANIC" or "CRITICAL";
    }

    /// <summary>过滤阈值名（界面下拉文案）。</summary>
    public static string ThresholdLabel(LogLevel min) =>
        min == LogLevel.Trace ? "全部级别" : $"{Label(min)}及以上";
}
