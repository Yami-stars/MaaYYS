namespace MaaYys.Engine.Logging;

/// <summary>
/// Go agent 子进程日志的解析（P5.5「agent 日志合并」）。
/// <para>
/// agent 用 Go <c>slog</c> 的文本 handler，形如：
/// <c>time=2026-10-27T13:00:00.000+08:00 level=INFO msg="agent started" port=7788</c>。
/// 也兼容 <c>[WARN] ...</c> / <c>ERROR: ...</c> 这类常见写法。
/// </para>
/// </summary>
public static class AgentLogParser
{
    /// <summary>从一行文本猜级别；识别不出时按 <paramref name="defaultLevel"/> 处理。</summary>
    public static LogLevel DetectLevel(string? line, LogLevel defaultLevel = LogLevel.Info)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return defaultLevel;
        }

        // 1) Go slog：level=INFO / level="warn"
        int marker = line.IndexOf("level=", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            string rest = line[(marker + "level=".Length)..].TrimStart('"');
            int end = 0;
            while (end < rest.Length && (char.IsLetter(rest[end]) || rest[end] == '_'))
            {
                end++;
            }

            if (end > 0 && LogLevels.TryParse(rest[..end], out LogLevel level))
            {
                return level;
            }
        }

        // 2) [WARN] / [ERROR] 前缀
        int open = line.IndexOf('[');
        if (open >= 0)
        {
            int close = line.IndexOf(']', open + 1);
            if (close > open + 1 && LogLevels.TryParse(line[(open + 1)..close], out LogLevel level))
            {
                return level;
            }
        }

        // 3) 行首大写级别 + 冒号/空格：INFO: ... / ERROR ...
        string trimmed = line.TrimStart();
        int word = 0;
        while (word < trimmed.Length && char.IsLetter(trimmed[word]))
        {
            word++;
        }

        if (word is >= 3 and <= 10 && LogLevels.TryParse(trimmed[..word], out LogLevel head))
        {
            return head;
        }

        return defaultLevel;
    }

    /// <summary>
    /// 清理一行：去掉行尾 CR/空白；slog 的 <c>time=…</c> 前缀保留（时间以本机落库时间为准，
    /// 但原始时间在排查时有用，故不做裁剪）。
    /// </summary>
    public static string Clean(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        return line.TrimEnd('\r', '\n', ' ', '\t');
    }

    /// <summary>该行是否值得落库（空行丢弃）。</summary>
    public static bool IsMeaningful(string? line) => !string.IsNullOrWhiteSpace(line);
}
