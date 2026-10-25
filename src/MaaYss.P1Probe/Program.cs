using MaaYss.P1Probe;

// P1 链路验证控制台入口。
// 子命令：
//   engine [资源目录]  P1.2 引擎集成验证（默认资源目录为输出目录下的 test_resource）
//   pi [路径]          P1.3 PI V2 协议解析验证（默认解析 MaaYYs 的 interface.json）
//   win32 [路径] [--hwnd 0x..] [--click x,y] [--swipe x1,y1,x2,y2,ms]
//                      P1.4 Win32 控制链路验证（默认解析 MaaYYs 的 interface.json）
//   agent              P1.5 Agent 链路（待实现）
var command = args.Length > 0 ? args[0].ToLowerInvariant() : "smoke";

int exitCode = command switch
{
    "engine" => await ProbeEngine.RunAsync(args.Length > 1 ? args[1] : null),
    "pi" => await ProbePi.RunAsync(args.Length > 1 ? args[1] : null),
    "win32" => await ProbeWin32.RunAsync(args),
    _ => RunSmoke(),
};

return exitCode;

static int RunSmoke()
{
    Console.WriteLine($"MaaYss P1 Probe | {MaaYss.Engine.MaaYssEngine.Name}");
    return 0;
}
