using MaaYys.Engine;
using MaaYys.Engine.Config;
using MaaYys.Engine.Execution;
using MaaYys.Engine.Scheduling;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// 定时规则（P5.4）：下次触发计算、周计划命中、分钟去重。
/// 固定基准：2026-10-27 是星期二。
/// </summary>
public sealed class TimerRulesTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    private static DateTimeOffset At(int day, int hour, int minute)
        => new(2026, 10, day, hour, minute, 0, Offset);

    private static TimerEntry Entry(int hour, int minute, int weekdays = TimerRules.AllWeekdays, bool enabled = true)
        => new()
        {
            Id = "t1",
            Hour = hour,
            Minute = minute,
            Configuration = "默认配置",
            Weekdays = weekdays,
            Enabled = enabled,
        };

    [Fact]
    public void NextOccurrence_SameDayWhenStillAhead()
    {
        // 现在是周二 09:00，计划每天 10:00 → 就是今天
        DateTimeOffset? next = TimerRules.NextOccurrence(Entry(10, 0), At(27, 9, 0));

        Assert.Equal(At(27, 10, 0), next);
    }

    [Fact]
    public void NextOccurrence_RollsToTomorrowWhenPassed()
    {
        // 现在是周二 11:00，计划每天 10:00 → 明天
        DateTimeOffset? next = TimerRules.NextOccurrence(Entry(10, 0), At(27, 11, 0));

        Assert.Equal(At(28, 10, 0), next);
    }

    [Fact]
    public void NextOccurrence_SkipsWeekdaysNotInPlan()
    {
        // 只跑周一（DayOfWeek.Monday = 1 → 位 1），现在是周二 → 下周一 11/2
        int mondayOnly = 1 << (int)DayOfWeek.Monday;
        DateTimeOffset? next = TimerRules.NextOccurrence(Entry(10, 0, mondayOnly), At(27, 9, 0));

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, Offset), next);
        Assert.Equal(DayOfWeek.Monday, next!.Value.DayOfWeek);
    }

    [Fact]
    public void NextOccurrence_ReturnsNullForInvalidEntry()
    {
        Assert.Null(TimerRules.NextOccurrence(Entry(25, 0), At(27, 9, 0)));
        Assert.Null(TimerRules.NextOccurrence(new TimerEntry { Hour = 10, Minute = 0 }, At(27, 9, 0)));
    }

    [Fact]
    public void ShouldFire_RequiresEnabledTimeAndWeekday()
    {
        TimerEntry entry = Entry(10, 0);
        Assert.True(TimerRules.ShouldFire(entry, At(27, 10, 0)));

        // 时间不对
        Assert.False(TimerRules.ShouldFire(entry, At(27, 10, 1)));

        // 禁用
        entry.Enabled = false;
        Assert.False(TimerRules.ShouldFire(entry, At(27, 10, 0)));
        entry.Enabled = true;

        // 星期不在计划内（只选周三，今天周二）
        entry.Weekdays = 1 << (int)DayOfWeek.Wednesday;
        Assert.False(TimerRules.ShouldFire(entry, At(27, 10, 0)));
    }

    [Fact]
    public void ShouldFire_SkipsDuplicateMinute()
    {
        TimerEntry entry = Entry(10, 0);
        DateTimeOffset now = At(27, 10, 0);

        entry.LastFiredKey = TimerRules.MinuteKey(now);
        Assert.False(TimerRules.ShouldFire(entry, now));

        // 下一分钟同一时刻不会命中，明天同一时刻才再触发
        Assert.True(TimerRules.ShouldFire(entry, At(28, 10, 0)));
    }

    [Fact]
    public void DescribeWeekdays_CoversKnownMasks()
    {
        Assert.Equal("每天", TimerRules.DescribeWeekdays(TimerRules.AllWeekdays));
        Assert.Equal("工作日", TimerRules.DescribeWeekdays(TimerRules.Workdays));
        Assert.Equal("周末", TimerRules.DescribeWeekdays(TimerRules.Weekend));
        Assert.Equal("一、三", TimerRules.DescribeWeekdays(
            (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Wednesday)));
    }

    [Fact]
    public void Normalize_ClampsAndFillsDefaults()
    {
        var entry = new TimerEntry { Hour = 30, Minute = -5, Weekdays = 0 };

        TimerRules.Normalize(entry);

        Assert.Equal(23, entry.Hour);
        Assert.Equal(0, entry.Minute);
        Assert.Equal(TimerRules.AllWeekdays, entry.Weekdays);
    }
}

/// <summary>调度器轮询行为：到点触发一次、同分钟不重复、跨天再触发。</summary>
public sealed class TimerSchedulerTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    private static DateTimeOffset At(int day, int hour, int minute)
        => new(2026, 10, day, hour, minute, 0, Offset);

    [Fact]
    public void Poll_FiresOncePerMinute()
    {
        TimerEntry entry = new() { Id = "a", Hour = 10, Minute = 0, Configuration = "默认配置" };
        List<TimerEntry> entries = [entry];
        List<DateTimeOffset> fired = [];

        using var scheduler = new TimerScheduler(() => entries);
        scheduler.Fired += (_, e) => fired.Add(e.When);

        Assert.Single(scheduler.Poll(At(27, 10, 0)));
        Assert.Single(fired);

        // 同一分钟内再轮询多次都不该重复
        Assert.Empty(scheduler.Poll(At(27, 10, 0)));
        Assert.Empty(scheduler.Poll(At(27, 10, 0)));
        Assert.Single(fired);

        // 次日同一时刻再触发
        Assert.Single(scheduler.Poll(At(28, 10, 0)));
        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void Poll_UpdatesLastFiredKey()
    {
        TimerEntry entry = new() { Id = "a", Hour = 10, Minute = 0, Configuration = "默认配置" };
        List<TimerEntry> entries = [entry];

        using var scheduler = new TimerScheduler(() => entries);
        scheduler.Poll(At(27, 10, 0));

        Assert.Equal("202610271000", entry.LastFiredKey);
    }

    [Fact]
    public void Poll_IgnoresDisabledEntry()
    {
        TimerEntry entry = new() { Id = "a", Hour = 10, Minute = 0, Configuration = "默认配置", Enabled = false };
        List<TimerEntry> entries = [entry];

        using var scheduler = new TimerScheduler(() => entries);
        Assert.Empty(scheduler.Poll(At(27, 10, 0)));
    }

    [Fact]
    public void Poll_RespectsWeekdayPlan()
    {
        // 只在周三触发；基准日周二
        TimerEntry entry = new()
        {
            Id = "a",
            Hour = 10,
            Minute = 0,
            Configuration = "默认配置",
            Weekdays = 1 << (int)DayOfWeek.Wednesday,
        };
        List<TimerEntry> entries = [entry];

        using var scheduler = new TimerScheduler(() => entries);
        Assert.Empty(scheduler.Poll(At(27, 10, 0)));   // 周二
        Assert.Single(scheduler.Poll(At(28, 10, 0)));  // 周三
    }

    [Fact]
    public void Poll_TicksEvenWhenNothingFires()
    {
        int ticks = 0;
        using var scheduler = new TimerScheduler(() => []);
        scheduler.Ticked += (_, _) => ticks++;

        scheduler.Poll(At(27, 10, 0));

        Assert.Equal(1, ticks);
    }
}

/// <summary>
/// 定时到点 → 真引擎执行的链路（P5.4）：确认 <see cref="TimerScheduler"/> 的触发回调
/// 能直接把队列投递下去并跑完，中间没有线程死锁 / 重入问题。
/// </summary>
public sealed class TimerExecutionTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    private static DateTimeOffset At(int day, int hour, int minute)
        => new(2026, 10, day, hour, minute, 0, Offset);

    [Fact]
    public async Task TimerFire_LaunchesQueueOnRealEngine()
    {
        using MaaYysEngine engine = MaaYysEngine.CreateNull(marshalToSynchronizationContext: false);
        EngineConnectResult connect = await engine.ConnectAsync();
        Assert.True(connect.Success, connect.Error);
        Assert.True(await engine.LoadResourceAsync(TestPaths.TestResourceDir), "资源加载失败");

        using var host = new MaaEngineExecutionHost(engine);
        using var executor = new QueueExecutor(host, new ExecutionOptions { StallTimeoutSeconds = 30 });

        TimerEntry entry = new() { Id = "t", Hour = 8, Minute = 30, Configuration = "默认配置" };
        List<TimerEntry> entries = [entry];
        Task<ExecutionSummary>? started = null;
        DateTimeOffset firedAt = default;

        using var scheduler = new TimerScheduler(() => entries);
        scheduler.Fired += (_, e) =>
        {
            firedAt = e.When;
            started = executor.RunAsync([new ExecutionItem("a", "TestTask", null, "定时任务")]);
        };

        TimerEntry? fired = Assert.Single(scheduler.Poll(At(27, 8, 30)));
        Assert.Equal(At(27, 8, 30), firedAt);
        Assert.Equal("202610270830", fired.LastFiredKey);
        Assert.NotNull(started);

        ExecutionSummary summary = await started!.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(ExecutionOutcome.Completed, summary.Outcome);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(ExecutionState.Idle, executor.State);
    }
}

public sealed class ConfigurationStoreTimerTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public ConfigurationStoreTimerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maayys-timer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "maayys.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private ConfigurationStore LoadedStore()
    {
        ConfigurationStore store = new(_path);
        store.Load();
        return store;
    }

    [Fact]
    public void Timers_RoundTripThroughFile()
    {
        ConfigurationStore store = LoadedStore();
        TimerEntry added = store.AddTimer(new TimerEntry
        {
            Label = "每日御魂",
            Hour = 8,
            Minute = 30,
            Configuration = "默认配置",
            Weekdays = TimerRules.Workdays,
            ForceStart = true,
        });

        Assert.False(string.IsNullOrEmpty(added.Id));

        ConfigurationStore reloaded = LoadedStore();
        TimerEntry only = Assert.Single(reloaded.Timers);
        Assert.Equal(added.Id, only.Id);
        Assert.Equal(8, only.Hour);
        Assert.Equal(30, only.Minute);
        Assert.Equal(TimerRules.Workdays, only.Weekdays);
        Assert.True(only.ForceStart);
        Assert.Equal("每日御魂", only.Label);
    }

    [Fact]
    public void UpdateTimer_PersistsChanges()
    {
        ConfigurationStore store = LoadedStore();
        TimerEntry added = store.AddTimer(new TimerEntry { Hour = 8, Minute = 0, Configuration = "默认配置" });

        TimerEntry edit = added.Clone();
        edit.Hour = 21;
        edit.Enabled = false;
        Assert.True(store.UpdateTimer(edit));

        TimerEntry reloaded = Assert.Single(LoadedStore().Timers);
        Assert.Equal(21, reloaded.Hour);
        Assert.False(reloaded.Enabled);
    }

    [Fact]
    public void RemoveTimer_Persists()
    {
        ConfigurationStore store = LoadedStore();
        TimerEntry added = store.AddTimer(new TimerEntry { Hour = 8, Minute = 0, Configuration = "默认配置" });

        Assert.True(store.RemoveTimer(added.Id));
        Assert.Empty(LoadedStore().Timers);
    }

    [Fact]
    public void MarkTimerFired_WritesMinuteKey()
    {
        ConfigurationStore store = LoadedStore();
        TimerEntry added = store.AddTimer(new TimerEntry { Hour = 8, Minute = 0, Configuration = "默认配置" });

        store.MarkTimerFired(added.Id, "202610270800");

        Assert.Equal("202610270800", Assert.Single(LoadedStore().Timers).LastFiredKey);
    }

    [Fact]
    public void RetargetTimers_RebindsOnRenameAndDropsOnDelete()
    {
        ConfigurationStore store = LoadedStore();
        store.AddTimer(new TimerEntry { Hour = 8, Minute = 0, Configuration = "旧组" });

        Assert.Equal(1, store.RetargetTimers("旧组", "新组"));
        Assert.Equal("新组", Assert.Single(store.Timers).Configuration);

        Assert.Equal(1, store.RetargetTimers("新组", null));
        Assert.Empty(store.Timers);
    }

    [Fact]
    public void Load_SkipsInvalidTimersAndDuplicateIds()
    {
        // 手写一份带坏数据的文件：一条时/分越界、两条同 Id
        string json = """
        {
          "config_version": 1,
          "current": "默认配置",
          "configurations": { "默认配置": { "name": "默认配置", "tasks": [] } },
          "timers": [
            { "id": "dup", "hour": 9, "minute": 0, "configuration": "默认配置", "weekdays": 127 },
            { "id": "dup", "hour": 10, "minute": 0, "configuration": "默认配置", "weekdays": 127 },
            { "id": "bad", "hour": 30, "minute": 0, "configuration": "默认配置", "weekdays": 127 }
          ]
        }
        """;
        File.WriteAllText(_path, json, new System.Text.UTF8Encoding(false));

        ConfigurationStore store = LoadedStore();

        // 越界的会被规整成 23:00（Normalize 先执行），所以仍在列表里；但重复 Id 只留第一条
        Assert.Equal(2, store.Timers.Count);
        Assert.Equal(9, store.Timers[0].Hour);
        Assert.Contains(store.LoadErrors, e => e.Contains("重复 Id"));
    }
}
