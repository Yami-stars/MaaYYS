using MaaYys.Engine.Config;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// 任务选项的读取与编译（P3.4）。
/// 锁住的语义：默认值回落、switch/select 的联动子选项、checkbox 多选、
/// input 的类型转换与 <c>{占位符}</c> 注入、以及「同名节点按字段深合并」。
/// </summary>
public sealed class TaskOptionsTests : IDisposable
{
    private readonly string _root;
    private readonly InterfaceConfig _config;

    public TaskOptionsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maayys-opt-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>一个覆盖四种类型的任务：select（带子选项）、switch、checkbox、input。</summary>
    private const string InterfaceJson = """
    {
      "interface_version": 2,
      "name": "opt-test",
      "controller": [],
      "resource": [],
      "group": [ { "name": "g" } ],
      "task": [
        {
          "name": "测试任务",
          "entry": "入口",
          "group": ["g"],
          "option": ["目标", "开关", "多选", "次数"]
        },
        {
          "name": "无选项任务",
          "entry": "入口2"
        }
      ],
      "option": {
        "目标": {
          "type": "select",
          "label": "挑战目标",
          "default_case": "大蛇",
          "cases": [
            { "name": "大蛇", "label": "大蛇", "pipeline_override": { "点击目标": { "expected": "大蛇" } }, "option": ["层数"] },
            { "name": "业原火", "label": "业原火", "pipeline_override": { "点击目标": { "expected": "业原火" } }, "option": ["层数"] }
          ]
        },
        "层数": {
          "type": "select",
          "label": "层数",
          "default_case": "壹层",
          "cases": [
            { "name": "壹层", "label": "壹层", "pipeline_override": { "点击层数": { "expected": "壹层" } } },
            { "name": "悲鸣", "label": "悲鸣", "pipeline_override": { "点击层数": { "expected": "悲鸣" } } }
          ]
        },
        "开关": {
          "type": "switch",
          "label": "是否切换阵容",
          "default_case": "Yes",
          "cases": [
            { "name": "Yes", "label": "是", "pipeline_override": { "开始装备": { "enabled": true } }, "option": ["阵容"] },
            { "name": "No", "label": "否", "pipeline_override": { "开始装备": { "enabled": false } } }
          ]
        },
        "阵容": {
          "type": "input",
          "label": "阵容预设",
          "inputs": [
            { "name": "group", "label": "分组", "default": "默认分组", "pipeline_type": "string" },
            { "name": "team", "label": "队伍", "default": "默认队伍", "pipeline_type": "string" }
          ],
          "pipeline_override": {
            "装备": { "custom_action_param": { "group_name": "{group}", "team_name": "{team}" } }
          }
        },
        "多选": {
          "type": "checkbox",
          "label": "要买的物品",
          "default_case": ["符咒"],
          "cases": [
            { "name": "符咒", "label": "符咒", "pipeline_override": { "买符咒": { "enabled": true } } },
            { "name": "达摩", "label": "达摩", "pipeline_override": { "买达摩": { "enabled": true } } }
          ]
        },
        "次数": {
          "type": "input",
          "label": "挑战次数",
          "inputs": [
            { "name": "n", "label": "次数", "default": "10", "pipeline_type": "int" }
          ],
          "pipeline_override": {
            "设置次数": { "custom_action_param": { "expected_number": "{n}", "note": "共{n}次" } }
          }
        }
      }
    }
    """;

    private TaskConfig Task(string name = "测试任务")
        => _config.MergedTasks.Single(t => t.Name == name);

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    // ---- 编译 ----

    [Fact]
    public void Compile_UsesDefaultsWhenNothingSelected()
    {
        string json = TaskOptions.Compile(_config, Task(), null, out IReadOnlyList<string> warnings);

        Assert.Empty(warnings);
        Assert.Contains("\"expected\":\"大蛇\"", json);
        Assert.Contains("\"expected\":\"壹层\"", json);       // 子选项的默认值
        Assert.Contains("\"enabled\":true", json);            // switch 默认 Yes
        Assert.Contains("\"买符咒\"", json);                   // checkbox 默认选中「符咒」
        Assert.DoesNotContain("买达摩", json);
    }

    [Fact]
    public void Compile_FollowsSelectedCaseAndItsChildren()
    {
        string json = TaskOptions.Compile(
            _config, Task(), Values(("目标", "业原火"), ("层数", "悲鸣")), out _);

        Assert.Contains("\"expected\":\"业原火\"", json);
        Assert.Contains("\"expected\":\"悲鸣\"", json);
    }

    [Fact]
    public void Compile_DropsChildOptionWhenParentSwitchedOff()
    {
        string json = TaskOptions.Compile(_config, Task(), Values(("开关", "No")), out _);

        Assert.Contains("\"enabled\":false", json);
        Assert.DoesNotContain("group_name", json);   // 只有 Yes 才带出「阵容」输入项

        string again = TaskOptions.Compile(_config, Task(), Values(("开关", "Yes")), out _);
        Assert.Contains("group_name", again);
    }

    [Fact]
    public void Compile_SubstitutesInputWithCorrectType()
    {
        string json = TaskOptions.Compile(
            _config, Task(),
            Values(("开关", "Yes"), ("阵容/group", "斗技组"), ("阵容/team", "一队"), ("次数/n", "42")),
            out _);

        Assert.Contains("\"group_name\":\"斗技组\"", json);
        Assert.Contains("\"team_name\":\"一队\"", json);
        Assert.Contains("\"expected_number\":42", json);   // int 不是字符串
        Assert.Contains("\"note\":\"共42次\"", json);        // 字符串内插
    }

    [Fact]
    public void Compile_ReportsInvalidIntButKeepsGoing()
    {
        TaskOptions.Compile(_config, Task(), Values(("次数/n", "十次")), out IReadOnlyList<string> warnings);

        Assert.Single(warnings);
        Assert.Contains("应为整数", warnings[0]);
    }

    [Fact]
    public void Compile_CheckboxMergesMultipleCases()
    {
        string json = TaskOptions.Compile(_config, Task(), Values(("多选", "符咒,达摩")), out _);

        Assert.Contains("买符咒", json);
        Assert.Contains("买达摩", json);
    }

    [Fact]
    public void Compile_DeepMergesSameNodeFields()
    {
        // 「装备」节点同时来自 option 级 override（带占位符）与（无）case override，字段不能互相覆盖
        string json = TaskOptions.Compile(_config, Task(), Values(("开关", "Yes")), out _);

        Assert.Contains("\"group_name\"", json);
        Assert.Contains("\"team_name\"", json);
    }

    [Fact]
    public void Compile_EmptyTaskReturnsEmptyObject()
    {
        string json = TaskOptions.Compile(_config, Task("无选项任务"), null, out _);

        Assert.Equal("{}", json);
    }

    // ---- 描述（界面渲染依据）----

    [Fact]
    public void Describe_ExpandsOnlySelectedBranches()
    {
        IReadOnlyList<TaskOptionDefinition> rows = TaskOptions.Describe(_config, "测试任务");

        // 目标 + 层数(子) + 开关 + 阵容(子) + 多选 + 次数
        Assert.Equal(6, rows.Count);
        TaskOptionDefinition level = rows.Single(r => r.Key == "层数");
        Assert.Equal(1, level.Level);
        Assert.Equal("目标", level.ParentKey);
        Assert.Equal("大蛇", level.ParentCase);
        Assert.Equal(18, level.Indent);
    }

    [Fact]
    public void Describe_HidesChildOfUnselectedSwitch()
    {
        IReadOnlyList<TaskOptionDefinition> rows = TaskOptions.Describe(
            _config, "测试任务", Values(("开关", "No")));

        Assert.DoesNotContain(rows, r => r.Key == "阵容");
        Assert.Contains(rows, r => r.Key == "开关");
    }

    [Fact]
    public void Describe_ExposesChoicesAndInputs()
    {
        IReadOnlyList<TaskOptionDefinition> rows = TaskOptions.Describe(_config, "测试任务");

        TaskOptionDefinition target = rows.Single(r => r.Key == "目标");
        Assert.Equal(2, target.Choices.Count);
        Assert.Equal("业原火", target.Choices[1].Name);
        Assert.True(target.IsSelect);

        TaskOptionDefinition count = rows.Single(r => r.Key == "次数");
        Assert.True(count.IsInput);
        OptionInputField field = Assert.Single(count.Inputs);
        Assert.Equal("int", field.PipelineType);
        Assert.Equal("10", field.Default);
    }

    [Fact]
    public void Describe_AllImportedTasksRenderSomething()
    {
        // 真实资源侧由 probe tasks 校验；这里只保证「无选项任务」不抛异常且返回空表
        Assert.Empty(TaskOptions.Describe(_config, "无选项任务"));
        Assert.Empty(TaskOptions.Describe(_config, "不存在的任务"));
    }
}
