using System.Text.Json.Serialization;

namespace MaaYys.Engine.Config;

/// <summary>
/// 一个「配置组」（对标 MAA <c>gui.new.json</c> 的 Configurations 项）：
/// 一组命名好的任务队列 + 选项取值，可被多个实例共用，也可随实例各自一套。
/// <para>
/// 纯数据对象：不含运行态，可安全序列化 / 克隆 / 导入导出。
/// </para>
/// </summary>
public sealed class TaskConfiguration
{
    /// <summary>配置组名长度上限（下拉框宽度约束）。</summary>
    public const int MaxNameLength = 32;

    /// <summary>
    /// 配置组名。正常情况下与 <c>configurations</c> 字典的键一致；
    /// 单独导入/导出时靠它保留组名（加载时以字典键为准并自动纠正）。
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "默认配置";

    /// <summary>任务队列（顺序即执行顺序）。</summary>
    [JsonPropertyName("tasks")]
    public List<TaskSelection> Tasks { get; set; } = [];

    /// <summary>
    /// 全局选项取值（MXU setting 扩展）：与任务级选项同构（option key → 取值），
    /// 但作用域是「整个配置组的所有任务」。编译每个任务的 <c>pipeline_override</c> 时会先合并全局层、
    /// 再用任务级覆盖同名节点字段，因此任务级优先级更高。
    /// </summary>
    [JsonPropertyName("global_options")]
    public Dictionary<string, string> GlobalOptions { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>启用的任务数。</summary>
    [JsonIgnore]
    public int EnabledCount => Tasks.Count(t => t.Enabled);

    /// <summary>一句话摘要，供下拉框/列表第二行显示。</summary>
    [JsonIgnore]
    public string Summary => Tasks.Count == 0
        ? "空配置"
        : $"{EnabledCount}/{Tasks.Count} 个任务启用";

    /// <summary>按名字查找任务条目（多实例时返回第一个匹配）。</summary>
    public TaskSelection? FindTask(string name)
        => Tasks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>按实例 ID 查找队列项（多实例下唯一定位某个实例）。</summary>
    public TaskSelection? FindById(string id)
        => Tasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// 向队列追加一个任务实例，支持同类多实例。自动生成唯一 <see cref="TaskSelection.Id"/>。
    /// 返回新建的条目。
    /// </summary>
    public TaskSelection AddInstance(string name, string? alias = null)
    {
        var entry = new TaskSelection { Name = name, Alias = alias };
        entry.EnsureId();
        Tasks.Add(entry);
        return entry;
    }

    /// <summary>按实例 ID 移除队列项。</summary>
    public bool RemoveById(string id)
    {
        TaskSelection? found = FindById(id);
        return found is not null && Tasks.Remove(found);
    }

    /// <summary>
    /// 把指定实例移到 <paramref name="newIndex"/>（拖拽排序用）。越界自动夹到有效范围；
    /// 同位置直接返回。
    /// </summary>
    public void MoveById(string id, int newIndex)
    {
        int oldIndex = Tasks.FindIndex(t => string.Equals(t.Id, id, StringComparison.Ordinal));
        if (oldIndex < 0)
        {
            return;
        }

        newIndex = Math.Max(0, Math.Min(newIndex, Tasks.Count - 1));
        if (oldIndex == newIndex)
        {
            return;
        }

        TaskSelection item = Tasks[oldIndex];
        Tasks.RemoveAt(oldIndex);
        Tasks.Insert(newIndex, item);
    }

    /// <summary>
    /// 为所有缺少 Id 的条目补齐唯一 ID（旧配置 / 未分配项）。幂等，可安全重复调用。
    /// 加载配置后调用，确保 FindById 始终有效。
    /// </summary>
    public void EnsureIds()
    {
        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (TaskSelection task in Tasks)
        {
            if (string.IsNullOrEmpty(task.Id) || !used.Add(task.Id))
            {
                task.EnsureId();
                used.Add(task.Id);
            }
        }
    }

    /// <summary>
    /// 取任务的选项值；任务不存在或选项未设置时返回 <paramref name="fallback"/>。
    /// </summary>
    public string GetOption(string taskName, string optionKey, string fallback = "")
        => FindTask(taskName)?.GetOption(optionKey, fallback) ?? fallback;

    /// <summary>启用/禁用任务；任务不在队列里时追加（按需创建）。</summary>
    public TaskSelection EnsureTask(string name, bool enabled = true)
    {
        TaskSelection? existing = FindTask(name);
        if (existing is not null)
        {
            existing.Enabled = enabled;
            return existing;
        }

        var created = new TaskSelection { Name = name, Enabled = enabled };
        Tasks.Add(created);
        return created;
    }

    /// <summary>深拷贝（新名字），用于「复制配置组」。</summary>
    public TaskConfiguration Clone(string? newName = null) => new()
    {
        Name = newName ?? $"{Name} 副本",
        Tasks = Tasks.Select(t => t.Clone()).ToList(),
        GlobalOptions = new Dictionary<string, string>(GlobalOptions, StringComparer.Ordinal),
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
    };

    /// <summary>
    /// 校验配置组。返回中文错误列表，空表示通过。
    /// 这里只拦「明显坏掉」的情况：空名、重名、非法选项目录由调用方判断。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("配置组名不能为空");
        }
        else
        {
            if (Name.Length > MaxNameLength)
            {
                errors.Add($"配置组名不能超过 {MaxNameLength} 个字符（当前 {Name.Length}）");
            }

            if (Name.Any(char.IsControl))
            {
                errors.Add("配置组名不能包含控制字符");
            }
        }

        // 多实例：允许同一任务入口出现多次，唯一键用「name#id」，
        // id 缺省时用下标兜底，保证旧数据（每个任务唯一）仍能通过。
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < Tasks.Count; i++)
        {
            TaskSelection task = Tasks[i];
            if (string.IsNullOrWhiteSpace(task.Name))
            {
                errors.Add("任务名不能为空");
                continue;
            }

            string key = string.IsNullOrEmpty(task.Id) ? $"{task.Name}#{i}" : $"{task.Name}#{task.Id}";
            if (!seen.Add(key))
            {
                errors.Add($"任务「{task.Name}」在队列中重复出现");
            }
        }

        return errors;
    }
}
