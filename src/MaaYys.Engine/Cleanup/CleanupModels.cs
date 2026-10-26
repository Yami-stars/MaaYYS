using System.Text.Json.Serialization;

namespace MaaYys.Engine.Cleanup;

/// <summary>清理项的种类（决定「怎么清」：删文件 / 删节点 / 挪备份）。</summary>
public enum CleanupCategory
{
    /// <summary>整份 pipeline 文件都不可达，可整体移出。</summary>
    DeadFile,

    /// <summary>文件里只有部分节点不可达，只删这些节点。</summary>
    DeadNodes,

    /// <summary>运行时自动产生的配置备份（<c>*.bak</c> / <c>*.bad-时间戳</c>）。</summary>
    ConfigBackup,

    /// <summary>没有任何节点引用的模板图。</summary>
    UnusedTemplate,

    /// <summary>没有任何节点引用的 ONNX 模型。</summary>
    UnusedModel,
}

/// <summary>
/// 风险等级。<see cref="Safe"/> 可直接勾选；<see cref="Review"/> 要用户确认；
/// <see cref="Protected"/> 不提供清理（但会列出来，让用户知道「为什么这些东西没被清掉」）。
/// </summary>
public enum CleanupRisk
{
    Safe,
    Review,
    Protected,
}

/// <summary>一条可清理项。所有操作都只针对 <see cref="RelativePath"/>，不改动其它文件。</summary>
public sealed class CleanupItem
{
    /// <summary>稳定 Id（跨扫描不变），用于记住用户勾选。</summary>
    public required string Id { get; init; }

    public CleanupCategory Category { get; init; }

    public CleanupRisk Risk { get; init; }

    /// <summary>相对资源根目录/<c>resource_pack/base</c> 的路径，例如 <c>pipeline/日常/寮30.json</c>。</summary>
    public required string RelativePath { get; init; }

    /// <summary>涉及的死节点名（<see cref="CleanupCategory.DeadNodes"/> / <see cref="CleanupCategory.DeadFile"/>）。</summary>
    public IReadOnlyList<string> Nodes { get; init; } = [];

    /// <summary>文件体积（节点级清理时是「该文件体积」，不是精确释放量）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>为什么判定成冗余——直接显示给用户，避免「不知道为什么被选中」。</summary>
    public required string Reason { get; init; }

    /// <summary>是否允许勾选清理。</summary>
    [JsonIgnore]
    public bool CanClean => Risk != CleanupRisk.Protected;

    [JsonIgnore]
    public string CategoryLabel => Category switch
    {
        CleanupCategory.DeadFile => "整份死文件",
        CleanupCategory.DeadNodes => "不可达节点",
        CleanupCategory.ConfigBackup => "配置备份",
        // 模型一律受保护（不可再生），所以类别名不带「未引用」——它描述的是资产类型而非判定结论
        CleanupCategory.UnusedModel => "模型（受保护）",
        CleanupCategory.UnusedTemplate => "未引用模板图",
        _ => Category.ToString(),
    };

    [JsonIgnore]
    public string RiskLabel => Risk switch
    {
        CleanupRisk.Safe => "可清理",
        CleanupRisk.Review => "需确认",
        _ => "受保护",
    };

    [JsonIgnore]
    public string SizeText => SizeBytes switch
    {
        <= 0 => "—",
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        _ => $"{SizeBytes / 1024.0 / 1024.0:F1} MB",
    };

    [JsonIgnore]
    public string NodeSummary => Nodes.Count == 0
        ? string.Empty
        : $"{Nodes.Count} 个节点：{string.Join("、", Nodes.Take(6))}{(Nodes.Count > 6 ? " …" : string.Empty)}";

    public override string ToString() => $"[{CategoryLabel}][{RiskLabel}] {RelativePath}";
}

/// <summary>
/// 一个「没接线」的任务：<c>tasks/</c> 下有定义，但 <c>interface.json</c> 的 import 里没引用它。
/// <para>
/// 这类任务目前<b>故意隐藏</b>（活动没开 / 玩法在调整）。它们的 pipeline 节点在可达性分析里
/// 会全部判成「不可达」，但绝不能当垃圾清掉——下个版本活动一开，把文件加回 import 就能用。
/// 所以审计把它们当作<b>受保护根</b>，这些节点只会出现在「受保护」清单里。
/// </para>
/// </summary>
public sealed class HiddenTaskInfo
{
    /// <summary>任务内部名（<c>task[].name</c>）。</summary>
    public required string Name { get; init; }

    /// <summary>界面显示名（<c>label</c>，缺省回退 name）。</summary>
    public required string Label { get; init; }

    /// <summary>pipeline 入口节点。</summary>
    public required string Entry { get; init; }

    /// <summary>相对 interface.json 的任务文件，例如 <c>tasks/限时活动.json</c>。</summary>
    public required string File { get; init; }

    /// <summary>该任务声明的选项数量。</summary>
    public int OptionCount { get; init; }

    /// <summary>启用方法（一句话，直接显示在界面里）。</summary>
    [JsonIgnore]
    public string EnableHint => $"在 interface.json 的 import 里加入 \"{File}\" 即可启用";

    public override string ToString() => $"{Label}（{File}）";
}

/// <summary>审计请求。</summary>
public sealed class AuditRequest
{
    /// <summary>interface.json 绝对路径。</summary>
    public required string InterfacePath { get; init; }

    /// <summary>资源集名（<c>resource[].name</c>）；为空时取第一个可用资源集。</summary>
    public string? ResourceSetName { get; init; }

    /// <summary>是否扫描未引用的模板图/模型（默认开；关闭可加快扫描）。</summary>
    public bool IncludeAssets { get; init; } = true;

    /// <summary>配置目录（扫 <c>*.bak</c> / <c>*.bad-*</c>）；null 时用程序目录下的 <c>config/</c>。</summary>
    public string? ConfigDirectory { get; init; }
}

/// <summary>扫描结果。</summary>
public sealed class ResourceAuditResult
{
    public required string InterfacePath { get; init; }

    /// <summary>interface.json 所在目录（资源根）。</summary>
    public required string ResourceRoot { get; init; }

    public required string ResourceSetName { get; init; }

    /// <summary>资源链上的 bundle 目录（按加载顺序）。</summary>
    public IReadOnlyList<string> BundlePaths { get; init; } = [];

    /// <summary>扫描过的配置目录（配置备份项的定位基准）。</summary>
    public string ConfigDirectory { get; init; } = string.Empty;

    public IReadOnlyList<CleanupItem> Items { get; init; } = [];

    public IReadOnlyList<HiddenTaskInfo> HiddenTasks { get; init; } = [];

    /// <summary>加载/解析阶段的问题（interface.json 缺失项、JSON 解析失败等）。</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public int TotalNodes { get; init; }

    public int LiveNodes { get; init; }

    /// <summary>真死节点数（已排除隐藏任务与活动轮换包）。</summary>
    public int DeadNodes { get; init; }

    /// <summary>只因隐藏任务而存活的节点数。</summary>
    public int ProtectedByHiddenTasks { get; init; }

    /// <summary>活动轮换包 / 全局兜底链等「故意保留」的不可达节点数。</summary>
    public int ProtectedByConvention { get; init; }

    public int TemplateTotal { get; init; }

    public int TemplateUnused { get; init; }

    public int ModelTotal { get; init; }

    public int ModelUnused { get; init; }

    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;

    public TimeSpan Duration { get; init; }

    /// <summary>可直接勾选的项。</summary>
    public IReadOnlyList<CleanupItem> Cleanable => [.. Items.Where(i => i.CanClean)];

    public IReadOnlyList<CleanupItem> Protected => [.. Items.Where(i => !i.CanClean)];

    /// <summary>预计释放空间（只统计整份移出的文件；节点级清理按实际重写前后差值算）。</summary>
    public long EstimatedBytes =>
        Items.Where(i => i.CanClean && i.Category != CleanupCategory.DeadNodes).Sum(i => i.SizeBytes);

    public string Summary
    {
        get
        {
            int safe = Items.Count(i => i.CanClean && i.Risk == CleanupRisk.Safe);
            int review = Items.Count(i => i.CanClean && i.Risk == CleanupRisk.Review);
            return $"节点 {TotalNodes} 个（可达 {LiveNodes} / 不可达 {DeadNodes}）"
                   + $" · 可清理 {safe} 项 + 需确认 {review} 项 · 受保护 {Items.Count(i => !i.CanClean)} 项"
                   + $" · 预计释放 {FormatBytes(EstimatedBytes)}";
        }
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };
}
