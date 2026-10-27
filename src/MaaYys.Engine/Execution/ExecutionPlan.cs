using System.Text.Json;
using MaaYys.Engine.Config;

namespace MaaYys.Engine.Execution;

/// <summary>
/// 把配置组的任务队列编译成 <see cref="ExecutionItem"/> 列表（任务级选项 + 全局选项合并）。
/// <para>
/// 抽到引擎层的原因：投递契约（entry 名 + <c>pipeline_override</c>）属于引擎职责，
/// 界面只负责展示；这样执行项构造也能被单测覆盖，不必启动 WPF。
/// </para>
/// </summary>
public static class ExecutionPlan
{
    /// <summary>按队列顺序批量编译。</summary>
    public static List<ExecutionItem> Build(
        InterfaceConfig config,
        IEnumerable<TaskSelection> tasks,
        IReadOnlyDictionary<string, string>? globalOptions = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tasks);

        List<ExecutionItem> items = [];
        foreach (TaskSelection task in tasks)
        {
            items.Add(BuildItem(config, task, globalOptions));
        }

        return items;
    }

    /// <summary>
    /// 编译单个队列项。清单里找不到对应任务、或选项编译失败时，
    /// <see cref="ExecutionItem.PipelineOverride"/> 为 null（用清单里的默认 pipeline 投递）。
    /// </summary>
    public static ExecutionItem BuildItem(
        InterfaceConfig config,
        TaskSelection task,
        IReadOnlyDictionary<string, string>? globalOptions = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(task);

        TaskConfig? definition = config.MergedTasks.FirstOrDefault(
            t => string.Equals(t.Name, task.Name, StringComparison.Ordinal));

        string? overrideJson = null;
        if (definition is not null)
        {
            try
            {
                overrideJson = TaskOptions.Compile(config, definition, task.Options, globalOptions, out _);
            }
            catch (JsonException)
            {
                overrideJson = null;
            }
        }

        return new ExecutionItem(task.Id, task.Name, overrideJson, task.DisplayName, task.Enabled);
    }
}
