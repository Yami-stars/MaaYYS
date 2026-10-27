using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaaYys.Engine.Config;

/// <summary>
/// 配置组持久化（<c>config/maayys.json</c>），对标 MAA <c>gui.new.json</c> 的
/// <c>Configurations</c> 命名组 + <c>Current</c> 指针 + <c>ConfigVersion</c>。
///
/// 与 <see cref="InstanceStore"/> 的分工：
/// <list type="bullet">
/// <item>instances.json —— 连着哪个设备（窗口/ADB、截图方式、资源集）；</item>
/// <item>maayys.json —— 跑哪些任务、选项怎么选（可被多个实例共用一套）。</item>
/// </list>
///
/// 设计要点与实例清单保持一致：便携优先、损坏先备份、原子写、变更即落盘。
/// </summary>
public sealed class ConfigurationStore
{
    /// <summary>配置文件名。</summary>
    public const string FileName = "maayys.json";

    /// <summary>当前配置文件格式版本（<c>config_version</c>）。</summary>
    public const int ConfigVersion = 1;

    /// <summary>默认配置组名（首次启动/迁移后无可用组时使用）。</summary>
    public const string DefaultName = "默认配置";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly List<TaskConfiguration> _ordered = [];
    private readonly List<Scheduling.TimerEntry> _timers = [];
    private readonly Logging.LogOptions _logOptions = new();

    public ConfigurationStore(string? path = null)
    {
        Path = path ?? DefaultPath;
    }

    /// <summary>默认路径：程序目录下 <c>config/maayys.json</c>（与 instances.json 同目录）。</summary>
    public static string DefaultPath => System.IO.Path.Combine(AppContext.BaseDirectory, "config", FileName);

    /// <summary>配置文件绝对路径。</summary>
    public string Path { get; }

    /// <summary>全部配置组（按声明顺序，下拉框顺序即此顺序）。</summary>
    public IReadOnlyList<TaskConfiguration> Configurations
    {
        get
        {
            lock (_gate)
            {
                return _ordered.ToList();
            }
        }
    }

    /// <summary>当前配置组名（Current 指针）。</summary>
    public string? Current { get; private set; }

    /// <summary>当前配置组；无任何配置组时为 null。</summary>
    public TaskConfiguration? CurrentConfiguration
    {
        get
        {
            lock (_gate)
            {
                return FindCore(Current) ?? _ordered.FirstOrDefault();
            }
        }
    }

    /// <summary>加载期问题（文件损坏/版本过高/条目被纠正），供 UI 提示；不影响可用性。</summary>
    public List<string> LoadErrors { get; } = [];

    /// <summary>
    /// 定时计划（P5.4）：与配置组同级存放，跨配置组共享一份。
    /// 返回的是副本，改动请走 <see cref="AddTimer"/> / <see cref="UpdateTimer"/> / <see cref="RemoveTimer"/>。
    /// </summary>
    public IReadOnlyList<Scheduling.TimerEntry> Timers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Select(t => t.Clone()).ToList();
            }
        }
    }

    /// <summary>
    /// 日志策略（P5.5）：与配置组同级存放。
    /// 返回的是**同一个实例**（不是副本）——引擎日志管线持有它，改完立即对写入端生效；
    /// 改动请走 <see cref="UpdateLogOptions"/>（负责夹取、落盘、广播）。
    /// </summary>
    public Logging.LogOptions LogOptions
    {
        get
        {
            lock (_gate)
            {
                return _logOptions;
            }
        }
    }

    /// <summary>配置内容或 Current 变化（UI 据此刷新下拉框与任务列表）。</summary>
    public event EventHandler? Changed;

    // ---- 加载 / 保存 ----

    /// <summary>
    /// 读取配置。
    /// <para>
    /// 文件不存在时不会凭空造一个空配置组：若 <paramref name="migrationSources"/> 里有带
    /// <see cref="InstanceDefinition.TaskEntries"/> 的实例，会先把它们「提升」为同名配置组
    /// （P2.4 → P2.6 迁移），再落盘；否则才建一个空的默认组。
    /// </para>
    /// </summary>
    /// <param name="migrationSources">
    /// 迁移来源（通常是当前实例清单）。传 null 表示不做迁移。
    /// 迁移会写回 <see cref="InstanceDefinition.ConfigurationName"/>，调用方需在返回后判断是否需要保存实例清单。
    /// </param>
    /// <returns>是否修改了 <paramref name="migrationSources"/> 中的实例（调用方据此决定是否 Save）。</returns>
    public bool Load(IReadOnlyList<InstanceDefinition>? migrationSources = null)
    {
        bool touchedInstances = false;

        lock (_gate)
        {
            _ordered.Clear();
            _timers.Clear();
            LoadErrors.Clear();
            Current = null;

            bool needSave = false;

            if (!File.Exists(Path))
            {
                needSave = true;
            }
            else
            {
                Document? document = null;
                try
                {
                    document = JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), SerializerOptions);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    string backup = BackupBrokenFile();
                    LoadErrors.Add($"配置文件解析失败（{ex.Message}），已备份为 {System.IO.Path.GetFileName(backup)}");
                    needSave = true;
                }

                if (document is not null)
                {
                    if (document.ConfigVersion > ConfigVersion)
                    {
                        LoadErrors.Add($"配置文件版本 {document.ConfigVersion} 高于程序支持的 {ConfigVersion}，已按兼容方式读取");
                    }

                    Adopt(document.Configurations);
                    AdoptTimers(document.Timers);
                    AdoptLogOptions(document.Log);
                    Current = document.Current is { } current && FindCore(current) is not null
                        ? current
                        : _ordered.FirstOrDefault()?.Name;
                }
            }

            // 迁移：仅在「还没有任何配置组」时进行，避免覆盖用户已有配置
            if (_ordered.Count == 0 && migrationSources is { Count: > 0 })
            {
                touchedInstances = MigrateFromInstancesCore(migrationSources) > 0;
                needSave |= touchedInstances;
            }

            if (_ordered.Count == 0)
            {
                _ordered.Add(new TaskConfiguration { Name = DefaultName });
                needSave = true;
            }

            Current ??= _ordered[0].Name;

            if (needSave)
            {
                SaveCore();
            }
        }

        return touchedInstances;
    }

    /// <summary>写盘（原子替换 + 上一版备份）。</summary>
    public void Save()
    {
        lock (_gate)
        {
            SaveCore();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 更新日志策略（P5.5）：原地覆盖 + 夹取 + 落盘。不换对象，引擎日志管线无需重连。
    /// </summary>
    public void UpdateLogOptions(Logging.LogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            _logOptions.CopyFrom(options).Sanitized();
            SaveCore();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- 查询 ----

    /// <summary>按名字查找配置组（不存在返回 null）。</summary>
    public TaskConfiguration? Find(string? name)
    {
        lock (_gate)
        {
            return FindCore(name);
        }
    }

    /// <summary>确保存在该名字的配置组，不存在则创建并返回。</summary>
    public TaskConfiguration GetOrCreate(string? name)
    {
        TaskConfiguration created;
        bool added = false;

        lock (_gate)
        {
            string target = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
            TaskConfiguration? existing = FindCore(target);
            if (existing is not null)
            {
                return existing;
            }

            created = new TaskConfiguration { Name = UniqueNameCore(target) };
            _ordered.Add(created);
            Current ??= created.Name;
            SaveCore();
            added = true;
        }

        if (added)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return created;
    }

    // ---- 变更操作 ----

    /// <summary>新建配置组（自动去重名），并切为当前。</summary>
    public TaskConfiguration Add(string? name = null)
    {
        TaskConfiguration created;
        lock (_gate)
        {
            created = new TaskConfiguration { Name = UniqueNameCore(name ?? NextNameCore()) };
            _ordered.Add(created);
            Current = created.Name;
            SaveCore();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return created;
    }

    /// <summary>复制配置组，返回副本并切为当前。</summary>
    public TaskConfiguration? Duplicate(string sourceName)
    {
        TaskConfiguration? copy = null;
        lock (_gate)
        {
            TaskConfiguration? source = FindCore(sourceName);
            if (source is null)
            {
                return null;
            }

            copy = source.Clone(UniqueNameCore($"{source.Name} 副本"));
            _ordered.Insert(_ordered.IndexOf(source) + 1, copy);
            Current = copy.Name;
            SaveCore();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return copy;
    }

    /// <summary>
    /// 删除配置组。**允许删空**——配置组不像实例那样是 UI 的立足点，
    /// 删空后由 UI 提示「新建配置组」，不会出现无处置身的情况。
    /// 删除的是当前组时，Current 落到相邻组（无剩余则为 null）。
    /// </summary>
    public bool Remove(string name, out string? error)
    {
        error = null;
        bool removed = false;

        lock (_gate)
        {
            int index = _ordered.FindIndex(c => string.Equals(c.Name, name, StringComparison.Ordinal));
            if (index < 0)
            {
                error = "配置组不存在";
                return false;
            }

            _ordered.RemoveAt(index);
            if (string.Equals(Current, name, StringComparison.Ordinal))
            {
                Current = _ordered.Count == 0
                    ? null
                    : _ordered[Math.Min(index, _ordered.Count - 1)].Name;
            }

            SaveCore();
            removed = true;
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>重命名配置组。重命名后 Current 指针跟着走。</summary>
    public bool Rename(string oldName, string newName, out string? error)
    {
        error = null;
        bool renamed = false;

        lock (_gate)
        {
            TaskConfiguration? target = FindCore(oldName);
            if (target is null)
            {
                error = "配置组不存在";
                return false;
            }

            string trimmed = (newName ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                error = "配置组名不能为空";
                return false;
            }

            if (trimmed.Length > TaskConfiguration.MaxNameLength)
            {
                error = $"配置组名不能超过 {TaskConfiguration.MaxNameLength} 个字符";
                return false;
            }

            if (string.Equals(trimmed, oldName, StringComparison.Ordinal))
            {
                return false;
            }

            if (FindCore(trimmed) is not null)
            {
                error = $"已存在同名配置组「{trimmed}」";
                return false;
            }

            target.Name = trimmed;
            target.UpdatedAt = DateTimeOffset.Now;
            if (string.Equals(Current, oldName, StringComparison.Ordinal))
            {
                Current = trimmed;
            }

            SaveCore();
            renamed = true;
        }

        if (renamed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return renamed;
    }

    /// <summary>切换当前配置组。</summary>
    public bool SetCurrent(string name)
    {
        bool changed = false;
        lock (_gate)
        {
            if (!string.Equals(Current, name, StringComparison.Ordinal) && FindCore(name) is not null)
            {
                Current = name;
                SaveCore();
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>
    /// 整组内容被就地修改后调用：刷新时间戳 + 落盘。
    /// 直接改 <see cref="TaskConfiguration.Tasks"/> 的调用方必须调它，否则改动不会保存。
    /// </summary>
    public void Touch(string name)
    {
        lock (_gate)
        {
            TaskConfiguration? target = FindCore(name);
            if (target is null)
            {
                return;
            }

            target.UpdatedAt = DateTimeOffset.Now;
            SaveCore();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- 定时计划（P5.4） ----

    /// <summary>新增一条定时计划（分配 Id、规整参数后落盘）。</summary>
    public Scheduling.TimerEntry AddTimer(Scheduling.TimerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Scheduling.TimerEntry added = entry.Clone();
        added.Id = string.Empty;
        added.EnsureId();
        Scheduling.TimerRules.Normalize(added);

        lock (_gate)
        {
            _timers.Add(added);
            SaveCore();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return added.Clone();
    }

    /// <summary>按 Id 覆盖一条定时计划（含启停、时间、周计划、绑定配置）。</summary>
    public bool UpdateTimer(Scheduling.TimerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            int index = _timers.FindIndex(t => string.Equals(t.Id, entry.Id, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            Scheduling.TimerEntry copy = entry.Clone();
            Scheduling.TimerRules.Normalize(copy);
            copy.Id = _timers[index].Id;
            _timers[index] = copy;
            SaveCore();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>删除一条定时计划。</summary>
    public bool RemoveTimer(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _timers.RemoveAll(t => string.Equals(t.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                SaveCore();
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>配置组被删除/改名后清理或改绑定时计划，避免指向不存在的组。</summary>
    /// <returns>被改动（删除或改绑）的条数。</returns>
    public int RetargetTimers(string oldConfiguration, string? newConfiguration)
    {
        int touched = 0;
        lock (_gate)
        {
            if (string.IsNullOrEmpty(newConfiguration))
            {
                touched = _timers.RemoveAll(t => string.Equals(t.Configuration, oldConfiguration, StringComparison.Ordinal));
            }
            else
            {
                foreach (Scheduling.TimerEntry timer in _timers)
                {
                    if (string.Equals(timer.Configuration, oldConfiguration, StringComparison.Ordinal))
                    {
                        timer.Configuration = newConfiguration;
                        touched++;
                    }
                }
            }

            if (touched > 0)
            {
                SaveCore();
            }
        }

        if (touched > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return touched;
    }

    /// <summary>
    /// 记录某条计划已触发（写 <c>last_fired</c>），保证重启后同一分钟不重复跑。
    /// </summary>
    public void MarkTimerFired(string id, string minuteKey)
    {
        bool touched;
        lock (_gate)
        {
            Scheduling.TimerEntry? target = _timers.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
            touched = target is not null && !string.Equals(target.LastFiredKey, minuteKey, StringComparison.Ordinal);
            if (touched)
            {
                target!.LastFiredKey = minuteKey;
                SaveCore();
            }
        }

        if (touched)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- 导入 / 导出 ----

    /// <summary>
    /// 从文件导入配置组。支持三种格式（按内容自动识别）：
    /// <list type="number">
    /// <item>MaaYys 完整配置（<c>config_version</c> + <c>configurations</c>）；</item>
    /// <item>MaaYys 单个配置组（含 <c>name</c> + <c>tasks</c>）；</item>
    /// <item>MAA <c>gui.new.json</c>（<c>Configurations</c> + <c>TaskQueue</c>，只取任务名与启用状态）。</item>
    /// </list>
    /// 同名配置组自动加后缀，不覆盖已有配置。
    /// </summary>
    public ImportResult Import(string filePath)
    {
        var result = new ImportResult();

        if (!File.Exists(filePath))
        {
            result.Error = $"文件不存在：{filePath}";
            return result;
        }

        List<TaskConfiguration> groups;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(filePath),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            groups = ParseGroups(document.RootElement, result.Errors);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            result.Error = $"解析失败：{ex.Message}";
            return result;
        }

        if (groups.Count == 0)
        {
            // 优先把解析阶段的具体原因抛给调用方，而不是笼统的「没有配置组」
            result.Error = result.Errors.Count > 0
                ? string.Join("；", result.Errors)
                : "文件里没有识别到任何配置组";
            return result;
        }

        lock (_gate)
        {
            foreach (TaskConfiguration group in groups)
            {
                group.Name = UniqueNameCore(group.Name);
                group.CreatedAt = DateTimeOffset.Now;
                group.UpdatedAt = group.CreatedAt;
                _ordered.Add(group);
                result.Imported.Add(group.Name);
            }

            Current ??= _ordered.FirstOrDefault()?.Name;
            SaveCore();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>
    /// 导出配置。指定 <paramref name="name"/> 时只导该组，否则导出全部 + Current 指针。
    /// </summary>
    public bool Export(string filePath, string? name = null)
    {
        string json;
        lock (_gate)
        {
            if (name is null)
            {
                json = JsonSerializer.Serialize(ToDocument(_ordered, Current), SerializerOptions);
            }
            else
            {
                TaskConfiguration? single = FindCore(name);
                if (single is null)
                {
                    return false;
                }

                json = JsonSerializer.Serialize(ToDocument([single], single.Name), SerializerOptions);
            }
        }

        try
        {
            string? directory = System.IO.Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, json, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>生成导出/预览用的 JSON（不落盘）。</summary>
    public string ToJson(string? name = null)
    {
        lock (_gate)
        {
            if (name is not null && FindCore(name) is { } single)
            {
                return JsonSerializer.Serialize(ToDocument([single], single.Name), SerializerOptions);
            }

            return JsonSerializer.Serialize(ToDocument(_ordered, Current), SerializerOptions);
        }
    }

    // ---- 迁移 ----

    /// <summary>
    /// 把实例上旧的 <see cref="InstanceDefinition.TaskEntries"/> 提升为同名配置组（P2.4 → P2.6）。
    /// <para>
    /// 只处理「有任务但还没有配置组名」的实例；已迁移过的实例原样跳过。
    /// 迁移后会写回 <see cref="InstanceDefinition.ConfigurationName"/>，调用方负责保存实例清单。
    /// </para>
    /// </summary>
    /// <returns>被迁移的实例数量。</returns>
    public int MigrateFromInstances(IReadOnlyList<InstanceDefinition> instances)
    {
        int migrated;
        lock (_gate)
        {
            migrated = MigrateFromInstancesCore(instances);
            if (migrated > 0)
            {
                Current ??= _ordered.FirstOrDefault()?.Name;
                SaveCore();
            }
        }

        if (migrated > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return migrated;
    }

    // ---- 内部 ----

    /// <summary>调用方必须已持有 <see cref="_gate"/>。返回迁移的实例数量。</summary>
    private int MigrateFromInstancesCore(IReadOnlyList<InstanceDefinition> instances)
    {
        int migrated = 0;

        foreach (InstanceDefinition instance in instances)
        {
            if (!string.IsNullOrWhiteSpace(instance.ConfigurationName))
            {
                continue;
            }

            if (instance.TaskEntries is not { Count: > 0 })
            {
                continue;
            }

            var group = new TaskConfiguration
            {
                Name = UniqueNameCore(instance.Name),
                Tasks = instance.TaskEntries
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.Ordinal)
                    .Select(n => new TaskSelection { Name = n, Enabled = true })
                    .ToList(),
            };

            _ordered.Add(group);
            instance.ConfigurationName = group.Name;
            migrated++;
        }

        Current ??= _ordered.FirstOrDefault()?.Name;
        return migrated;
    }

    /// <summary>调用方必须已持有 <see cref="_gate"/>。</summary>
    private TaskConfiguration? FindCore(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : _ordered.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>调用方必须已持有 <see cref="_gate"/>。</summary>
    private string UniqueNameCore(string baseName)
    {
        string name = (baseName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            name = DefaultName;
        }

        if (FindCore(name) is null)
        {
            return name;
        }

        for (int n = 2; ; n++)
        {
            string candidate = $"{name} {n}";
            if (FindCore(candidate) is null)
            {
                return candidate;
            }
        }
    }

    /// <summary>调用方必须已持有 <see cref="_gate"/>。</summary>
    private string NextNameCore()
    {
        for (int n = 1; ; n++)
        {
            string candidate = n == 1 ? DefaultName : $"{DefaultName} {n}";
            if (FindCore(candidate) is null)
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 把读到的配置组装进 _ordered。调用方必须已持有 <see cref="_gate"/>。
    /// 容错：丢弃无名项、纠正「字典键与 name 字段不一致」、去重同名。
    /// </summary>
    private void Adopt(List<Entry>? entries)
    {
        if (entries is null)
        {
            return;
        }

        foreach (Entry entry in entries)
        {
            string name = string.IsNullOrWhiteSpace(entry.Key) ? entry.Value.Name : entry.Key;
            name = (name ?? string.Empty).Trim();

            if (name.Length == 0)
            {
                LoadErrors.Add("丢弃了一个没有名字的配置组");
                continue;
            }

            if (FindCore(name) is not null)
            {
                string adjusted = UniqueNameCore(name);
                LoadErrors.Add($"配置组「{name}」重复，已改名为「{adjusted}」");
                name = adjusted;
            }

            TaskConfiguration group = entry.Value;
            if (!string.Equals(group.Name, name, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(group.Name))
                {
                    LoadErrors.Add($"配置组「{group.Name}」的 name 与键「{name}」不一致，已以键为准");
                }

                group.Name = name;
            }

            group.Tasks ??= [];
            // 任务条目容错：丢弃空名、去重
            HashSet<string> seen = new(StringComparer.Ordinal);
            List<TaskSelection> kept = [];
            foreach (TaskSelection task in group.Tasks)
            {
                if (string.IsNullOrWhiteSpace(task.Name))
                {
                    continue;
                }

                task.Options ??= new Dictionary<string, string>(StringComparer.Ordinal);
                if (seen.Add(task.Name))
                {
                    kept.Add(task);
                }
                else
                {
                    LoadErrors.Add($"配置组「{name}」里任务「{task.Name}」重复，已只保留第一个");
                }
            }

            group.Tasks = kept;
            group.EnsureIds();          // 旧配置无 id 字段，补齐以保证多实例定位可用
            _ordered.Add(group);
        }
    }

    /// <summary>
    /// 装载定时计划：补 Id、规整非法值、丢弃无名条目的重复项。调用方必须已持有 <see cref="_gate"/>。
    /// </summary>
    private void AdoptTimers(List<Scheduling.TimerEntry>? timers)
    {
        if (timers is null)
        {
            return;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (Scheduling.TimerEntry timer in timers)
        {
            if (timer is null)
            {
                continue;
            }

            timer.EnsureId();
            Scheduling.TimerRules.Normalize(timer);

            if (!seen.Add(timer.Id))
            {
                LoadErrors.Add($"丢弃了重复 Id 的定时计划「{timer}」");
                continue;
            }

            if (!Scheduling.TimerRules.IsValid(timer))
            {
                LoadErrors.Add($"定时计划「{(timer.Label ?? "未命名")}」参数非法（{timer.Hour:00}:{timer.Minute:00}），已跳过");
                continue;
            }

            _timers.Add(timer);
        }
    }

    /// <summary>采纳日志策略（P5.5）：缺省段或非法值都回落到默认，不阻断加载。</summary>
    private void AdoptLogOptions(Logging.LogOptions? options)
    {
        if (options is not null)
        {
            _logOptions.CopyFrom(options);
        }

        string? before = _logOptions.TimeFormat;
        _logOptions.Sanitized();
        if (options is not null && !string.Equals(before, _logOptions.TimeFormat, StringComparison.Ordinal))
        {
            LoadErrors.Add($"日志时间格式「{before}」非法，已回退为 {Logging.LogOptions.DefaultTimeFormat}");
        }
    }

    /// <summary>原子写：tmp → Move(overwrite)，上一版留 .bak。</summary>
    private void SaveCore()
    {
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(ToDocument(_ordered, Current, _timers, _logOptions), SerializerOptions);
        string temp = Path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));

        if (File.Exists(Path))
        {
            try
            {
                File.Copy(Path, Path + ".bak", overwrite: true);
            }
            catch (IOException)
            {
                // 备份失败不阻断主流程
            }
        }

        File.Move(temp, Path, overwrite: true);
    }

    private string BackupBrokenFile()
    {
        string target = $"{Path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(Path, target, overwrite: true);
        }
        catch (IOException)
        {
            return Path;
        }

        return target;
    }

    private static Document ToDocument(
        IEnumerable<TaskConfiguration> groups,
        string? current,
        IReadOnlyList<Scheduling.TimerEntry>? timers = null,
        Logging.LogOptions? logOptions = null)
    {
        var document = new Document
        {
            ConfigVersion = ConfigVersion,
            Current = current,
        };

        foreach (TaskConfiguration group in groups)
        {
            document.Configurations.Add(new Entry { Key = group.Name, Value = group });
        }

        if (timers is { Count: > 0 })
        {
            document.Timers = timers.Select(t => t.Clone()).ToList();
        }

        document.Log = logOptions?.Clone();

        return document;
    }

    /// <summary>
    /// 从任意受支持格式里提取配置组。
    /// 识别顺序：MaaYys 完整文档 → MaaYys 单组 → MAA gui.new.json。
    /// </summary>
    private static List<TaskConfiguration> ParseGroups(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("文件根节点不是 JSON 对象");
            return [];
        }

        // 先按「大小写敏感的专属字段」区分两代格式：
        //   MaaYys → 小写 config_version / configurations / tasks
        //   MAA    → 大写 ConfigVersion / Configurations + TaskQueue
        // 否则大小写不敏感匹配会把 MAA 的 Configurations 误认成我们的配置组（其内容是 TaskQueue，解析出来是空组）。
        bool looksMaaYys = root.TryGetProperty("config_version", out _)
                          || root.TryGetProperty("configurations", out _)
                          || root.TryGetProperty("tasks", out _);

        if (!looksMaaYys)
        {
            return ParseMaaFormat(root, errors);
        }

        // ---- 1) MaaYys 完整配置（configurations 是数组或对象都接受）----
        if (TryGetProperty(root, "configurations", out JsonElement configurations))
        {
            var groups = new List<TaskConfiguration>();
            if (configurations.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in configurations.EnumerateObject())
                {
                    TaskConfiguration? group = property.Value.Deserialize<TaskConfiguration>(SerializerOptions);
                    if (group is null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(group.Name))
                    {
                        group.Name = property.Name;
                    }

                    groups.Add(group);
                }
            }
            else if (configurations.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in configurations.EnumerateArray())
                {
                    TaskConfiguration? group = element.Deserialize<TaskConfiguration>(SerializerOptions);
                    if (group is not null)
                    {
                        groups.Add(group);
                    }
                }
            }
            else
            {
                errors.Add("configurations 既不是对象也不是数组");
            }

            return groups;
        }

        // ---- 2) MaaYys 单个配置组（顶层直接是配置组）----
        if (root.TryGetProperty("tasks", out _))
        {
            TaskConfiguration? single = root.Deserialize<TaskConfiguration>(SerializerOptions);
            return single is null ? [] : [single];
        }

        errors.Add("无法识别的配置格式（既没有 configurations，也没有 tasks / Configurations）");
        return [];
    }

    /// <summary>MAA <c>gui.new.json</c> 格式：Configurations → TaskQueue，只取任务名与启用位。</summary>
    private static List<TaskConfiguration> ParseMaaFormat(JsonElement root, List<string> errors)
    {
        if (!TryGetProperty(root, "Configurations", out JsonElement maaConfigurations)
            || maaConfigurations.ValueKind != JsonValueKind.Object)
        {
            errors.Add("无法识别的配置格式（既没有 configurations，也没有 tasks / Configurations）");
            return [];
        }

        var groups = new List<TaskConfiguration>();
        foreach (JsonProperty property in maaConfigurations.EnumerateObject())
        {
            groups.Add(ReadMaaGroup(property.Name, property.Value));
        }

        if (groups.Count > 0)
        {
            errors.Add("已按 MAA gui.new.json 格式导入：只保留任务名与启用状态，MAA 专有参数已忽略");
        }

        return groups;
    }

    /// <summary>把 MAA 的一个 Configuration 转成我们的配置组（只取 TaskQueue 的名称与启用位）。</summary>
    private static TaskConfiguration ReadMaaGroup(string name, JsonElement element)
    {
        var group = new TaskConfiguration { Name = name };

        if (!TryGetProperty(element, "TaskQueue", out JsonElement queue)
            || queue.ValueKind != JsonValueKind.Array)
        {
            return group;
        }

        foreach (JsonElement item in queue.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string taskName = TryGetProperty(item, "Name", out JsonElement nameElement)
                              && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(taskName))
            {
                continue;
            }

            bool enabled = !TryGetProperty(item, "IsEnable", out JsonElement enabledElement)
                           || enabledElement.ValueKind != JsonValueKind.False;

            group.Tasks.Add(new TaskSelection { Name = taskName, Enabled = enabled });
        }

        return group;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    // ---- DTO ----

    /// <summary>
    /// 配置组条目：用「键 + 值」的数组承载字典，保证 JSON 顺序稳定（Dictionary 不保证顺序），
    /// 同时与 <c>configurations</c> 语义一致（键 = 配置组名）。
    /// </summary>
    private sealed class Entry
    {
        public string Key { get; set; } = string.Empty;

        public TaskConfiguration Value { get; set; } = new();
    }

    /// <summary>配置文件 DTO（顶层结构：config_version / current / configurations）。</summary>
    private sealed class Document
    {
        [JsonPropertyName("config_version")]
        public int ConfigVersion { get; set; } = ConfigurationStore.ConfigVersion;

        [JsonPropertyName("current")]
        public string? Current { get; set; }

        /// <summary>
        /// 序列化为对象形式 <c>{ "组名": {...} }</c>；反序列化兼容对象与数组两种写法。
        /// 用 Entry 列表承载是为了保留顺序。
        /// </summary>
        [JsonPropertyName("configurations")]
        [JsonConverter(typeof(EntryDictionaryConverter))]
        public List<Entry> Configurations { get; set; } = [];

        /// <summary>定时计划（P5.4）：与配置组同级，跨组共享。</summary>
        [JsonPropertyName("timers")]
        public List<Scheduling.TimerEntry>? Timers { get; set; }

        /// <summary>日志策略（P5.5）：与配置组同级，跨组共享。</summary>
        [JsonPropertyName("log")]
        public Logging.LogOptions? Log { get; set; }
    }

    /// <summary>
    /// 让 <see cref="Document.Configurations"/> 在 JSON 里表现为普通对象、在内存里保持有序列表。
    /// 读：对象 → 按出现顺序转列表；数组 → 直接反序列化。
    /// 写：列表 → 按顺序写对象。
    /// </summary>
    private sealed class EntryDictionaryConverter : JsonConverter<List<Entry>>
    {
        public override List<Entry> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var entries = new List<Entry>();

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    TaskConfiguration? group = JsonSerializer.Deserialize<TaskConfiguration>(ref reader, options);
                    if (group is not null)
                    {
                        entries.Add(new Entry { Key = group.Name, Value = group });
                    }
                }

                return entries;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return entries;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                string key = reader.GetString() ?? string.Empty;
                reader.Read();
                TaskConfiguration? group = JsonSerializer.Deserialize<TaskConfiguration>(ref reader, options);
                if (group is not null)
                {
                    entries.Add(new Entry { Key = key, Value = group });
                }
            }

            return entries;
        }

        public override void Write(Utf8JsonWriter writer, List<Entry> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (Entry entry in value)
            {
                writer.WritePropertyName(entry.Key);
                JsonSerializer.Serialize(writer, entry.Value, options);
            }

            writer.WriteEndObject();
        }
    }
}

/// <summary>导入结果。</summary>
public sealed class ImportResult
{
    /// <summary>成功导入的配置组名（已做去重名处理，可能与文件里的名字不同）。</summary>
    public List<string> Imported { get; } = [];

    /// <summary>非致命提示（格式降级、字段被忽略等）。</summary>
    public List<string> Errors { get; } = [];

    /// <summary>致命错误（文件不存在/解析失败/无配置组）。非 null 表示整体失败。</summary>
    public string? Error { get; set; }

    public bool Success => Error is null && Imported.Count > 0;
}
