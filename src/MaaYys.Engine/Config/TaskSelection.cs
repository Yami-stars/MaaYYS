using System.Text.Json.Serialization;

namespace MaaYys.Engine.Config;

/// <summary>
/// 配置组里的一个任务条目（队列项）：任务入口名 + 实例 ID + 可选显示名 + 是否启用 + 选项取值。
/// <para>
/// 只存「用户选择」，不存 interface.json 里的定义——定义永远以资源清单为准，
/// 这样资源更新后新增的 option 会自动以默认值出现，旧配置不会因为缺字段而失效。
/// </para>
/// </summary>
public sealed class TaskSelection
{
    /// <summary>任务入口名（对应 interface.json <c>task[].name</c> / pipeline entry）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 队列项实例唯一 ID。空字符串表示「未分配 / 单实例（旧数据兼容）」。
    /// 同类多实例时由程序生成（Guid），用于区分同一任务入口的多个队列项。
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 用户自定义显示名（重命名）。为空时界面回退到 <see cref="Name"/>。
    /// 不参与引擎投递，仅用于 UI 区分多个同类实例。
    /// </summary>
    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    /// <summary>是否加入任务队列。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 选项取值：option key → 取值。
    /// <para>
    /// 统一存字符串：PI V2 的 Select/Switch 取值为 case 名，Input 以字符串承载数字，
    /// 投递到 pipeline override 前再按 option 类型做转换。用字符串可避免
    /// JSON 数字/布尔来回转换丢精度或类型漂移。
    /// </para>
    /// </summary>
    [JsonPropertyName("options")]
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.Ordinal);

    public TaskSelection Clone() => new()
    {
        Name = Name,
        Id = Id,
        Alias = Alias,
        Enabled = Enabled,
        Options = new Dictionary<string, string>(Options, StringComparer.Ordinal),
    };

    /// <summary>取某个选项的值；未设置时返回 <paramref name="fallback"/>。</summary>
    public string GetOption(string key, string fallback = "")
        => Options.TryGetValue(key, out string? value) ? value : fallback;

    /// <summary>设置选项值（null/空串视为清除，回到默认值）。</summary>
    public void SetOption(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Options.Remove(key);
        }
        else
        {
            Options[key] = value;
        }
    }

    /// <summary>界面显示名：有别名用别名，否则用任务入口名。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias!;

    /// <summary>若 <see cref="Id"/> 为空，生成一个新的实例 ID（就地修改）。</summary>
    public void EnsureId()
    {
        if (string.IsNullOrEmpty(Id))
        {
            Id = Guid.NewGuid().ToString("N");
        }
    }

    [JsonIgnore]
    public string Summary => Options.Count == 0 ? Name : $"{Name}（{Options.Count} 项已改）";
}
