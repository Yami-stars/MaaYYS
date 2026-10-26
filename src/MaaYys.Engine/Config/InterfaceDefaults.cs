using System.Text.Json.Serialization;

namespace MaaYys.Engine.Config;

/// <summary>
/// interface.json <c>controller[].win32</c> 段解析出的默认值（P3.6「Win32 参数可配置化」）。
/// <para>
/// 之前这些参数只是被解析出来<b>打印</b>，真正连控制器时用的是代码里的常量（FramePool + SendMessage）。
/// 结果就是「改 interface.json 的 win32 段不生效」——P3.6 把它补上：清单里的值是<b>默认值</b>，
/// 实例自己的字段（实例页可编辑）优先，实例没填才回落到这里。
/// </para>
/// </summary>
public sealed record Win32Defaults(
    string? TitleRegex,
    string? ClassRegex,
    string? Screencap,
    string? Mouse,
    string? Keyboard)
{
    /// <summary>从清单里取第一个 Win32 控制器的默认值；没有 Win32 控制器则为 null。</summary>
    public static Win32Defaults? From(InterfaceConfig? config)
    {
        ControllerConfig? controller = config?.Controllers
            .FirstOrDefault(c => string.Equals(c.Type, "Win32", StringComparison.Ordinal));
        if (controller?.Win32 is not { } win32)
        {
            return null;
        }

        return new Win32Defaults(
            NullIfEmpty(win32.WindowRegex),
            NullIfEmpty(win32.ClassRegex),
            NullIfEmpty(win32.Screencap),
            NullIfEmpty(win32.Mouse),
            NullIfEmpty(win32.Keyboard));
    }

    [JsonIgnore]
    public ulong ScreencapMethod => MaaWin32MethodNames.ParseScreencap(Screencap);

    [JsonIgnore]
    public ulong MouseMethod => MaaWin32MethodNames.ParseInput(Mouse);

    [JsonIgnore]
    public ulong KeyboardMethod => MaaWin32MethodNames.ParseInput(Keyboard);

    public override string ToString()
        => $"窗口={TitleRegex ?? "—"} 类名={ClassRegex ?? "—"} "
           + $"截图={Screencap ?? "FramePool"} 鼠标={Mouse ?? "SendMessage"} 键盘={Keyboard ?? "SendMessage"}";

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
