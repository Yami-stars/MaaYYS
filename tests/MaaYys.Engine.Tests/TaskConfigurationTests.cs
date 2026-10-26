using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using MaaYys.Engine.Config;

namespace MaaYys.Engine.Tests;

/// <summary>P5.1 任务队列模型：多实例 / 排序 / 别名 / 持久化。</summary>
public sealed class TaskConfigurationTests
{
    [Fact]
    public void AddInstance_GeneratesUniqueIds_AndPassesValidation()
    {
        var cfg = new TaskConfiguration();
        TaskSelection a = cfg.AddInstance("御魂");
        TaskSelection b = cfg.AddInstance("御魂");

        Assert.NotEqual(a.Id, b.Id);
        Assert.False(string.IsNullOrEmpty(a.Id));
        Assert.False(string.IsNullOrEmpty(b.Id));
        Assert.Equal("御魂", a.Name);
        Assert.Equal("御魂", b.Name);
        Assert.Empty(cfg.Validate());
    }

    [Fact]
    public void MoveById_ReordersTasks()
    {
        var cfg = new TaskConfiguration();
        TaskSelection a = cfg.AddInstance("A");
        TaskSelection b = cfg.AddInstance("B");
        TaskSelection c = cfg.AddInstance("C");

        cfg.MoveById(a.Id, 2);

        Assert.Equal("B", cfg.Tasks[0].Name);
        Assert.Equal("C", cfg.Tasks[1].Name);
        Assert.Equal("A", cfg.Tasks[2].Name);
    }

    [Fact]
    public void MoveById_ClampsOutOfRange()
    {
        var cfg = new TaskConfiguration();
        TaskSelection a = cfg.AddInstance("A");
        cfg.AddInstance("B");
        cfg.MoveById(a.Id, 99);

        Assert.Equal(1, cfg.Tasks.FindIndex(t => t.Id == a.Id));
    }

    [Fact]
    public void EnsureIds_FillsMissingAndKeepsUnique()
    {
        var cfg = new TaskConfiguration
        {
            Tasks = new()
            {
                new TaskSelection { Name = "A" },
                new TaskSelection { Name = "B", Id = "preset-x" },
                new TaskSelection { Name = "C" },
            },
        };

        cfg.EnsureIds();

        Assert.All(cfg.Tasks, t => Assert.False(string.IsNullOrEmpty(t.Id)));
        HashSet<string> ids = new(StringComparer.Ordinal);
        Assert.All(cfg.Tasks, t => Assert.True(ids.Add(t.Id)));
        Assert.Contains(cfg.Tasks, t => t.Id == "preset-x");
    }

    [Fact]
    public void Alias_AndOptions_RoundTripThroughJson()
    {
        var cfg = new TaskConfiguration();
        TaskSelection e = cfg.AddInstance("御魂", "觉醒材料");
        e.SetOption("count", "5");

        string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        TaskConfiguration? reloaded = JsonSerializer.Deserialize<TaskConfiguration>(json);

        Assert.NotNull(reloaded);
        TaskSelection? got = reloaded!.FindById(e.Id);
        Assert.NotNull(got);
        Assert.Equal("觉醒材料", got!.Alias);
        Assert.Equal("5", got.GetOption("count"));
    }

    [Fact]
    public void Order_PreservedAfterJsonRoundTrip()
    {
        var cfg = new TaskConfiguration();
        cfg.AddInstance("A");
        cfg.AddInstance("B");
        cfg.AddInstance("C");

        string json = JsonSerializer.Serialize(cfg);
        TaskConfiguration? reloaded = JsonSerializer.Deserialize<TaskConfiguration>(json);

        Assert.NotNull(reloaded);
        Assert.Equal(new[] { "A", "B", "C" }, reloaded!.Tasks.Select(t => t.Name));
        Assert.Equal(0, reloaded.Tasks.FindIndex(t => t.Name == "A"));
        Assert.Equal(2, reloaded.Tasks.FindIndex(t => t.Name == "C"));
    }

    [Fact]
    public void Validate_AllowsDuplicateName_WhenIdsDiffer()
    {
        var cfg = new TaskConfiguration
        {
            Tasks = new()
            {
                new TaskSelection { Name = "御魂", Id = "a" },
                new TaskSelection { Name = "御魂", Id = "b" },
                new TaskSelection { Name = "觉醒", Id = "c" },
            },
        };

        Assert.Empty(cfg.Validate());
    }

    [Fact]
    public void DisplayName_FallsBackToName_WhenAliasEmpty()
    {
        Assert.Equal("御魂", new TaskSelection { Name = "御魂" }.DisplayName);
        Assert.Equal("觉醒材料", new TaskSelection { Name = "御魂", Alias = "觉醒材料" }.DisplayName);
    }
}
