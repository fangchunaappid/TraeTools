using System;
using Avalonia;
using Avalonia.Styling;

namespace TraeTools.Services;

/// <summary>
/// 主题模式：把配置里的三态字符串 <c>ThemeMode</c>（system / light / dark）与 Avalonia 的
/// <see cref="ThemeVariant"/> 对应起来，并负责应用到当前应用。
/// 配色本体只存在于 <c>Styles/AppTheme.axaml</c> 的 ThemeDictionaries（Light/Dark 两套取值），
/// 视图统一用 <c>{DynamicResource 键名}</c> 引用，因此切换后无需重建窗口即可实时变色。
/// </summary>
public static class ThemeService
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>可选的三种模式（设置页下拉直接使用）。</summary>
    public static readonly string[] Modes = { System, Light, Dark };

    /// <summary>把任意输入规整为合法模式；非法值回退 "system"。</summary>
    public static string Normalize(string? mode) => mode switch
    {
        Light or Dark or System => mode,
        _ => System,
    };

    /// <summary>模式字符串 → Avalonia 主题变体。</summary>
    public static ThemeVariant ToVariant(string? mode) => Normalize(mode) switch
    {
        Light => ThemeVariant.Light,
        Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,   // 跟随系统
    };

    /// <summary>当前生效的模式字符串（从应用实际状态反推，而非读配置）。</summary>
    public static string Current => Application.Current?.RequestedThemeVariant switch
    {
        var v when v == ThemeVariant.Light => Light,
        var v when v == ThemeVariant.Dark => Dark,
        _ => System,
    };

    /// <summary>
    /// 当前是否深色：按应用**实际生效**的变体判断（RequestedThemeVariant=Default 时由系统解析），
    /// 供标题栏 ☾/☀ 图标使用。
    /// </summary>
    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>应用主题。mode 为空或非法时按「跟随系统」处理。必须在 UI 线程调用。</summary>
    public static void Apply(string? mode)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = ToVariant(mode);
    }

    /// <summary>bool → 模式字符串（标题栏快捷切换 / 持久化用）。</summary>
    public static string ToMode(bool dark) => dark ? Dark : Light;
}
