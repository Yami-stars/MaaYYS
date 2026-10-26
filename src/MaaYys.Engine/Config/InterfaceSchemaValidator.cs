using MaaYys.Engine.Resources;

namespace MaaYys.Engine.Config;

/// <summary>PI 诊断级别：Error 表示协议上不合法，Warning 表示能跑但值得看一眼，Info 只是统计。</summary>
public enum PiDiagnosticLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>一条 PI 校验结论。</summary>
public sealed record PiDiagnostic(PiDiagnosticLevel Level, string Path, string Message)
{
    public override string ToString() => $"[{Level}] {Path}：{Message}";
}

/// <summary>interface.json 的 PI V2 校验结果。</summary>
public sealed class InterfaceSchemaReport
{
    /// <summary>被校验的清单路径。</summary>
    public required string InterfacePath { get; init; }

    public IReadOnlyList<PiDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>协议合法性：没有 Error 级结论即为通过。</summary>
    public bool IsValid => !Diagnostics.Any(d => d.Level == PiDiagnosticLevel.Error);

    public int ErrorCount => Diagnostics.Count(d => d.Level == PiDiagnosticLevel.Error);

    public int WarningCount => Diagnostics.Count(d => d.Level == PiDiagnosticLevel.Warning);

    /// <summary>`InterfaceConfig.Load` 自己报的硬错误（import 缺失、option 引用悬空等）。</summary>
    public IReadOnlyList<string> LoadErrors { get; init; } = [];

    public string Summary => IsValid
        ? $"✓ 通过 PI V2 校验（错误 0 · 警告 {WarningCount}）"
        : $"✗ 未通过 PI V2 校验（错误 {ErrorCount} · 警告 {WarningCount}）";
}

/// <summary>
/// PI V2 <c>interface.json</c> 校验器（P3.6）。
/// <para>
/// 与 <see cref="InterfaceConfig.Load"/> 的分工：Load 只做「不通过就没法继续解析」的 4 项硬校验
/// （interface_version、controller 类型、import 存在性、option 引用），其余全部交给这里，
/// 这样校验可以<b>单独跑</b>（<c>probe pi --validate</c>）而不必先建会话。
/// </para>
/// <para>
/// 判定口径对齐官方 MaaPiCli Parser：必填字段、类型合法性、跨段引用完整性、磁盘路径存在性。
/// Win32 控制器的键鼠/截图方式额外按 <see cref="MaaWin32MethodNames"/> 的已知名校验——
/// 写错名字在 MaaFramework 里是静默回退（不是报错），最容易悄悄改变行为。
/// </para>
/// </summary>
public static class InterfaceSchemaValidator
{
    public static InterfaceSchemaReport ValidateFile(string interfaceJsonPath)
    {
        List<string> loadErrors = [];
        InterfaceConfig config = InterfaceConfig.Load(interfaceJsonPath, loadErrors);
        return Validate(config, loadErrors);
    }

    public static InterfaceSchemaReport Validate(InterfaceConfig config, IReadOnlyList<string>? loadErrors = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        string root = config.BaseDirectory;
        var diagnostics = new List<PiDiagnostic>();

        void Add(PiDiagnosticLevel level, string path, string message)
            => diagnostics.Add(new PiDiagnostic(level, path, message));

        // ---- 顶层必填 ----
        if (config.InterfaceVersion != 2)
        {
            Add(PiDiagnosticLevel.Error, "interface_version", $"必须为 2，实际为 {config.InterfaceVersion}");
        }

        if (string.IsNullOrWhiteSpace(config.Name))
        {
            Add(PiDiagnosticLevel.Error, "name", "必填");
        }

        if (string.IsNullOrWhiteSpace(config.Version))
        {
            Add(PiDiagnosticLevel.Warning, "version", "未填写，资源版本更新整理无法判断是否需要重跑");
        }

        // ---- controller ----
        if (config.Controllers.Count == 0)
        {
            Add(PiDiagnosticLevel.Error, "controller", "至少一个控制器");
        }

        HashSet<string> controllerNames = new(StringComparer.Ordinal);
        bool hasWin32 = false;
        foreach (ControllerConfig controller in config.Controllers)
        {
            string path = $"controller[{controller.Name}]";
            if (string.IsNullOrWhiteSpace(controller.Name))
            {
                Add(PiDiagnosticLevel.Error, path, "name 必填");
            }
            else if (!controllerNames.Add(controller.Name))
            {
                Add(PiDiagnosticLevel.Error, path, "name 重复");
            }

            if (!controller.IsKnownType)
            {
                Add(PiDiagnosticLevel.Error, $"{path}.type",
                    $"类型 \"{controller.Type}\" 非法（合法值：{string.Join("/", ControllerConfig.KnownTypes)}）");
            }

            switch (controller.Type)
            {
                case "Win32":
                    hasWin32 = true;
                    ValidateWin32(controller, path, Add);
                    break;

                case "PlayCover" when controller.PlayCover is null
                                      || string.IsNullOrWhiteSpace(controller.PlayCover.Uuid):
                    Add(PiDiagnosticLevel.Error, $"{path}.playcover.uuid", "PlayCover 控制器必填");
                    break;

                case "Adb" when controller.Adb is null:
                    Add(PiDiagnosticLevel.Warning, $"{path}.adb", "未声明 adb 段（用框架默认能力探测）");
                    break;
            }

            if (controller.AttachResourcePath is { Count: > 0 } attach)
            {
                foreach (string relative in attach)
                {
                    if (!Directory.Exists(Path.GetFullPath(Path.Combine(root, relative))))
                    {
                        Add(PiDiagnosticLevel.Error, $"{path}.attach_resource_path", $"目录不存在：{relative}");
                    }
                }
            }
        }

        if (!hasWin32)
        {
            Add(PiDiagnosticLevel.Warning, "controller", "没有 Win32 控制器，桌面端无法连接");
        }

        // ---- resource ----
        if (config.Resources.Count == 0)
        {
            Add(PiDiagnosticLevel.Error, "resource", "至少一个资源集");
        }

        HashSet<string> resourceNames = new(StringComparer.Ordinal);
        foreach (ResourceConfig resource in config.Resources)
        {
            string path = $"resource[{resource.Name}]";
            if (!resourceNames.Add(resource.Name))
            {
                Add(PiDiagnosticLevel.Error, path, "name 重复");
            }

            if (resource.Path.Count == 0)
            {
                Add(PiDiagnosticLevel.Error, $"{path}.path", "至少一个 bundle 路径");
                continue;
            }

            foreach (string relative in resource.Path)
            {
                if (!Directory.Exists(Path.GetFullPath(Path.Combine(root, relative))))
                {
                    Add(PiDiagnosticLevel.Error, $"{path}.path", $"目录不存在：{relative}");
                }
            }

            // P3.5 契约：第一个 bundle 是 base，后面才是渠道层
            string first = Path.GetFileName(resource.Path[0].TrimEnd('/', '\\'));
            if (resource.Path.Count > 1 && !string.Equals(first, "base", StringComparison.OrdinalIgnoreCase))
            {
                Add(PiDiagnosticLevel.Warning, $"{path}.path",
                    $"第一个 bundle 是「{first}」而不是 base（渠道分层约定第一个是完整定义）");
            }

            foreach (string name in resource.Controller ?? [])
            {
                if (!controllerNames.Contains(name))
                {
                    Add(PiDiagnosticLevel.Error, $"{path}.controller", $"引用了不存在的控制器：{name}");
                }
            }
        }

        // ---- group ----
        HashSet<string> groupNames = new(StringComparer.Ordinal);
        foreach (GroupConfig group in config.MergedGroups)
        {
            if (!groupNames.Add(group.Name))
            {
                Add(PiDiagnosticLevel.Warning, $"group[{group.Name}]", "name 重复（合并时后者被忽略）");
            }
        }

        // ---- task ----
        if (config.MergedTasks.Count == 0)
        {
            Add(PiDiagnosticLevel.Error, "task/import", "合并后没有任何任务");
        }

        HashSet<string> taskNames = new(StringComparer.Ordinal);
        HashSet<string> usedGroups = new(StringComparer.Ordinal);
        foreach (TaskConfig task in config.MergedTasks)
        {
            string path = $"task[{task.Name}]{(task.SourceFile is null ? string.Empty : $"@{task.SourceFile}")}";
            if (string.IsNullOrWhiteSpace(task.Name))
            {
                Add(PiDiagnosticLevel.Error, path, "name 必填");
            }
            else if (!taskNames.Add(task.Name))
            {
                Add(PiDiagnosticLevel.Warning, path, "name 重复（同名任务在界面上会打架）");
            }

            if (string.IsNullOrWhiteSpace(task.Entry))
            {
                Add(PiDiagnosticLevel.Error, path, "entry 必填（投递任务时用的 pipeline 入口）");
            }

            foreach (string name in task.Group ?? [])
            {
                usedGroups.Add(name);
                if (!groupNames.Contains(name))
                {
                    Add(PiDiagnosticLevel.Error, $"{path}.group", $"引用了不存在的分组：{name}");
                }
            }

            foreach (string name in task.Controller ?? [])
            {
                if (!controllerNames.Contains(name))
                {
                    Add(PiDiagnosticLevel.Error, $"{path}.controller", $"引用了不存在的控制器：{name}");
                }
            }

            foreach (string name in task.Resource ?? [])
            {
                if (!resourceNames.Contains(name))
                {
                    Add(PiDiagnosticLevel.Error, $"{path}.resource", $"引用了不存在的资源集：{name}");
                }
            }
        }

        foreach (string name in groupNames.Where(g => !usedGroups.Contains(g)))
        {
            Add(PiDiagnosticLevel.Warning, $"group[{name}]", "没有任何任务使用这个分组");
        }

        // ---- option ----
        foreach ((string key, OptionConfig option) in config.MergedOptions)
        {
            string path = $"option[{key}]";
            if (!option.IsKnownType)
            {
                Add(PiDiagnosticLevel.Error, $"{path}.type",
                    $"类型 \"{option.Type}\" 非法（合法值：{string.Join("/", OptionConfig.KnownTypes)}）");
                continue;
            }

            bool needsCases = option.Type is "select" or "switch" or "checkbox";
            if (needsCases && option.Cases is not { Count: > 0 })
            {
                Add(PiDiagnosticLevel.Error, $"{path}.cases", $"{option.Type} 至少要有一个 case");
                continue;
            }

            if (option.Type == "input" && option.Inputs is not { Count: > 0 })
            {
                Add(PiDiagnosticLevel.Error, $"{path}.inputs", "input 至少要有一个输入字段");
            }

            if (option.Cases is { Count: > 0 } cases)
            {
                HashSet<string> caseNames = new(StringComparer.Ordinal);
                foreach (CaseConfig item in cases)
                {
                    if (!caseNames.Add(item.Name))
                    {
                        Add(PiDiagnosticLevel.Warning, $"{path}.cases", $"case name 重复：{item.Name}");
                    }
                }

                // default_case 指向不存在的 case：界面会渲染出一个永远选不到的默认值
                if (option.DefaultCaseString is { } defaultCase && !caseNames.Contains(defaultCase))
                {
                    Add(PiDiagnosticLevel.Error, $"{path}.default_case", $"指向不存在的 case：{defaultCase}");
                }

                foreach (CaseConfig item in cases)
                {
                    foreach (string sub in item.Option ?? [])
                    {
                        if (!config.MergedOptions.ContainsKey(sub))
                        {
                            Add(PiDiagnosticLevel.Error, $"{path}.cases[{item.Name}].option",
                                $"引用了不存在的 option：{sub}");
                        }
                    }
                }
            }
        }

        // ---- agent ----
        foreach (AgentConfig agent in config.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.ChildExec))
            {
                Add(PiDiagnosticLevel.Error, "agent.child_exec", "必填");
                continue;
            }

            string resolved = ResolveLike(agent.ChildExec, root);
            if (!File.Exists(resolved))
            {
                Add(PiDiagnosticLevel.Warning, "agent.child_exec",
                    $"文件不存在：{agent.ChildExec}（P4 接 agent 前可以先不准备）");
            }
        }

        // ---- 引用的外部文件 ----
        CheckReferencedFile(config.Icon, "icon", root, PiDiagnosticLevel.Warning, Add);
        CheckReferencedFile(AsString(config.Welcome), "welcome", root, PiDiagnosticLevel.Warning, Add);
        CheckReferencedFile(config.License, "license", root, PiDiagnosticLevel.Warning, Add);

        foreach (string error in loadErrors ?? [])
        {
            diagnostics.Add(new PiDiagnostic(PiDiagnosticLevel.Error, "load", error));
        }

        // ---- 统计 ----
        diagnostics.Add(new PiDiagnostic(
            PiDiagnosticLevel.Info, "统计",
            $"控制器 {config.Controllers.Count} · 资源集 {config.Resources.Count} · "
            + $"任务 {config.MergedTasks.Count}（顶层 {config.Tasks.Count} + import {config.MergedTasks.Count - config.Tasks.Count}） · "
            + $"选项 {config.MergedOptions.Count} · 分组 {config.MergedGroups.Count} · import {config.ImportedFiles.Count}/{config.Import?.Count ?? 0}"));

        return new InterfaceSchemaReport
        {
            InterfacePath = config.BaseDirectory,
            Diagnostics = [.. diagnostics
                .OrderByDescending(d => d.Level)
                .ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase)],
            LoadErrors = loadErrors ?? [],
        };
    }

    private static void ValidateWin32(
        ControllerConfig controller,
        string path,
        Action<PiDiagnosticLevel, string, string> add)
    {
        if (controller.Win32 is null)
        {
            add(PiDiagnosticLevel.Error, $"{path}.win32", "Win32 控制器必须声明 win32 段");
            return;
        }

        Win32ControllerConfig win32 = controller.Win32;

        // 窗口定位：两个正则都空就找不到窗口
        if (string.IsNullOrWhiteSpace(win32.WindowRegex) && string.IsNullOrWhiteSpace(win32.ClassRegex))
        {
            add(PiDiagnosticLevel.Error, $"{path}.win32", "window_regex 与 class_regex 至少要有一个");
        }

        // 键鼠/截图方式写错名字，MaaFramework 是静默回退而不是报错 —— 必须拦
        if (!string.IsNullOrWhiteSpace(win32.Screencap)
            && !MaaWin32MethodNames.IsKnownScreencap(win32.Screencap))
        {
            add(PiDiagnosticLevel.Error, $"{path}.win32.screencap",
                $"未知的截图方式 \"{win32.Screencap}\"（已知：{string.Join("/", MaaWin32MethodNames.ScreencapNames)}）");
        }

        if (!string.IsNullOrWhiteSpace(win32.Mouse) && !MaaWin32MethodNames.IsKnownInput(win32.Mouse))
        {
            add(PiDiagnosticLevel.Error, $"{path}.win32.mouse",
                $"未知的输入方式 \"{win32.Mouse}\"（已知：{string.Join("/", MaaWin32MethodNames.InputNames)}）");
        }

        if (!string.IsNullOrWhiteSpace(win32.Keyboard) && !MaaWin32MethodNames.IsKnownInput(win32.Keyboard))
        {
            add(PiDiagnosticLevel.Error, $"{path}.win32.keyboard",
                $"未知的输入方式 \"{win32.Keyboard}\"（已知：{string.Join("/", MaaWin32MethodNames.InputNames)}）");
        }

        // P1.4 实测：PrintWindow 在 DPI-unaware 游戏上有黑边 → 点击整体带偏
        if (string.Equals(win32.Screencap, "PrintWindow", StringComparison.Ordinal))
        {
            add(PiDiagnosticLevel.Warning, $"{path}.win32.screencap",
                "PrintWindow 在 DPI 缩放下会带黑边导致点击偏移（P1.4 实测），建议 FramePool");
        }
    }

    private static void CheckReferencedFile(
        string? relative,
        string field,
        string root,
        PiDiagnosticLevel level,
        Action<PiDiagnosticLevel, string, string> add)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return;
        }

        if (!File.Exists(ResolveLike(relative, root)))
        {
            add(level, field, $"引用的文件不存在：{relative}");
        }
    }

    /// <summary>welcome 在 schema 里可以是字符串也可以是对象（多语言），取字符串形态即可。</summary>
    private static string? AsString(System.Text.Json.JsonElement? element)
        => element is { ValueKind: System.Text.Json.JsonValueKind.String } e ? e.GetString() : null;

    /// <summary>
    /// 解析 interface.json 里的相对文件引用：既有 <c>./xxx</c>，也有 <c>/assets/logo.png</c>
    /// 这种「以根开头但其实是资源目录内」的写法（MXU/MAA 都这么写）。
    /// </summary>
    private static string ResolveLike(string relative, string root)
    {
        string trimmed = relative.Replace('\\', '/').TrimStart('/', '.');
        return Path.GetFullPath(Path.Combine(root, trimmed));
    }
}
