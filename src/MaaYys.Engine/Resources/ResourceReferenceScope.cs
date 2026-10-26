using System.Text.Json;
using MaaYys.Engine.Config;

namespace MaaYys.Engine.Resources;

/// <summary>
/// pipeline 之外的引用来源。静态扫描只看 pipeline 会漏掉两类引用，
/// 导致把<b>活节点</b>误判成死节点：
/// <list type="bullet">
///   <item><c>tasks/*.json</c> 的 <c>task[].entry</c>：任务入口，没有任何 pipeline 节点引用它；</item>
///   <item><c>option[].cases[].pipeline_override</c> 与 <c>task[].pipeline_override</c>：
///         键是<b>被覆盖的节点名</b>，值里还可能含 <c>next</c>/<c>on_error</c> 指向别的节点。</item>
/// </list>
/// </summary>
public sealed record ResourceReferenceScope(
    IReadOnlyCollection<string> TaskEntries,
    IReadOnlyCollection<string> OverrideTargets,
    IReadOnlyCollection<string> OverrideReferences)
{
    public static ResourceReferenceScope Empty { get; } = new([], [], []);

    /// <summary>从已解析的 interface.json 汇总（含 import 进来的任务与选项）。</summary>
    public static ResourceReferenceScope FromConfig(InterfaceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var entries = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var references = new HashSet<string>(StringComparer.Ordinal);

        foreach (TaskConfig task in config.MergedTasks)
        {
            if (!string.IsNullOrWhiteSpace(task.Entry))
            {
                entries.Add(task.Entry!);
            }

            if (task.PipelineOverride is { } taskOverride)
            {
                CollectOverride(taskOverride, targets, references);
            }
        }

        foreach (OptionConfig option in config.MergedOptions.Values)
        {
            if (option.PipelineOverride is { } optionOverride)
            {
                CollectOverride(optionOverride, targets, references);
            }

            if (option.Cases is null)
            {
                continue;
            }

            foreach (CaseConfig optionCase in option.Cases)
            {
                if (optionCase.PipelineOverride is { } caseOverride)
                {
                    CollectOverride(caseOverride, targets, references);
                }
            }
        }

        return new ResourceReferenceScope([.. entries], [.. targets], [.. references]);
    }

    /// <summary>一个 pipeline_override 对象：键是被覆盖的节点，值里可能还有 next/on_error。</summary>
    internal static void CollectOverride(
        JsonElement overrideObject, HashSet<string> targets, HashSet<string> references)
    {
        if (overrideObject.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty node in overrideObject.EnumerateObject())
        {
            targets.Add(node.Name);

            if (node.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (string field in (string[])["next", "on_error", "interrupt"])
            {
                if (node.Value.TryGetProperty(field, out JsonElement list))
                {
                    CollectNodeNames(list, references);
                }
            }
        }
    }

    private static void CollectNodeNames(JsonElement value, HashSet<string> into)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            {
                string raw = value.GetString() ?? string.Empty;
                if (raw.StartsWith("[JumpBack]", StringComparison.Ordinal))
                {
                    raw = raw["[JumpBack]".Length..];
                }

                if (raw.StartsWith("[Anchor]", StringComparison.Ordinal) || raw.Length == 0)
                {
                    return;
                }

                into.Add(raw);
                break;
            }

            case JsonValueKind.Object:
                if (value.TryGetProperty("name", out JsonElement name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    CollectNodeNames(name, into);
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement element in value.EnumerateArray())
                {
                    CollectNodeNames(element, into);
                }

                break;
        }
    }
}
