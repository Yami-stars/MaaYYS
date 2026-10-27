using System.IO;
using MaaYys.Engine.Config;
using MaaYys.Engine.Execution;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// P5.3 执行计划编译：队列项 → (entry, pipeline_override) 的映射契约。
/// 用真实 resource/interface.json 验证，避免「界面预览对了、投递时传错」这类问题。
/// </summary>
public sealed class ExecutionPlanTests
{
    private static InterfaceConfig LoadRealConfig()
    {
        string path = Path.Combine(TestPaths.RepoRoot, "resource", "interface.json");
        Assert.True(File.Exists(path), $"缺少清单：{path}");

        List<string> errors = [];
        InterfaceConfig config = InterfaceConfig.Load(path, errors);
        Assert.Empty(errors);
        return config;
    }

    [Fact]
    public void BuildItem_MapsQueueFieldsAndOverride()
    {
        InterfaceConfig config = LoadRealConfig();
        TaskConfig definition = config.MergedTasks[0];

        TaskSelection selection = new() { Name = definition.Name, Id = "id-a" };

        ExecutionItem item = ExecutionPlan.BuildItem(config, selection);

        Assert.Equal("id-a", item.Id);
        Assert.Equal(definition.Name, item.Entry);
        Assert.Equal(definition.Name, item.DisplayName);
        Assert.True(item.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(item.PipelineOverride));
    }

    [Fact]
    public void BuildItem_UsesAliasAndEnabledFlag()
    {
        InterfaceConfig config = LoadRealConfig();
        TaskSelection selection = new()
        {
            Name = config.MergedTasks[0].Name,
            Id = "id-b",
            Alias = "我的御魂",
            Enabled = false,
        };

        ExecutionItem item = ExecutionPlan.BuildItem(config, selection);

        Assert.Equal("我的御魂", item.DisplayName);
        Assert.False(item.Enabled);
    }

    [Fact]
    public void BuildItem_UnknownTaskKeepsNullOverride()
    {
        InterfaceConfig config = LoadRealConfig();
        TaskSelection selection = new() { Name = "_不存在的任务_", Id = "id-c" };

        ExecutionItem item = ExecutionPlan.BuildItem(config, selection);

        Assert.Null(item.PipelineOverride);
        Assert.Equal("_不存在的任务_", item.Entry);
    }

    [Fact]
    public void Build_PreservesQueueOrder()
    {
        InterfaceConfig config = LoadRealConfig();
        TaskSelection[] queue =
        [
            new TaskSelection { Name = config.MergedTasks[0].Name, Id = "1" },
            new TaskSelection { Name = config.MergedTasks[1].Name, Id = "2" },
            new TaskSelection { Name = config.MergedTasks[0].Name, Id = "3" },   // 同类多实例
        ];

        List<ExecutionItem> items = ExecutionPlan.Build(config, queue);

        Assert.Equal(new[] { "1", "2", "3" }, items.Select(i => i.Id));
        Assert.Equal(2, items.Count(i => i.Entry == config.MergedTasks[0].Name));
    }

    [Fact]
    public void BuildItem_AppliesGlobalOptionsLayer()
    {
        InterfaceConfig config = LoadRealConfig();

        // 找一个带 select 选项的任务，验证全局层与任务层同时进入 override
        TaskConfig? withOption = config.MergedTasks.FirstOrDefault(t => (t.Option?.Count ?? 0) > 0);
        Assert.NotNull(withOption);

        TaskSelection selection = new() { Name = withOption!.Name, Id = "g" };
        ExecutionItem withoutGlobal = ExecutionPlan.BuildItem(config, selection);
        ExecutionItem withGlobal = ExecutionPlan.BuildItem(
            config,
            selection,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["全局_调试模式"] = "开",
                ["全局_点击间隔/delay"] = "1234",
            });

        Assert.NotNull(withoutGlobal.PipelineOverride);
        Assert.NotNull(withGlobal.PipelineOverride);

        // 全局层真的进了 override：调试模式节点 + 点击间隔取值
        Assert.Contains("MaaYys_Global_Debug", withGlobal.PipelineOverride);
        Assert.Contains("1234", withGlobal.PipelineOverride);
        Assert.DoesNotContain("MaaYys_Global_Debug", withoutGlobal.PipelineOverride!);
    }
}
