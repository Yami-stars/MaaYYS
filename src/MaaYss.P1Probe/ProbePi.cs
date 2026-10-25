using System.Text.Json;
using MaaYss.Engine.Config;

namespace MaaYss.P1Probe;

/// <summary>
/// P1.3 PI V2 协议解析验证：以 MaaYYs 的 interface.json 为测试对象，
/// 解析 controller / resource / import，验证多 bundle 覆盖语义与任务入口合并。
/// </summary>
internal static class ProbePi
{
    private const string DefaultInterfacePath =
        @"D:\YYS\MaaYYs-win32-win-x86_64-v3.13.6-2608192116-MXU\interface.json";

    public static async Task<int> RunAsync(string? pathArg)
    {
        var path = string.IsNullOrWhiteSpace(pathArg) ? DefaultInterfacePath : pathArg;
        Console.WriteLine("=== P1.3 PI V2 协议解析验证 ===");
        Console.WriteLine($"[0] 目标 interface.json: {path}");

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"文件不存在: {path}");
            return 1;
        }

        var errors = new List<string>();
        var config = InterfaceConfig.Load(path, errors);

        // [1] 项目信息
        Console.WriteLine($"[1] 项目信息: {config.Name} {config.Version} (interface_version={config.InterfaceVersion})");
        if (!string.IsNullOrEmpty(config.Label))
        {
            Console.WriteLine($"    label: {config.Label} | github: {config.Github}");
        }

        // [2] 控制器配置
        Console.WriteLine($"[2] 控制器配置 ({config.Controllers.Count}):");
        foreach (var c in config.Controllers)
        {
            var extra = c.Type switch
            {
                "Win32" when c.Win32 is not null =>
                    $"class={c.Win32.ClassRegex} window={c.Win32.WindowRegex} " +
                    $"mouse={c.Win32.Mouse} keyboard={c.Win32.Keyboard} screencap={c.Win32.Screencap}",
                "PlayCover" when c.PlayCover is not null => $"uuid={c.PlayCover.Uuid}",
                _ => string.Empty,
            };
            Console.WriteLine($"    - {c.Name} ({c.Type}){(string.IsNullOrEmpty(extra) ? "" : $" | {extra}")}");
            if (c.DisplayShortSide is { } side)
            {
                Console.WriteLine($"      display_short_side={side}");
            }
        }

        // [3] 资源配置（含多路径覆盖链）
        Console.WriteLine($"[3] 资源配置 ({config.Resources.Count}) —— 验收项 1:");
        foreach (var r in config.Resources)
        {
            var chain = string.Join(" <- ", r.Path.Select(p => Path.GetFileName(p.TrimEnd('/', '\\'))));
            Console.WriteLine($"    - [{r.Name}] {r.DisplayName}");
            Console.WriteLine($"      加载链: {chain}（后者覆盖前者）");
            foreach (var p in r.Path)
            {
                var full = Path.GetFullPath(Path.Combine(config.BaseDirectory, p));
                var exists = Directory.Exists(full);
                if (!exists)
                {
                    Console.WriteLine($"      [缺失] {p}");
                }
            }
        }

        // [4] 任务分组
        Console.WriteLine($"[4] 任务分组 ({config.MergedGroups.Count}): " +
                          string.Join(" / ", config.MergedGroups.Select(g => $"{g.Label ?? g.Name}({g.Name})")));

        // [5] Agent 与预任务
        if (config.Agents.Count > 0)
        {
            Console.WriteLine($"[5] Agent ({config.Agents.Count}): " +
                              string.Join(", ", config.Agents.Select(a =>
                                  $"{a.ChildExec}{(a.ChildArgs is { Count: > 0 } args ? " " + string.Join(" ", args) : "")}")));
        }

        if (config.MergedPretasks.Count > 0)
        {
            Console.WriteLine($"    预任务 ({config.MergedPretasks.Count}): " +
                              string.Join(", ", config.MergedPretasks.Select(p => p.Exec)));
        }

        // [6] import 合并结果
        Console.WriteLine($"[6] Import 合并: {config.ImportedFiles.Count}/{config.Import?.Count ?? 0} 个文件解析成功");
        Console.WriteLine($"    任务入口合计: {config.MergedTasks.Count}（顶层 {config.Tasks.Count} + import 追加）—— 验收项 2");
        Console.WriteLine($"    option 池合计: {config.MergedOptions.Count}（同名先到先得，顶层优先）");
        Console.WriteLine($"    preset 合计: {config.MergedPresets.Count}");

        // 按来源文件统计
        var bySource = config.MergedTasks
            .GroupBy(t => t.SourceFile ?? "(interface.json 顶层)")
            .OrderByDescending(g => g.Count())
            .ToList();
        foreach (var g in bySource)
        {
            Console.WriteLine($"      {g.Key}: {g.Count()} 个任务");
        }

        // 孤儿任务文件（目录中存在但未被 import 引用）
        var tasksDir = Path.Combine(config.BaseDirectory, "tasks");
        if (Directory.Exists(tasksDir))
        {
            var referenced = new HashSet<string>(
                (config.Import ?? []).Select(i => i.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            var onDisk = Directory.GetFiles(tasksDir, "*.json")
                .Select(p => "tasks/" + Path.GetFileName(p))
                .Where(p => !referenced.Contains(p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (onDisk.Count > 0)
            {
                Console.WriteLine($"    [注意] 未被 import 引用的任务文件 ({onDisk.Count}): {string.Join(", ", onDisk)}");
            }
        }

        // 孤儿资源目录（resource_pack 中存在但无 resource 条目引用）
        var packDir = Path.Combine(config.BaseDirectory, "resource_pack");
        if (Directory.Exists(packDir))
        {
            var referencedDirs = new HashSet<string>(
                config.Resources.SelectMany(r => r.Path)
                    .Select(p => Path.GetFileName(p.TrimEnd('/', '\\'))), StringComparer.OrdinalIgnoreCase);
            var orphans = Directory.GetDirectories(packDir)
                .Select(d => Path.GetFileName(d)!)
                .Where(d => !referencedDirs.Contains(d))
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();
            if (orphans.Count > 0)
            {
                Console.WriteLine($"    [注意] 未被任何资源包引用的目录 ({orphans.Count}): {string.Join(", ", orphans)}");
            }
        }

        // [7] 交叉校验
        Console.WriteLine("[7] 交叉校验:");
        var warnings = new List<string>();

        // 7.1 任务 -> option 引用（官方硬校验已在 Load 中执行并计入 errors）
        var optTypeStat = config.MergedTasks
            .SelectMany(t => t.Option ?? [])
            .Select(o => config.MergedOptions.TryGetValue(o, out var oc) ? oc.Type : "?")
            .GroupBy(t => t)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}x{g.Count()}");
        Console.WriteLine($"    option 引用数: {config.MergedTasks.SelectMany(t => t.Option ?? []).Count()} " +
                          $"(按类型: {string.Join(", ", optTypeStat)})");

        // 7.2 任务 -> group 引用
        var groupNames = new HashSet<string>(config.MergedGroups.Select(g => g.Name), StringComparer.Ordinal);
        var badGroupRefs = config.MergedTasks
            .SelectMany(t => t.Group ?? [])
            .Where(g => !groupNames.Contains(g))
            .Distinct()
            .ToList();
        if (badGroupRefs.Count > 0)
        {
            warnings.Add($"任务引用了未定义的 group: {string.Join(", ", badGroupRefs)}");
        }

        // 7.3 任务 -> resource / controller 引用
        var resourceNames = new HashSet<string>(config.Resources.Select(r => r.Name), StringComparer.Ordinal);
        var badResRefs = config.MergedTasks
            .SelectMany(t => t.Resource ?? [])
            .Where(r => !resourceNames.Contains(r))
            .Distinct()
            .ToList();
        if (badResRefs.Count > 0)
        {
            warnings.Add($"任务引用了未定义的 resource: {string.Join(", ", badResRefs)}");
        }

        var controllerNames = new HashSet<string>(config.Controllers.Select(c => c.Name), StringComparer.Ordinal);
        var badCtrlRefs = config.MergedTasks
            .SelectMany(t => t.Controller ?? [])
            .Where(c => !controllerNames.Contains(c))
            .Distinct()
            .ToList();
        if (badCtrlRefs.Count > 0)
        {
            warnings.Add($"任务引用了未定义的 controller: {string.Join(", ", badCtrlRefs)}");
        }

        // 7.4 资源包 -> controller 引用
        var badResCtrl = config.Resources
            .SelectMany(r => r.Controller ?? [])
            .Where(c => !controllerNames.Contains(c))
            .Distinct()
            .ToList();
        if (badResCtrl.Count > 0)
        {
            warnings.Add($"资源包引用了未定义的 controller: {string.Join(", ", badResCtrl)}");
        }

        // 7.5 option 类型合法性
        var unknownOptTypes = config.MergedOptions.Values
            .Where(o => !o.IsKnownType)
            .Select(o => $"{o.Key}({o.Type})")
            .ToList();
        if (unknownOptTypes.Count > 0)
        {
            warnings.Add($"未知 option 类型: {string.Join(", ", unknownOptTypes)}");
        }

        // 7.6 i18n 字符串统计（$ 开头）
        var i18nCount = CountI18n(config);
        if (i18nCount > 0)
        {
            Console.WriteLine($"    i18n 字符串（$ 开头）: {i18nCount}");
        }

        // 7.7 资源路径磁盘存在性
        var missingPaths = config.Resources
            .SelectMany(r => r.Path.Select(p => (Name: r.Name, Path: p)))
            .Where(x => !Directory.Exists(Path.GetFullPath(Path.Combine(config.BaseDirectory, x.Path))))
            .ToList();
        if (missingPaths.Count > 0)
        {
            warnings.Add($"资源路径不存在: {string.Join(", ", missingPaths.Select(x => $"{x.Name}/{x.Path}"))}");
        }

        if (warnings.Count == 0)
        {
            Console.WriteLine("    group / resource / controller 引用、option 类型、资源路径: 全部有效");
        }
        else
        {
            foreach (var w in warnings)
            {
                Console.WriteLine($"    [警告] {w}");
            }
        }

        // [8] 任务清单（按分组）
        Console.WriteLine("[8] 任务清单（按分组）:");
        foreach (var (groupName, tasks) in config.TasksByGroup())
        {
            var label = groupName.Length == 0
                ? "未分组"
                : config.FindGroup(groupName)?.DisplayName ?? groupName;
            Console.WriteLine($"    [{label}] ({tasks.Count})");
            foreach (var t in tasks.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var optCount = t.Option?.Count ?? 0;
                var src = t.SourceFile is { } s ? Path.GetFileName(s) : "顶层";
                Console.WriteLine($"      - {t.Name} (entry={t.Entry}, options={optCount}, 来源={src})");
            }
        }

        // [9] 验收
        // 说明：流程表原写「32 个任务入口」为早期按 tasks/ 目录文件数的误计数。
        // 按 PI V2 官方语义（MaaPiCli Parser.cpp 实证），只有 interface.json 的 import[]
        // 引用的文件才构成任务入口；目录中另有 3 个未被引用的孤儿文件，由探针单独识别报告。
        Console.WriteLine("[9] 验收（官方 PI V2 语义: 8 个渠道服资源 + 29 个 import 任务入口）:");
        var resOk = config.Resources.Count == 8;
        var taskOk = config.MergedTasks.Count == 29;
        Console.WriteLine($"    资源 bundle 数量: {config.Resources.Count} {(resOk ? "✓" : "✗（预期 8）")}");
        Console.WriteLine($"    import 任务入口数量: {config.MergedTasks.Count} {(taskOk ? "✓" : "✗（预期 29）")}");

        if (errors.Count > 0)
        {
            Console.WriteLine($"[!] 硬校验错误 ({errors.Count}):");
            foreach (var e in errors)
            {
                Console.WriteLine($"    [错误] {e}");
            }
        }

        if (errors.Count > 0 || !resOk || !taskOk)
        {
            Console.WriteLine("=== P1.3 验证失败 ===");
            return 1;
        }

        Console.WriteLine("=== P1.3 验证通过 ===");
        await Task.CompletedTask;
        return 0;
    }

    /// <summary>递归统计 $ 开头的 i18n 字符串（Schema 的 i18nString 约定）。</summary>
    private static int CountI18n(InterfaceConfig config)
    {
        // 重新读取原始 JSON 做全量扫描（模型层已丢失部分原始结构）
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(config.BaseDirectory, "interface.json")));
            return CountI18nElement(doc.RootElement);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static int CountI18nElement(JsonElement element)
    {
        var count = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (element.GetString()?.StartsWith('$') == true)
                {
                    count++;
                }

                break;
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    count += CountI18nElement(prop.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    count += CountI18nElement(item);
                }

                break;
        }

        return count;
    }
}
