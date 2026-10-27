namespace MaaYys.Engine.Scheduling;

/// <summary>
/// 定时计划的纯函数规则（无状态、无时钟依赖），供 <see cref="TimerScheduler"/>、界面与测试共用。
/// </summary>
public static class TimerRules
{
    /// <summary>每天（位 0-6 全置位）。</summary>
    public const int AllWeekdays = 0x7F;

    /// <summary>工作日（周一至周五）。</summary>
    public const int Workdays = 0b0011_1110;

    /// <summary>周末（周日 + 周六）。</summary>
    public const int Weekend = 0b0100_0001;

    /// <summary>界面上周几的展示顺序：周一 … 周日（对应 <see cref="DayOfWeek"/> 1,2,3,4,5,6,0）。</summary>
    public static readonly DayOfWeek[] DisplayOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    /// <summary>时/分是否在合法范围。</summary>
    public static bool IsValidTime(int hour, int minute) => hour is >= 0 and <= 23 && minute is >= 0 and <= 59;

    /// <summary>整条计划是否可用（周计划为空视作每天，不算非法）。</summary>
    public static bool IsValid(TimerEntry entry)
        => entry is not null
           && IsValidTime(entry.Hour, entry.Minute)
           && !string.IsNullOrWhiteSpace(entry.Configuration);

    /// <summary>就地夹取到合法范围（手改配置文件时用）。</summary>
    public static void Normalize(TimerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Hour = Math.Clamp(entry.Hour, 0, 23);
        entry.Minute = Math.Clamp(entry.Minute, 0, 59);
        entry.Weekdays &= AllWeekdays;
        if (entry.Weekdays == 0)
        {
            entry.Weekdays = AllWeekdays;
        }

        entry.Label = string.IsNullOrWhiteSpace(entry.Label) ? null : entry.Label.Trim();
        entry.Configuration = (entry.Configuration ?? string.Empty).Trim();
    }

    /// <summary>该星期是否被计划包含。</summary>
    public static bool IncludesWeekday(int mask, DayOfWeek day) => (mask & (1 << (int)day)) != 0;

    /// <summary>分钟键：同一分钟内只触发一次。</summary>
    public static string MinuteKey(DateTimeOffset moment) => moment.ToString("yyyyMMddHHmm");

    /// <summary>时/分是否命中（不看星期 / 启用）。</summary>
    public static bool MatchesTime(TimerEntry entry, DateTimeOffset now)
        => entry.Hour == now.Hour && entry.Minute == now.Minute;

    /// <summary>是否应在这一分钟触发：启用 + 时/分命中 + 星期命中 + 本分钟未触发过。</summary>
    public static bool ShouldFire(TimerEntry entry, DateTimeOffset now)
        => entry.Enabled
           && IsValid(entry)
           && MatchesTime(entry, now)
           && IncludesWeekday(entry.Weekdays, now.DayOfWeek)
           && !string.Equals(entry.LastFiredKey, MinuteKey(now), StringComparison.Ordinal);

    /// <summary>
    /// 下一次触发时刻（严格晚于 <paramref name="after"/>）；无合法配置时返回 null。
    /// </summary>
    public static DateTimeOffset? NextOccurrence(TimerEntry entry, DateTimeOffset after)
    {
        if (!IsValid(entry))
        {
            return null;
        }

        for (int dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            DateTime day = after.Date.AddDays(dayOffset);
            var candidate = new DateTimeOffset(
                day.Year, day.Month, day.Day, entry.Hour, entry.Minute, 0, after.Offset);

            if (candidate <= after)
            {
                continue;
            }

            if (!IncludesWeekday(entry.Weekdays, candidate.DayOfWeek))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    /// <summary>周计划的文字描述（每天 / 工作日 / 周末 / 周一、周三…）。</summary>
    public static string DescribeWeekdays(int mask)
    {
        mask &= AllWeekdays;
        return mask switch
        {
            AllWeekdays => "每天",
            Workdays => "工作日",
            Weekend => "周末",
            0 => "未选择",
            _ => string.Join("、", DisplayOrder
                .Where(d => IncludesWeekday(mask, d))
                .Select(ShortName)),
        };
    }

    /// <summary>周几的单字名（周一 → 一）。</summary>
    public static string ShortName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日",
    };
}
