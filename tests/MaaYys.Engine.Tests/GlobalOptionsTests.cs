using System.Text.Json;
using System.Text.Json.Serialization;
using MaaYys.Engine.Config;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// P5.2 全局选项层（MXU setting 扩展）的读取 / 校验 / 编译。
/// 锁住的语义：全局选项来自 setting 段 + global_option；编译时先合并全局层、再用任务级覆盖同名节点字段；
/// 全局选项缺失引用会在 <see cref="InterfaceConfig.Load"/> 报硬错误；配置组能持久化 global_options。
/// </summary>
public sealed class GlobalOptionsTests : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _root;
    private readonly InterfaceConfig _config;

    public GlobalOptionsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maayys-global-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "interface.json"), InterfaceJson);
        _config = InterfaceConfig.Load(Path.Combine(_root, "interface.json"), []);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响结论
        }
    }

    /// <summary>任务选项（目标）与全局选项（全局_目标）都会写同一个节点 T.expected，用来验证覆盖优先级。</summary>
    private const string InterfaceJson = """
    {
      "interface_version": 2,
      "name": "global-test",
      "controller": [],
      "resource": [],
      "task": [
        { "name": "测试任务", "entry": "入口", "option": ["目标"] }
      ],
      "option": {
        "目标": {
          "type": "select", "label": "任务目标", "default_case": "A",
          "cases": [
            { "name": "A", "label": "A", "pipeline_override": { "T": { "expected": "A" } } },
            { "name": "B", "label": "B", "pipeline_override": { "T": { "expected": "B" } } }
          ]
        },
        "全局_目标": {
          "type": "select", "label": "全局目标", "default_case": "X",
          "cases": [
            { "name": "X", "label": "X", "pipeline_override": { "T": { "expected": "X" } } },
            { "name": "Y", "label": "Y", "pipeline_override": { "T": { "expected": "Y" } } }
          ]
        },
        "全局_重试": {
          "type": "input", "label": "重试次数",
          "inputs": [ { "name": "n", "label": "次数", "default": "1", "pipeline_type": "int" } ],
          "pipeline_override": { "GR": { "retry": "{n}" } }
        }
      },
      "setting": [
        { "name": "通用", "label": "通用设置", "option": ["全局_目标", "全局_重试"] }
      ],
      "global_option": ["全局_目标"]
    }
    """;

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private TaskConfig Task(string name = "测试任务")
        => _config.MergedTasks.Single(t => t.Name == name);

    // ---- 全局选项键 ----

    [Fact]
    public void GlobalOptionKeys_UnionsSettingAndGlobalOptionWithoutDuplicates()
    {
        IReadOnlyList<string> keys = _config.GlobalOptionKeys();

        // setting 段贡献 全局_目标、全局_重试；global_option 再贡献 全局_目标（去重）
        Assert.Contains("全局_目标", keys);
        Assert.Contains("全局_重试", keys);
        Assert.Equal(2, keys.Count);
        // setting 段在前，所以 全局_目标 排在 全局_重试 之前
        List<string> keysList = [.. keys];
        Assert.True(keysList.IndexOf("全局_目标") < keysList.IndexOf("全局_重试"));
    }

    // ---- 编译：全局层 + 任务级覆盖 ----

    [Fact]
    public void Compile_AppliesNonConflictingGlobalLayer()
    {
        // 全局层先合并：全局_重试 写入节点 GR（不与任务级冲突），因此一定出现
        string json = TaskOptions.Compile(
            _config, Task(), null, Values(("全局_重试/n", "5"), ("全局_目标", "Y")), out IReadOnlyList<string> warnings);

        Assert.Empty(warnings);
        Assert.Contains("\"retry\":5", json);           // 来自全局_重试（全局层生效）
        Assert.Contains("\"expected\":\"A\"", json);    // 任务级默认 A 覆盖了冲突的全局_目标=Y
    }

    [Fact]
    public void Compile_TaskLevelOverridesGlobalOnConflict()
    {
        // 全局=Y，任务=目标=A → 同名节点 T.expected 应为任务级 A
        string json = TaskOptions.Compile(
            _config, Task(),
            Values(("目标", "A")),
            Values(("全局_目标", "Y")),
            out _);

        Assert.Contains("\"expected\":\"A\"", json);
        Assert.DoesNotContain("\"expected\":\"Y\"", json);
    }

    [Fact]
    public void Compile_TaskDefaultStillWinsOverGlobal()
    {
        // 任务不显式设置（走默认 A），全局=Y → 任务默认值 A 仍覆盖全局（任务级永远优先）
        string json = TaskOptions.Compile(
            _config, Task(), null, Values(("全局_目标", "Y")), out _);

        Assert.Contains("\"expected\":\"A\"", json);
    }

    [Fact]
    public void Compile_BackwardCompatibleThreeArgStillWorks()
    {
        // 旧的 3 参 Compile 不应因新增全局层而报错，且行为与仅任务级一致
        string json = TaskOptions.Compile(_config, Task(), Values(("目标", "B")), out IReadOnlyList<string> warnings);

        Assert.Empty(warnings);
        Assert.Contains("\"expected\":\"B\"", json);
    }

    // ---- 全局仅编译 ----

    [Fact]
    public void CompileGlobal_EmitsOnlyGlobalContribution()
    {
        string json = TaskOptions.CompileGlobal(
            _config, Values(("全局_目标", "Y"), ("全局_重试/n", "5")), out IReadOnlyList<string> warnings);

        Assert.Empty(warnings);
        Assert.Contains("\"expected\":\"Y\"", json);   // 全局_目标
        Assert.Contains("\"retry\":5", json);           // 全局_重试（int 注入）
        Assert.DoesNotContain("\"expected\":\"A\"", json);  // 任务级不出现
    }

    // ---- 描述（显式键列表）----

    [Fact]
    public void Describe_ExplicitKeys_RendersEachOption()
    {
        IReadOnlyList<TaskOptionDefinition> rows = TaskOptions.Describe(
            _config, new[] { "全局_目标", "全局_重试" });

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Key == "全局_目标" && r.IsSelect);
        Assert.Contains(rows, r => r.Key == "全局_重试" && r.IsInput);
    }

    // ---- 校验 ----

    [Fact]
    public void Load_ReportsErrorWhenSettingReferencesMissingOption()
    {
        string bad = InterfaceJson.Replace("\"option\": [\"全局_目标\", \"全局_重试\"]", "\"option\": [\"不存在的选项\"]");
        string dir = Path.Combine(_root, "bad");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "interface.json"), bad);

        List<string> errors = [];
        InterfaceConfig.Load(Path.Combine(dir, "interface.json"), errors);

        Assert.Contains(errors, e => e.Contains("setting") && e.Contains("不存在"));
    }

    // ---- 持久化 ----

    [Fact]
    public void TaskConfiguration_GlobalOptionsRoundTrips()
    {
        var group = new TaskConfiguration { Name = "默认配置" };
        group.GlobalOptions["全局_目标"] = "Y";
        group.GlobalOptions["全局_重试/n"] = "5";
        group.AddInstance("测试任务");

        string json = JsonSerializer.Serialize(group, SerializerOptions);
        TaskConfiguration? restored = JsonSerializer.Deserialize<TaskConfiguration>(json, SerializerOptions);

        Assert.NotNull(restored);
        Assert.Equal("Y", restored!.GlobalOptions["全局_目标"]);
        Assert.Equal("5", restored.GlobalOptions["全局_重试/n"]);
        Assert.Single(restored.Tasks);
    }
}
