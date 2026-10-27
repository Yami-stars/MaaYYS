using System.IO;
using System.Reflection;
using MaaYys.App.Services;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 关于（P6.2）：程序版本、运行库版本与三类清单路径。
/// 只读 —— 「为什么资源没更新」这类问题第一个要看的就是这三条路径指到哪儿了。
/// </summary>
public sealed class AboutViewModel : SettingsSectionViewModel
{
    public AboutViewModel(InstanceWorkspace? workspace)
        : base(workspace)
    {
    }

    /// <summary>程序版本（程序集版本，取三段）。</summary>
    public string AppVersion
    {
        get
        {
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "未知" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    /// <summary>MaaFramework 运行库版本（缺库时降级为提示，不让设置页崩）。</summary>
    public string FrameworkVersion
    {
        get
        {
            try
            {
                return MaaYys.Engine.MaaFramework.Version;
            }
            catch (Exception ex)
            {
                return $"不可用（{ex.Message}）";
            }
        }
    }

    /// <summary>配置清单路径（<c>config/maayys.json</c>）。</summary>
    public string ConfigPath => Workspace?.Configurations.Path ?? "（未加载）";

    /// <summary>实例清单路径。</summary>
    public string InstanceStorePath => Workspace?.Store.Path ?? "（未加载）";

    /// <summary>资源清单路径。</summary>
    public string InterfacePath => Workspace?.Resources.InterfacePath ?? "（未找到 resource/interface.json）";

    /// <summary>程序根目录。</summary>
    public string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>配置文件是否已落盘（没落盘说明改设置只改了内存）。</summary>
    public string ConfigState => Workspace is null
        ? "工作区未加载"
        : File.Exists(ConfigPath) ? "已落盘" : "尚未生成（首次保存设置时创建）";
}
