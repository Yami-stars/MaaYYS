using System.Diagnostics;
using MaaYss.Engine;
using MaaYss.Engine.Native;

namespace MaaYss.P1Probe;

/// <summary>
/// P1.2 引擎集成验证：
/// 版本打印 → 运行库定位 → 资源 PostBundle/Wait → Tasker 绑定 → PostTask → 回调观测 → 清理。
/// 关于控制器：v5.13 源码实证 PipelineTask 主循环每次迭代先 screencap()，
/// 无控制器时截图恒空，将循环至 reco_timeout（默认 20s）后任务失败；
/// 且 MaaYYs 分发的运行库未包含 MaaDbgControlUnit.dll（Debug 控制器不可用）。
/// 故绑定 MaaNullController（自定义控制器扩展点，1x1 静态截图，无额外 DLL 依赖）。
/// 声明顺序保证 Tasker 先于 Controller 释放（Tasker 以裸指针持有 Controller）。
/// </summary>
internal static class ProbeEngine
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <param name="resourcePathArg">测试资源目录；为 null 时取输出目录下的 test_resource。</param>
    public static async Task<int> RunAsync(string? resourcePathArg)
    {
        Console.WriteLine("=== P1.2 引擎集成验证 ===");

        Console.WriteLine($"[1] MaaFramework 版本: {MaaFramework.Version}");
        Console.WriteLine($"[2] 运行库: {MaaFramework.NativeLibraryPath ?? "<未定位，走系统默认解析>"}");

        string resourceDir = resourcePathArg ?? Path.Combine(AppContext.BaseDirectory, "test_resource");
        if (!Directory.Exists(resourceDir))
        {
            Console.Error.WriteLine($"测试资源目录不存在: {resourceDir}");
            return 1;
        }

        // ---- 资源加载 ----
        using MaaResource resource = new();
        resource.EventReceived += (_, e) => Log("Resource", e);

        long resId = resource.PostBundle(resourceDir);
        Console.WriteLine($"[3] PostBundle({resourceDir}) -> id={resId}");
        if (resId == 0)
        {
            Console.Error.WriteLine("PostBundle 返回无效 id（路径无效或加载被拒）");
            return 1;
        }

        MaaStatus resStatus = resource.Wait(resId);
        Console.WriteLine($"[4] 资源加载结果: {resStatus}");
        if (resStatus != MaaStatus.Succeeded)
        {
            Console.Error.WriteLine("资源加载失败");
            return 1;
        }

        // ---- 控制器 + Tasker + 任务投递 ----
        using MaaNullController controller = MaaNullController.Create();
        using MaaTasker tasker = new();
        tasker.EventReceived += (_, e) => Log("Tasker", e);
        tasker.BindResource(resource);

        if (!tasker.BindController(controller.Handle))
        {
            Console.Error.WriteLine("BindController(MaaNullController) 失败");
            return 1;
        }

        (long taskId, MaaStatus status) = await RunTaskAsync(tasker);
        if (taskId == 0)
        {
            Console.Error.WriteLine("任务投递被拒（PostTask 返回 0）");
            return 1;
        }

        Console.WriteLine($"[6] 任务最终状态: {status}");
        bool ok = status == MaaStatus.Succeeded;
        Console.WriteLine(ok ? "=== P1.2 验证通过 ===" : "=== P1.2 验证失败 ===");
        return ok ? 0 : 2;
    }

    private static async Task<(long TaskId, MaaStatus Status)> RunTaskAsync(MaaTasker tasker)
    {
        long taskId = tasker.PostTask("TestTask");
        Console.WriteLine($"[5] PostTask(\"TestTask\") -> id={taskId}");
        if (taskId == 0)
        {
            return (0, MaaStatus.Invalid);
        }

        Stopwatch timeout = Stopwatch.StartNew();
        while (true)
        {
            MaaStatus status = tasker.Status(taskId);
            if (status is not (MaaStatus.Pending or MaaStatus.Running))
            {
                return (taskId, status);
            }

            if (timeout.Elapsed > TimeSpan.FromSeconds(30))
            {
                Console.Error.WriteLine("等待任务完成超时（30s）");
                tasker.PostStop();
                return (taskId, status);
            }

            await Task.Delay(50);
        }
    }

    private static void Log(string channel, MaaFrameworkEventArgs e)
    {
        string details = string.IsNullOrEmpty(e.DetailsJson) ? string.Empty : $" | {e.DetailsJson}";
        Console.WriteLine($"    [{Clock.Elapsed.TotalSeconds,7:F3}s] {channel}: {e.Message}{details}");
    }
}
