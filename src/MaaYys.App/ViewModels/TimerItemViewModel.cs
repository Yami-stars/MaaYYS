using System.Collections.ObjectModel;
using System.Windows.Input;
using MaaYys.Engine.Scheduling;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 定时计划的一行（P5.4）：时间、周计划、绑定配置组、启用 / 强制定时启动，改动即保存。
/// <para>行内直接编辑（对标 MAA 定时器列表），没有「编辑模式」，减少一次点击。</para>
/// </summary>
public sealed class TimerItemViewModel : ViewModelBase
{
    private readonly TimerEntry _entry;
    private readonly Action<TimerItemViewModel> _onChanged;

    public TimerItemViewModel(
        TimerEntry entry,
        IEnumerable<string> configurationNames,
        Action<TimerItemViewModel> onChanged,
        Action<TimerItemViewModel> onRemove,
        Action<TimerItemViewModel> onRunNow)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entry = entry;
        _onChanged = onChanged;

        foreach (string name in configurationNames)
        {
            ConfigurationNames.Add(name);
        }

        RemoveCommand = new RelayCommand(_ => onRemove(this));
        RunNowCommand = new RelayCommand(_ => onRunNow(this));
    }

    /// <summary>可用配置组（下拉选项）。</summary>
    public ObservableCollection<string> ConfigurationNames { get; } = [];

    /// <summary>小时下拉选项。</summary>
    public IReadOnlyList<int> HourOptions { get; } = Enumerable.Range(0, 24).ToList();

    /// <summary>分钟下拉选项。</summary>
    public IReadOnlyList<int> MinuteOptions { get; } = Enumerable.Range(0, 60).ToList();

    public string Id => _entry.Id;

    public ICommand RemoveCommand { get; }

    /// <summary>立即试跑（不等时间到）。</summary>
    public ICommand RunNowCommand { get; }

    public int Hour
    {
        get => _entry.Hour;
        set => Apply(() => _entry.Hour = value);
    }

    public int Minute
    {
        get => _entry.Minute;
        set => Apply(() => _entry.Minute = value);
    }

    public string? Configuration
    {
        get => _entry.Configuration;
        set => Apply(() => _entry.Configuration = value ?? string.Empty);
    }

    public bool Enabled
    {
        get => _entry.Enabled;
        set => Apply(() => _entry.Enabled = value);
    }

    public bool ForceStart
    {
        get => _entry.ForceStart;
        set => Apply(() => _entry.ForceStart = value);
    }

    public string? Label
    {
        get => _entry.Label;
        set => Apply(() => _entry.Label = value);
    }

    // ---- 周计划（7 个开关 + 快捷按钮） ----

    public bool IsMonday
    {
        get => Has(DayOfWeek.Monday);
        set => SetDay(DayOfWeek.Monday, value);
    }

    public bool IsTuesday
    {
        get => Has(DayOfWeek.Tuesday);
        set => SetDay(DayOfWeek.Tuesday, value);
    }

    public bool IsWednesday
    {
        get => Has(DayOfWeek.Wednesday);
        set => SetDay(DayOfWeek.Wednesday, value);
    }

    public bool IsThursday
    {
        get => Has(DayOfWeek.Thursday);
        set => SetDay(DayOfWeek.Thursday, value);
    }

    public bool IsFriday
    {
        get => Has(DayOfWeek.Friday);
        set => SetDay(DayOfWeek.Friday, value);
    }

    public bool IsSaturday
    {
        get => Has(DayOfWeek.Saturday);
        set => SetDay(DayOfWeek.Saturday, value);
    }

    public bool IsSunday
    {
        get => Has(DayOfWeek.Sunday);
        set => SetDay(DayOfWeek.Sunday, value);
    }

    // ---- 展示 ----

    /// <summary>「08:30」。</summary>
    public string TimeText => $"{_entry.Hour:00}:{_entry.Minute:00}";

    /// <summary>「每天 / 工作日 / 一、三」。</summary>
    public string WeekdaysText => TimerRules.DescribeWeekdays(_entry.Weekdays);

    /// <summary>下次触发时间（界面每秒/每次轮询刷新）。</summary>
    public string NextRunText
    {
        get
        {
            DateTimeOffset? next = TimerRules.NextOccurrence(_entry, DateTimeOffset.Now);
            return next is null
                ? "—"
                : next.Value.ToString("MM-dd HH:mm") + " " + WeekdayName(next.Value.DayOfWeek);
        }
    }

    /// <summary>一行的摘要（无障碍朗读 / 提示用）。</summary>
    public string Summary => $"{TimeText} · {WeekdaysText}"
                             + (string.IsNullOrWhiteSpace(_entry.Label) ? string.Empty : $" · {_entry.Label}")
                             + $" → {_entry.Configuration}";

    /// <summary>导出为引擎侧对象（保存时用副本，避免与界面对象共享引用）。</summary>
    public TimerEntry ToEntry() => _entry.Clone();

    /// <summary>周计划刷新（界面「下次触发」定期更新）。</summary>
    public void RefreshDerived()
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(WeekdaysText));
        OnPropertyChanged(nameof(NextRunText));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>同步可用配置组（任务页新建/删除了配置组时）。</summary>
    public void SyncConfigurations(IReadOnlyList<string> names)
    {
        if (names.SequenceEqual(ConfigurationNames, StringComparer.Ordinal))
        {
            return;
        }

        ConfigurationNames.Clear();
        foreach (string name in names)
        {
            ConfigurationNames.Add(name);
        }

        // 绑定的组被删掉时清空，界面会提示「未绑定」
        if (!string.IsNullOrEmpty(_entry.Configuration) && !names.Contains(_entry.Configuration, StringComparer.Ordinal))
        {
            _entry.Configuration = string.Empty;
            RaiseDerived();
        }
    }

    private bool Has(DayOfWeek day) => TimerRules.IncludesWeekday(_entry.Weekdays, day);

    private void SetDay(DayOfWeek day, bool value)
    {
        int bit = 1 << (int)day;
        int mask = value ? _entry.Weekdays | bit : _entry.Weekdays & ~bit;
        Apply(() => _entry.Weekdays = mask);
    }

    private void Apply(Action mutate)
    {
        mutate();
        TimerRules.Normalize(_entry);
        RaiseDerived();
        _onChanged(this);
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(Hour));
        OnPropertyChanged(nameof(Minute));
        OnPropertyChanged(nameof(Configuration));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(ForceStart));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(IsMonday));
        OnPropertyChanged(nameof(IsTuesday));
        OnPropertyChanged(nameof(IsWednesday));
        OnPropertyChanged(nameof(IsThursday));
        OnPropertyChanged(nameof(IsFriday));
        OnPropertyChanged(nameof(IsSaturday));
        OnPropertyChanged(nameof(IsSunday));
        RefreshDerived();
    }

    private static string WeekdayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };
}
