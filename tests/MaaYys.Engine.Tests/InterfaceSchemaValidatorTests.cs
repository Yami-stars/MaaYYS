using MaaYys.Engine.Config;
using MaaYys.Engine.Native;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// P3.6 interface.json 的 PI V2 校验器测试。
/// <para>
/// 锁住三组判定：
/// <list type="bullet">
/// <item><b>必填与类型</b>：interface_version / name / controller / resource / task / option 的类型。</item>
/// <item><b>跨段引用</b>：task.group、task.controller、resource.controller、case.option 指向必须存在。</item>
/// <item><b>Win32 参数</b>：窗口正则至少要有一个；键鼠/截图方式写错名字必须报 Error
/// （MaaFramework 对未知名字是静默回退，不拦就会悄悄改变行为）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class InterfaceSchemaValidatorTests : IDisposable
{
    private readonly string _root;

    public InterfaceSchemaValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maayys-pi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteTask(string name, string json) => Write(Path.Combine("tasks", name), json);

    /// <summary>最小可用清单：1 个 Win32 控制器 + 1 个资源集 + 1 个任务。</summary>
    private string Minimal()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pack"));
        WriteTask("main.json", """
        { "task": [ { "name": "主线", "entry": "A", "group": ["daily"] } ] }
        """);
        Write("interface.json", """
        {
          "interface_version": 2,
          "name": "pi-test",
          "version": "v1.0.0",
          "controller": [
            { "name": "Win32", "type": "Win32",
              "win32": { "window_regex": "阴阳师", "class_regex": "Win32Window",
                         "mouse": "SendMessage", "keyboard": "SendMessage", "screencap": "FramePool" } }
          ],
          "resource": [ { "name": "测试服", "path": ["./pack"] } ],
          "group": [ { "name": "daily", "label": "日常" } ],
          "import": [ "tasks/main.json" ]
        }
        """);
        return Path.Combine(_root, "interface.json");
    }

    private InterfaceSchemaReport Validate(string path)
    {
        List<string> loadErrors = [];
        InterfaceConfig config = InterfaceConfig.Load(path, loadErrors);
        return InterfaceSchemaValidator.Validate(config, loadErrors);
    }

    // ---- 通过 ----

    [Fact]
    public void 最小合法清单通过校验()
    {
        InterfaceSchemaReport report = Validate(Minimal());

        Assert.True(report.IsValid, string.Join(" | ", report.Diagnostics.Select(d => d.ToString())));
        Assert.Equal(0, report.ErrorCount);
    }

    // ---- 必填与类型 ----

    [Fact]
    public void InterfaceVersion不是2会报错()
    {
        string path = Minimal();
        Write("interface.json", File.ReadAllText(path).Replace("\"interface_version\": 2", "\"interface_version\": 1"));

        Assert.False(Validate(path).IsValid);
    }

    [Fact]
    public void 控制器类型非法会报错()
    {
        string path = Minimal();
        Write("interface.json", File.ReadAllText(path).Replace("\"type\": \"Win32\"", "\"type\": \"Whatever\""));

        Assert.False(Validate(path).IsValid);
    }

    [Fact]
    public void Option类型非法会报错()
    {
        string path = Minimal();
        WriteTask("main.json", """
        {
          "task": [ { "name": "主线", "entry": "A", "option": ["opt"] } ],
          "option": { "opt": { "type": "slider", "label": "滑杆" } }
        }
        """);

        InterfaceSchemaReport report = Validate(path);

        Assert.Contains(report.Diagnostics,
            d => d.Level == PiDiagnosticLevel.Error && d.Path.Contains("option[opt]", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultCase指向不存在的Case会报错()
    {
        string path = Minimal();
        WriteTask("main.json", """
        {
          "task": [ { "name": "主线", "entry": "A", "option": ["opt"] } ],
          "option": {
            "opt": { "type": "switch", "default_case": "不存在的",
                     "cases": [ { "name": "Yes" } ] }
          }
        }
        """);

        InterfaceSchemaReport report = Validate(path);

        Assert.Contains(report.Diagnostics,
            d => d.Level == PiDiagnosticLevel.Error && d.Path.Contains("default_case", StringComparison.Ordinal));
    }

    // ---- 跨段引用 ----

    [Fact]
    public void 任务引用不存在的分组会报错()
    {
        string path = Minimal();
        WriteTask("main.json", """
        { "task": [ { "name": "主线", "entry": "A", "group": ["不存在的组"] } ] }
        """);

        InterfaceSchemaReport report = Validate(path);

        Assert.Contains(report.Diagnostics,
            d => d.Level == PiDiagnosticLevel.Error && d.Message.Contains("不存在的组", StringComparison.Ordinal));
    }

    [Fact]
    public void 资源集引用不存在的控制器会报错()
    {
        string path = Minimal();
        Write("interface.json", File.ReadAllText(path)
            .Replace("\"path\": [\"./pack\"]", "\"path\": [\"./pack\"], \"controller\": [\"没有这个控制器\"]"));

        Assert.False(Validate(path).IsValid);
    }

    [Fact]
    public void 定义了却没任务用的分组只给警告()
    {
        string path = Minimal();
        WriteTask("main.json", """{ "task": [ { "name": "主线", "entry": "A" } ] }""");

        InterfaceSchemaReport report = Validate(path);

        Assert.True(report.IsValid);
        Assert.Contains(report.Diagnostics,
            d => d.Level == PiDiagnosticLevel.Warning && d.Path.Contains("group[daily]", StringComparison.Ordinal));
    }

    // ---- Win32 参数 ----

    [Fact]
    public void Win32没有窗口正则会报错()
    {
        string path = Minimal();
        Write("interface.json", File.ReadAllText(path)
            .Replace("\"window_regex\": \"阴阳师\", \"class_regex\": \"Win32Window\",", string.Empty));

        InterfaceSchemaReport report = Validate(path);

        Assert.Contains(report.Diagnostics,
            d => d.Level == PiDiagnosticLevel.Error && d.Path.Contains("win32", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("screencap", "ScreenCapture")]
    [InlineData("mouse", "Telepathy")]
    public void 键鼠截图方式写错名字会报错(string field, string value)
    {
        string path = Minimal();
        string text = File.ReadAllText(path);
        string original = field == "screencap" ? "\"screencap\": \"FramePool\"" : "\"mouse\": \"SendMessage\"";
        Write("interface.json", text.Replace(original, $"\"{field}\": \"{value}\""));

        InterfaceSchemaReport report = Validate(path);

        Assert.Contains(report.Diagnostics,
            d => d.Level == PiDiagnosticLevel.Error && d.Path.Contains($"win32.{field}", StringComparison.Ordinal));
    }

    [Fact]
    public void PrintWindow只给警告不算错误()
    {
        string path = Minimal();
        Write("interface.json", File.ReadAllText(path).Replace("\"FramePool\"", "\"PrintWindow\""));

        InterfaceSchemaReport report = Validate(path);

        Assert.True(report.IsValid);
        Assert.Contains(report.Diagnostics,
            d => d.Level == PiDiagnosticLevel.Warning && d.Message.Contains("黑边", StringComparison.Ordinal));
    }

    // ---- 路径存在性 ----

    [Fact]
    public void 资源目录不存在会报错()
    {
        string path = Minimal();
        Write("interface.json", File.ReadAllText(path).Replace("\"./pack\"", "\"./不存在的目录\""));

        InterfaceSchemaReport report = Validate(path);

        Assert.Contains(report.Diagnostics,
            d => d.Level == PiDiagnosticLevel.Error && d.Message.Contains("目录不存在", StringComparison.Ordinal));
    }

    // ---- 默认值解析（P3.6 Win32 参数可配置化）----

    [Fact]
    public void 能解析出Win32默认值()
    {
        List<string> errors = [];
        InterfaceConfig config = InterfaceConfig.Load(Minimal(), errors);

        Win32Defaults defaults = Win32Defaults.From(config)!;

        Assert.Equal("阴阳师", defaults.TitleRegex);
        Assert.Equal("Win32Window", defaults.ClassRegex);
        Assert.Equal(MaaWin32ScreencapMethod.FramePool, defaults.ScreencapMethod);
        Assert.Equal(MaaWin32InputMethod.SendMessage, defaults.MouseMethod);
    }

    [Fact]
    public void 没有Win32控制器时默认值为Null()
    {
        string path = Minimal();
        Write("interface.json", File.ReadAllText(path).Replace("\"type\": \"Win32\"", "\"type\": \"Adb\""));
        List<string> errors = [];
        InterfaceConfig config = InterfaceConfig.Load(path, errors);

        Assert.Null(Win32Defaults.From(config));
    }
}
