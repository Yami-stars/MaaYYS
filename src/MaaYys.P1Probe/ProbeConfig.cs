using System.Text.Json;
using MaaYys.Engine.Config;

namespace MaaYys.P1Probe;

/// <summary>
/// 配置组验证（P2.6）。直接在真实文件上跑，用来证明「配置改动重启后保持」与「迁移旧配置不丢」。
///
/// 用法：
///   <c>config [--path maayys.json] [--list]</c>
///   <c>config --add 名字</c>             新建配置组
///   <c>config --task "组名|任务名"</c>    给组追加/启用一个任务
///   <c>config --set 名字</c>             切换 Current
///   <c>config --rename "旧名|新名"</c>   重命名
///   <c>config --remove 名字</c>          删除
///   <c>config --import 文件</c> / <c>--export 文件</c>
///   <c>config --migrate instances.json</c>  按 InstanceWorkspace 的方式做一次 P2.4 → P2.6 迁移并落盘
///
/// 多个操作按命令行出现顺序依次执行（便于「改 → 切 → 导」这类串联验证）。
/// </summary>
internal static class ProbeConfig
{
    public static int Run(string[] args)
    {
        string path = ReadOption(args, "--path")
                      ?? Path.Combine(Directory.GetCurrentDirectory(), "config", ConfigurationStore.FileName);

        Console.WriteLine($"[配置] {path}");

        var store = new ConfigurationStore(path);

        try
        {
            if (ReadOption(args, "--migrate") is { } instancesPath)
            {
                return Migrate(store, instancesPath);
            }

            store.Load();
            foreach (string error in store.LoadErrors)
            {
                Console.WriteLine($"  ⚠ {error}");
            }

            int exit = 0;
            foreach ((string key, string? value) in ReadOperations(args))
            {
                switch (key)
                {
                    case "--add":
                        Console.WriteLine($"  + 新建「{store.Add(value).Name}」");
                        break;

                    case "--task":
                    {
                        string[] parts = (value ?? string.Empty).Split('|', 2);
                        if (parts.Length != 2)
                        {
                            Console.WriteLine("  ✗ --task 需要写成 \"组名|任务名\"");
                            return 2;
                        }

                        TaskConfiguration? group = store.Find(parts[0]);
                        if (group is null)
                        {
                            Console.WriteLine($"  ✗ 配置组不存在：{parts[0]}");
                            return 2;
                        }

                        group.EnsureTask(parts[1], enabled: true);
                        store.Touch(group.Name);
                        Console.WriteLine($"  + 「{group.Name}」启用任务「{parts[1]}」");
                        break;
                    }

                    case "--set":
                    {
                        string target = value ?? string.Empty;
                        if (store.Find(target) is null)
                        {
                            Console.WriteLine($"  ✗ 配置组不存在：{target}");
                            exit = 3;
                        }
                        else if (string.Equals(store.Current, target, StringComparison.Ordinal))
                        {
                            Console.WriteLine($"  = 「{target}」已经是当前配置组");
                        }
                        else
                        {
                            store.SetCurrent(target);
                            Console.WriteLine($"  → Current = {store.Current}");
                        }

                        break;
                    }

                    case "--rename":
                    {
                        string[] parts = (value ?? string.Empty).Split('|', 2);
                        if (parts.Length != 2)
                        {
                            Console.WriteLine("  ✗ --rename 需要写成 \"旧名|新名\"");
                            return 2;
                        }

                        if (!store.Rename(parts[0], parts[1], out string? error))
                        {
                            Console.WriteLine($"  ✗ 重命名失败：{error}");
                            exit = 3;
                        }
                        else
                        {
                            Console.WriteLine($"  → 「{parts[0]}」改名为「{parts[1]}」");
                        }

                        break;
                    }

                    case "--remove":
                        if (!store.Remove(value ?? string.Empty, out string? removeError))
                        {
                            Console.WriteLine($"  ✗ 删除失败：{removeError}");
                            exit = 3;
                        }
                        else
                        {
                            Console.WriteLine($"  - 已删除「{value}」");
                        }

                        break;

                    case "--import":
                    {
                        ImportResult result = store.Import(value ?? string.Empty);
                        if (!result.Success)
                        {
                            Console.WriteLine($"  ✗ 导入失败：{result.Error}");
                            exit = 4;
                        }
                        else
                        {
                            Console.WriteLine($"  ← 导入 {result.Imported.Count} 组：{string.Join("、", result.Imported)}");
                            foreach (string note in result.Errors)
                            {
                                Console.WriteLine($"    · {note}");
                            }
                        }

                        break;
                    }

                    case "--export":
                        Console.WriteLine(store.Export(value ?? string.Empty)
                            ? $"  → 已导出到 {value}"
                            : $"  ✗ 导出失败：{value}");
                        break;

                    case "--list":
                        Print(store);
                        break;

                    default:
                        Console.WriteLine($"  ? 忽略未知参数 {key}");
                        break;
                }
            }

            if (ReadOperations(args).Count == 0)
            {
                Print(store);
            }

            return exit;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  ✗ {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void Print(ConfigurationStore store)
    {
        Console.WriteLine($"  ConfigVersion={ConfigurationStore.ConfigVersion} | Current={store.Current ?? "(无)"} | 配置组 {store.Configurations.Count} 个");
        foreach (TaskConfiguration group in store.Configurations)
        {
            Console.WriteLine($"    {(group.Name == store.Current ? "★" : " ")} {group.Name}  [{group.Summary}]");
            foreach (TaskSelection task in group.Tasks)
            {
                string options = task.Options.Count == 0
                    ? string.Empty
                    : "  {" + string.Join(", ", task.Options.Select(o => $"{o.Key}={o.Value}")) + "}";
                Console.WriteLine($"         {(task.Enabled ? "✓" : "·")} {task.Name}{options}");
            }
        }
    }

    /// <summary>
    /// 复刻 InstanceWorkspace 的迁移流程：先读实例清单，再让配置存储做迁移，
    /// 迁移有改动则把 instances.json 落盘（写入 configuration_name）。
    /// </summary>
    private static int Migrate(ConfigurationStore store, string instancesPath)
    {
        if (!File.Exists(instancesPath))
        {
            Console.WriteLine($"  ✗ 实例清单不存在：{instancesPath}");
            return 1;
        }

        // 迁移前的手写清单在解析时用的是 InstanceStore 自己的规则，这里直接复用它的 Load
        var instances = new InstanceStore(instancesPath);
        instances.Load();
        Console.WriteLine($"  [迁移前] 实例 {instances.Instances.Count} 个");
        foreach (InstanceDefinition instance in instances.Instances)
        {
            Console.WriteLine($"    · {instance.Name}  task_entries={instance.TaskEntries.Count}  configuration_name=\"{instance.ConfigurationName}\"");
        }

        bool touched = store.Load(instances.Instances);
        Console.WriteLine($"  [迁移] 配置组 {store.Configurations.Count} 个（实例被改写：{(touched ? "是" : "否")}）");

        if (touched)
        {
            instances.Save();
            Console.WriteLine("  → instances.json 已落盘（写入 configuration_name）");
        }

        Print(store);

        Console.WriteLine("  [迁移后] 实例清单：");
        foreach (InstanceDefinition instance in instances.Instances)
        {
            Console.WriteLine($"    · {instance.Name}  configuration_name=\"{instance.ConfigurationName}\"  task_entries={instance.TaskEntries.Count}");
        }

        // 复算一次：重新加载应看到同样的结果（幂等）
        bool again = store.Load(instances.Instances);
        Console.WriteLine($"  [幂等] 二次迁移改写的实例数：{(again ? "有（异常）" : "0（正常）")}");
        return again ? 5 : 0;
    }

    /// <summary>按出现顺序收集 --key value 形式的操作参数。</summary>
    private static List<(string Key, string? Value)> ReadOperations(string[] args)
    {
        string[] valueTaking = ["--path", "--migrate"];
        string[] operations = ["--add", "--task", "--set", "--rename", "--remove", "--import", "--export", "--list"];

        var result = new List<(string, string?)>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (valueTaking.Contains(arg) && i + 1 < args.Length)
            {
                i++;
                continue;
            }

            if (!operations.Contains(arg))
            {
                continue;
            }

            // --list 不带值；下一个参数若也是开关，则当前操作没有值
            result.Add((arg, arg == "--list" || i + 1 >= args.Length || args[i + 1].StartsWith("--")
                ? null
                : args[++i]));
        }

        return result;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
