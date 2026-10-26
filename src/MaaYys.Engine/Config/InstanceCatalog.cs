namespace MaaYys.Engine.Config;

/// <summary>可选任务（实例编辑器里勾选用）。</summary>
public sealed record InstanceTaskOption(string Entry, string Label, string? Group)
{
    public string Display => string.IsNullOrWhiteSpace(Label) ? Entry : $"{Label}（{Entry}）";
}

/// <summary>
/// 实例可选项清单：从 interface.json 读取资源集与任务列表。
/// P2.4 阶段仓库根尚无 interface.json（P3.6 才产出），因此**找不到文件必须降级**
/// ——返回内置默认项（资源 base + 空任务），编辑器仍可用，等 interface.json 落地后自动带出真实清单。
/// </summary>
public sealed class InstanceCatalog
{
    private static readonly IReadOnlyList<string> DefaultResources = ["base"];

    private InstanceCatalog(string? interfacePath)
    {
        InterfacePath = interfacePath;
    }

    /// <summary>interface.json 绝对路径（未找到为 null）。</summary>
    public string? InterfacePath { get; }

    /// <summary>是否成功读取到真实清单（false = 用的降级默认值）。</summary>
    public bool Loaded { get; private set; }

    /// <summary>可选资源集名（interface.json <c>resource[].name</c>）。</summary>
    public IReadOnlyList<string> ResourceNames { get; private set; } = DefaultResources;

    /// <summary>可选任务（<c>task[].entry</c>），按分组正序。</summary>
    public IReadOnlyList<InstanceTaskOption> Tasks { get; private set; } = [];

    /// <summary>加载问题（缺失/校验错误），供 UI 提示。</summary>
    public List<string> Errors { get; } = [];

    /// <summary>已加载时的分组名列表（未分组为空串）。</summary>
    public IReadOnlyList<string> Groups => Tasks
        .Select(t => t.Group ?? string.Empty)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// 加载清单；<paramref name="interfacePath"/> 为空或文件不存在时返回降级实例。
    /// </summary>
    public static InstanceCatalog Load(string? interfacePath)
    {
        var catalog = new InstanceCatalog(interfacePath);

        if (string.IsNullOrWhiteSpace(interfacePath) || !File.Exists(interfacePath))
        {
            catalog.Errors.Add("未找到 interface.json（P3.6 产出后自动读取资源与任务清单），当前使用默认选项");
            return catalog;
        }

        List<string> errors = [];
        InterfaceConfig config;
        try
        {
            config = InterfaceConfig.Load(interfacePath, errors);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            catalog.Errors.Add($"interface.json 读取失败：{ex.Message}，已降级为默认选项");
            return catalog;
        }

        catalog.Errors.AddRange(errors);

        List<string> resources = config.Resources
            .Select(r => r.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (resources.Count > 0)
        {
            catalog.ResourceNames = resources;
        }

        catalog.Tasks = config.MergedTasks
            .Where(t => !string.IsNullOrWhiteSpace(t.Entry))
            .Select(t => new InstanceTaskOption(
                t.Entry,
                string.IsNullOrWhiteSpace(t.Label) ? t.Name : t.Label!,
                t.Group is { Count: > 0 } ? t.Group[0] : null))
            .DistinctBy(t => t.Entry, StringComparer.Ordinal)
            .ToList();

        catalog.Loaded = true;
        return catalog;
    }

    /// <summary>任务总数（编辑器摘要显示）。</summary>
    public int TaskCount => Tasks.Count;
}
