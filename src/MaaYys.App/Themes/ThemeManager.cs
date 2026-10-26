using System.Windows;

namespace MaaYys.App.Themes;

public enum ThemeKind
{
    Light,
    Dark
}

/// <summary>
/// 主题切换：整体替换 Application 级资源字典（明/暗两套键完全对齐）。
/// P2.1 提供手动切换，P6.4 接入配置持久化与「跟随系统」。
/// </summary>
public static class ThemeManager
{
    // 程序集名已设为 MaaYys（AssemblyName），pack URI 必须用该名字，而非项目名 MaaYys.App
    private const string LightSource = "pack://application:,,,/MaaYys;component/Themes/Theme.Light.xaml";
    private const string DarkSource = "pack://application:,,,/MaaYys;component/Themes/Theme.Dark.xaml";

    public static ThemeKind Current { get; private set; } = ThemeKind.Dark;

    public static void Apply(ThemeKind kind)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var target = new ResourceDictionary { Source = new Uri(kind == ThemeKind.Dark ? DarkSource : LightSource, UriKind.Absolute) };

        // 移除上一套主题字典，再插入新的（保证同一时刻只有一套生效）
        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var src = app.Resources.MergedDictionaries[i].Source?.OriginalString ?? string.Empty;
            if (src.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                app.Resources.MergedDictionaries.RemoveAt(i);
            }
        }

        app.Resources.MergedDictionaries.Add(target);
        Current = kind;
    }

    public static ThemeKind Toggle()
    {
        var next = Current == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark;
        Apply(next);
        return next;
    }
}
