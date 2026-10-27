using System.Text;

namespace MaaYys.Engine.Logging;

/// <summary>
/// 日志导出（P5.5）：纯文本 / CSV 两种纯函数格式化，便于单测与「导出前先预览」。
/// <para>
/// 截图不在引擎侧编码，而是在文本里留一个确定的文件名（<c>shot-000123.png</c>），
/// GUI 导出时按同一规则把缩略图写进 <c>日志名_shots/</c> 目录 —— 两边靠命名约定对齐，
/// 谁先谁后都无所谓。
/// </para>
/// </summary>
public static class LogExporter
{
    /// <summary>截图卡片文件名（由序号推导，稳定可复现）。</summary>
    public static string ShotFileName(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"shot-{entry.Seq:D6}.png";
    }

    /// <summary>单行格式化：<c>13:24:26.123 [ERROR] Runner  实例A  队列失败 │ {json} [截图 shot-000123.png]</c></summary>
    public static string FormatLine(LogEntry entry, LogOptions options)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);

        var builder = new StringBuilder(128);
        builder.Append(options.FormatTime(entry.Time));
        builder.Append(" [").Append(LogLevels.Abbreviation(entry.Level)).Append(']');
        builder.Append(' ').Append(entry.Source);

        if (!string.IsNullOrEmpty(entry.Scope))
        {
            builder.Append(' ').Append(entry.Scope);
        }

        builder.Append("  ").Append(entry.Message);

        if (!string.IsNullOrWhiteSpace(entry.Details))
        {
            builder.Append(" │ ").Append(entry.Details);
        }

        if (entry.Thumbnail is not null)
        {
            builder.Append(" [截图 ").Append(ShotFileName(entry)).Append(']');
        }

        return builder.ToString();
    }

    /// <summary>
    /// 纯文本导出（等宽友好）。<paramref name="header"/> 为 null 时自动生成导出说明。
    /// </summary>
    public static string ToText(IReadOnlyList<LogEntry> entries, LogOptions options, string? header = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        var builder = new StringBuilder(entries.Count * 96);
        builder.AppendLine(header ?? $"MaaYys 运行日志 · {entries.Count} 条 · 时间格式 {options.TimeFormat}");
        builder.AppendLine(new string('-', 60));

        foreach (LogEntry entry in entries)
        {
            builder.AppendLine(FormatLine(entry, options));
        }

        return builder.ToString();
    }

    /// <summary>CSV 导出（Excel 直接打开；UTF-8 BOM 由调用方写文件时决定）。</summary>
    public static string ToCsv(IReadOnlyList<LogEntry> entries, LogOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        var builder = new StringBuilder(entries.Count * 120);
        builder.AppendLine("时间,级别,来源,实例,分类,消息,详情,截图");

        foreach (LogEntry entry in entries)
        {
            builder.Append(Escape(options.FormatTime(entry.Time))).Append(',')
                   .Append(Escape(LogLevels.Abbreviation(entry.Level))).Append(',')
                   .Append(Escape(entry.Source)).Append(',')
                   .Append(Escape(entry.Scope)).Append(',')
                   .Append(Escape(entry.Category)).Append(',')
                   .Append(Escape(entry.Message)).Append(',')
                   .Append(Escape(entry.Details)).Append(',')
                   .Append(entry.Thumbnail is null ? string.Empty : ShotFileName(entry))
                   .AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>CSV 字段转义：含逗号/引号/换行时加引号并把内部引号翻倍。</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        bool needsQuote = value.IndexOfAny([',', '"', '\n', '\r']) >= 0;
        return needsQuote ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
