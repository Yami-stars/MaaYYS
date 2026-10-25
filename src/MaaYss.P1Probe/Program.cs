using System.Runtime.InteropServices;
using MaaYss.P1Probe;

// P1 链路验证控制台入口。
// 子命令：
//   engine [资源目录]  P1.2 引擎集成验证（默认资源目录为输出目录下的 test_resource）
//   pi [路径]          P1.3 PI V2 协议解析验证（默认解析 MaaYYs 的 interface.json）
//   win32 [路径] [--hwnd 0x..] [--click x,y] [--swipe x1,y1,x2,y2,ms]
//                      P1.4 Win32 控制链路验证（默认解析 MaaYYs 的 interface.json）
//   agent [--port N] [--agent 路径] [--libdir 路径]
//                      P1.5 Agent 链路验证（拉起 Go agent 子进程并调用其 custom action/recognition）

// DPI 感知设置：阴阳师桌面版为 DPI 感知进程（物理客户端 1431x805），
// 若本进程 DPI 无感知，Seize 等依赖 ClientToScreen 的输入方法会因坐标虚拟化（125% 缩放）而整体偏移。
// 必须在任何窗口/DPI 相关调用前设置，且每进程仅一次生效。
SetProcessDpiAwareness();

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "smoke";

int exitCode = command switch
{
    "engine" => await ProbeEngine.RunAsync(args.Length > 1 ? args[1] : null),
    "pi" => await ProbePi.RunAsync(args.Length > 1 ? args[1] : null),
    "win32" => await ProbeWin32.RunAsync(args),
    "agent" => await ProbeAgent.RunAsync(args),
    _ => RunSmoke(),
};

return exitCode;

static int RunSmoke()
{
    Console.WriteLine($"MaaYss P1 Probe | {MaaYss.Engine.MaaYssEngine.Name}");
    return 0;
}

static void SetProcessDpiAwareness()
{
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4（Win10 1703+）
    if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
    {
        Console.WriteLine("[DPI] PerMonitorV2 感知已启用");
        return;
    }

    // 回退：系统级 DPI 感知（Win7+）
    if (SetProcessDPIAware())
    {
        Console.WriteLine("[DPI] 系统级感知已启用（回退）");
        return;
    }

    Console.WriteLine("[DPI] 设置失败（可能已由其他方式设置或系统不支持）");
}

[DllImport("user32.dll")]
static extern bool SetProcessDpiAwarenessContext(IntPtr value);

[DllImport("user32.dll")]
static extern bool SetProcessDPIAware();
