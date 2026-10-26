using MaaYys.Engine;

namespace MaaYys.App.ViewModels;

/// <summary>首页：展示引擎/运行库状态（P2.3 起扩展为连接面板 + 实时截图）。</summary>
public sealed class HomeViewModel : ViewModelBase
{
    public string EngineStatus { get; }
    public string RuntimeVersion { get; }
    public string RuntimePath { get; }

    public HomeViewModel()
    {
        // 运行库缺失不应导致 GUI 崩溃，降级为提示文本
        try
        {
            RuntimeVersion = MaaFramework.Version;
            RuntimePath = MaaFramework.NativeLibraryPath ?? "(未找到 MaaFramework.dll)";
            EngineStatus = "就绪";
        }
        catch (Exception ex)
        {
            RuntimeVersion = "-";
            RuntimePath = ex.Message;
            EngineStatus = "未就绪";
        }
    }
}
