using System.Runtime.InteropServices;
using MaaYys.P1Probe;

// P1 链路验证控制台入口。
// 子命令：
//   engine [资源目录]  P1.2 引擎集成验证（默认资源目录为输出目录下的 test_resource）
//   pi [路径]          P1.3 PI V2 协议解析验证（默认解析 MaaYYs 的 interface.json）
//   win32 [路径] [--hwnd 0x..] [--click x,y] [--swipe x1,y1,x2,y2,ms]
//                      P1.4 Win32 控制链路验证（默认解析 MaaYYs 的 interface.json）
//   agent [--port N] [--agent 路径] [--libdir 路径]
//                      P1.5 Agent 链路验证（拉起 Go agent 子进程并调用其 custom action/recognition）
//   devices [--regex 阴阳师] [--fps 10] [--seconds 3]
//                      P2.3 设备探测 + 实时帧采集验证（Win32 / ADB）
//   window [--process MaaYys] [--rounds 8]
//                      GUI 窗口行为诊断（最小化 → 模拟任务栏恢复 → 采样前台与 UI 响应）
//   minimize-loop [--process MaaYys] [--cycles 20] [--gap 900]
//                      最小化/恢复压力测试（逐轮判定恢复后客户区内容是否真的画出来了）
//   inspect [--process onmyoji] [--shot out.bmp]
//                      只读窗口状态诊断（几何/可见性/DWM cloak/扩展样式/中心命中/z-order）
//   resource [--root 目录] [--set 资源集] [--switch 目标资源集]
//                      P2.5 资源链加载与渠道热切换验证（不依赖游戏窗口）
//   config [--path maayys.json] [--add 名字] [--task "组|任务"] [--set 名字] [--migrate instances.json]
//                      P2.6 配置组持久化与 P2.4 → P2.6 迁移验证
//   pipeline [--root 目录] [--set 资源集] [--layer] [--verify]
//                      P3.1 pipeline 静态自检（JSON 可解析、引用不悬空、模板图不缺）
//                      --verify 额外用真实资源加载核对框架侧可见节点
//   audit [--root 目录] [--set 资源集] [--json out.json] [--no-assets]
//         [--config 目录] [--prune] [--state 状态文件]
//                      资源审计：不可达节点 / 死文件 / 未引用模板与模型 / 配置备份
//                      默认只扫描；--prune 才执行（多资源集取交集，直接删除、不进回收区）
//   cache [--root 程序目录] [--clear]
//                      图片缓存（debug/ 与 cache/ 下的图片）：默认只看占用，--clear 清空
//   channel [--root 目录] [--set 资源集] [--verify]
//                      P3.5 渠道服分层：静态拆出渠道层的差异，--verify 逐个资源集真实加载核对
//   tasks [--root 目录] [--task 任务名] [--value "选项=取值"] [--json]
//                      P3.4 任务定义与选项：列出 29 个任务的参数面板 / 打印编译后的 pipeline_override

// DPI 感知设置：阴阳师桌面版为 DPI 感知进程（物理客户端 1431x805），
// 若本进程 DPI 无感知，Seize 等依赖 ClientToScreen 的输入方法会因坐标虚拟化（125% 缩放）而整体偏移。
// 必须在任何窗口/DPI 相关调用前设置，且每进程仅一次生效。
// 输出统一 UTF-8：窗口标题等含中文，按默认 OEM 码页输出会在终端里变乱码
Console.OutputEncoding = System.Text.Encoding.UTF8;
SetProcessDpiAwareness();

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "smoke";

int exitCode = command switch
{
    "engine" => await ProbeEngine.RunAsync(args.Length > 1 ? args[1] : null),
    "pi" => args.Contains("--validate")
        ? ProbePi.Validate(args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Skip(1).FirstOrDefault())
        : await ProbePi.RunAsync(args.Length > 1 ? args[1] : null),
    "win32" => await ProbeWin32.RunAsync(args),
    "agent" => await ProbeAgent.RunAsync(args),
    "devices" => await ProbeDevices.RunAsync(args),
    "window" => ProbeWindow.Run(args),
    "minimize-loop" => ProbeWindow.RunMinimizeLoop(args),
    "minloop" => ProbeWindow.RunMinimizeLoop(args),
    "inspect" => ProbeInspect.Run(args),
    "resource" => await ProbeResource.RunAsync(args),
    "config" => ProbeConfig.Run(args),
    "pipeline" => await ProbePipeline.RunAsync(args),
    "audit" => ProbeAudit.Run(args),
    "cache" => ProbeAudit.Cache(args),
    "channel" => await ProbeChannel.RunAsync(args),
    "tasks" => ProbeTasks.Run(args),
    _ => RunSmoke(),
};

return exitCode;

static int RunSmoke()
{
    Console.WriteLine($"MaaYys P1 Probe | {MaaYys.Engine.MaaYysEngine.Name}");
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
