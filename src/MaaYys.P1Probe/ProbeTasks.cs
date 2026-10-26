using System.Text.Json;
using MaaYys.Engine.Config;

namespace MaaYys.P1Probe;

/// <summary>
/// 任务定义与选项编译（P3.4 验收）。
/// 用法：<c>tasks [--root 目录] [--task 任务名] [--value "选项=取值" ...] [--json]</c>
///
/// 不带 <c>--task</c>：列出全部 import 任务的选项数量与每个选项的取值（相当于界面上的参数面板要渲染什么）。
/// 带 <c>--task</c>：打印该任务编译后的 <c>pipeline_override</c> JSON（P5 投递任务时用的就是它）。
/// </summary>
internal static class ProbeTasks
{
    public static int Run(string[] args)
    {
        string root = ReadOption(args, "--root")
                      ?? Environment.GetEnvironmentVariable("MAAYYS_RESOURCE_ROOT")
                      ?? Directory.GetCurrentDirectory();

        string? interfacePath = LocateInterfaceJson(root);
        if (interfacePath is null)
        {
            Console.WriteLine($"[失败] 在 {root} 及其上级目录中找不到 interface.json");
            return 1;
        }

        List<string> errors = [];
        InterfaceConfig config = InterfaceConfig.Load(interfacePath, errors);
        Console.WriteLine($"[清单] {interfacePath}");
        Console.WriteLine($"[任务] {config.MergedTasks.Count} 个 · [选项] {config.MergedOptions.Count} 个 · import {config.ImportedFiles.Count} 个文件");

        if (errors.Count > 0)
        {
            Console.WriteLine($"[问题] {errors.Count} 条");
            foreach (string error in errors.Take(10))
            {
                Console.WriteLine($"  - {error}");
            }
        }

        Dictionary<string, string> values = ReadValues(args);

        string? taskName = ReadOption(args, "--task");
        if (taskName is null)
        {
            return ListTasks(config, args.Contains("--json"));
        }

        TaskConfig? task = config.MergedTasks.FirstOrDefault(t => string.Equals(t.Name, taskName, StringComparison.Ordinal))
                           ?? config.MergedTasks.FirstOrDefault(t =>
                               string.Equals(t.DisplayName, taskName, StringComparison.Ordinal));
        if (task is null)
        {
            Console.WriteLine($"[失败] 找不到任务「{taskName}」");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"=== 任务「{task.DisplayName}」选项面板 ===");
        IReadOnlyList<TaskOptionDefinition> rows = TaskOptions.Describe(config, task, values);
        foreach (TaskOptionDefinition row in rows)
        {
            string indent = new(' ', row.Indent + 2);
            string current = row.Type switch
            {
                "checkbox" => string.Join('/', TaskOptions.ResolveValues(
                    config.MergedOptions[row.Key], values)),
                "input" => string.Join(", ", row.Inputs.Select(i =>
                    $"{i.Label}={TaskOptions.ResolveInput(config.MergedOptions[row.Key], ToInput(config, row.Key, i), values)}")),
                _ => TaskOptions.ResolveValue(config.MergedOptions[row.Key], values) ?? "(未选)",
            };

            Console.WriteLine($"{indent}- [{row.Type}] {row.Label} = {current}");
            if (row.Choices.Count > 0)
            {
                Console.WriteLine($"{indent}    可选：{string.Join(" / ", row.Choices.Select(c => c.Label))}");
            }
        }

        string overrideJson = TaskOptions.Compile(config, task, values, out IReadOnlyList<string> warnings);
        Console.WriteLine();
        Console.WriteLine("=== 编译后的 pipeline_override ===");
        Console.WriteLine(Pretty(overrideJson));

        if (warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"[提示] {warnings.Count} 条");
            foreach (string warning in warnings)
            {
                Console.WriteLine($"  - {warning}");
            }
        }

        return 0;
    }

    private static InputConfig ToInput(InterfaceConfig config, string optionKey, OptionInputField field)
        => new()
        {
            Name = field.Name,
            Label = field.Label,
            Default = field.Default,
            PipelineType = field.PipelineType,
        };

    private static int ListTasks(InterfaceConfig config, bool json)
    {
        Console.WriteLine();
        Console.WriteLine("=== 任务与选项 ===");
        int totalRows = 0;
        foreach (TaskConfig task in config.MergedTasks)
        {
            IReadOnlyList<TaskOptionDefinition> rows = TaskOptions.Describe(config, task);
            totalRows += rows.Count;
            string groups = task.Group is { Count: > 0 } ? string.Join("/", task.Group) : "未分组";
            Console.WriteLine($"  · {task.DisplayName}｜入口 {task.Entry}｜{groups}｜{rows.Count} 项参数");
            foreach (TaskOptionDefinition row in rows.Where(r => r.Level == 0))
            {
                int children = rows.Count(r => r.ParentKey == row.Key);
                string type = row.Type switch
                {
                    "select" or "switch" => $"{row.Type}({row.Choices.Count} 选 1)",
                    "checkbox" => $"checkbox({row.Choices.Count} 多选)",
                    "input" => $"input({row.Inputs.Count} 字段)",
                    _ => row.Type,
                };
                Console.WriteLine($"      {row.Label} — {type}{(children > 0 ? $" + {children} 个联动子项" : string.Empty)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"[统计] 29 个已接线任务共渲染 {totalRows} 行参数面板");

        // 选项库里定义但没有任何任务引用的项（含 case 的联动子选项）——数据残留，界面上看不到
        HashSet<string> referenced = new(StringComparer.Ordinal);
        foreach (TaskConfig task in config.MergedTasks)
        {
            foreach (TaskOptionDefinition row in TaskOptions.Describe(config, task))
            {
                referenced.Add(row.Key);
            }
        }

        List<string> orphan = [.. config.MergedOptions.Keys.Where(k => !referenced.Contains(k))];
        if (orphan.Count > 0)
        {
            Console.WriteLine($"[未引用选项] {orphan.Count} 个（定义了但没有任何任务引用，界面上不会渲染）");
            foreach (string key in orphan)
            {
                Console.WriteLine($"  · {key}（{config.MergedOptions[key].Type}）");
            }
        }

        if (json)
        {
            var dump = config.MergedTasks.Select(t => new
            {
                name = t.Name,
                entry = t.Entry,
                options = TaskOptions.Describe(config, t).Select(r => new
                {
                    key = r.Key,
                    label = r.Label,
                    type = r.Type,
                    level = r.Level,
                    parent = r.ParentKey,
                    choices = r.Choices.Select(c => c.Name),
                }),
            });
            Console.WriteLine(JsonSerializer.Serialize(dump, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }

        return 0;
    }

    private static Dictionary<string, string> ReadValues(string[] args)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--value")
            {
                continue;
            }

            string raw = args[i + 1];
            int split = raw.IndexOf('=');
            if (split > 0)
            {
                values[raw[..split]] = raw[(split + 1)..];
            }
        }

        return values;
    }

    private static string Pretty(string json)
    {
        try
        {
            return JsonSerializer.Serialize(
                JsonSerializer.Deserialize<JsonElement>(json),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string? LocateInterfaceJson(string root)
    {
        string? current = Path.GetFullPath(root);
        for (int i = 0; i < 6 && current is not null; i++)
        {
            string candidate = Path.Combine(current, "interface.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string nested = Path.Combine(current, "resource", "interface.json");
            if (File.Exists(nested))
            {
                return nested;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }
}
