using MaaYss.P1Probe;

// P1 链路验证控制台入口。
// 子命令：
//   engine [资源目录]  P1.2 引擎集成验证（默认资源目录为输出目录下的 test_resource）
//   pi                P1.3 PI V2 协议解析（待实现）
//   win32             P1.4 Win32 控制链路（待实现）
//   agent             P1.5 Agent 链路（待实现）
var command = args.Length > 0 ? args[0].ToLowerInvariant() : "smoke";

int exitCode = command switch
{
    "engine" => await ProbeEngine.RunAsync(args.Length > 1 ? args[1] : null),
    _ => RunSmoke(),
};

return exitCode;

static int RunSmoke()
{
    Console.WriteLine($"MaaYss P1 Probe | {MaaYss.Engine.MaaYssEngine.Name}");
    return 0;
}
