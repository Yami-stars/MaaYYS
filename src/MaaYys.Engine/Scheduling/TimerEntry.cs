using System.Text.Json.Serialization;

namespace MaaYys.Engine.Scheduling;

/// <summary>
/// 一条定时计划（P5.4），对标 MAA 的 <c>Timer</c>：每天固定「时:分」触发某个配置组。
/// <para>
/// 周计划用位掩码表示：位索引 = <see cref="DayOfWeek"/> 的数值（0=周日 … 6=周六），
/// 与 .NET 的枚举一致，免去「MAA 用 0=周日、cron 用 1=周一」这类换算歧义。
/// </para>
/// </summary>
public sealed class TimerEntry
{
    /// <summary>实例唯一标识（界面增删与去重用）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>备注名（可空，仅用于界面区分同点触发）。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>小时（0-23）。</summary>
    [JsonPropertyName("hour")]
    public int Hour { get; set; }

    /// <summary>分钟（0-59）。</summary>
    [JsonPropertyName("minute")]
    public int Minute { get; set; }

    /// <summary>到点要运行的配置组名。</summary>
    [JsonPropertyName("configuration")]
    public string Configuration { get; set; } = string.Empty;

    /// <summary>是否启用。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>周计划位掩码；<see cref="TimerRules.AllWeekdays"/> 表示每天。</summary>
    [JsonPropertyName("weekdays")]
    public int Weekdays { get; set; } = TimerRules.AllWeekdays;

    /// <summary>
    /// 强制定时启动：到点时已有队列在跑，先停止再启动本配置；
    /// false 表示跳过本次（记录原因，等下一周期）。
    /// </summary>
    [JsonPropertyName("force_start")]
    public bool ForceStart { get; set; }

    /// <summary>
    /// 最近一次触发的分钟键（<c>yyyyMMddHHmm</c>）。持久化它是为了让
    /// 「同一分钟内重启程序」不重复触发。
    /// </summary>
    [JsonPropertyName("last_fired")]
    public string? LastFiredKey { get; set; }

    /// <summary>补一个唯一 Id（旧数据 / 新建时用）。</summary>
    public void EnsureId()
    {
        if (string.IsNullOrEmpty(Id))
        {
            Id = Guid.NewGuid().ToString("N");
        }
    }

    public TimerEntry Clone() => new()
    {
        Id = Id,
        Label = Label,
        Hour = Hour,
        Minute = Minute,
        Configuration = Configuration,
        Enabled = Enabled,
        Weekdays = Weekdays,
        ForceStart = ForceStart,
        LastFiredKey = LastFiredKey,
    };

    public override string ToString() => $"{Hour:00}:{Minute:00} → {Configuration}";
}
