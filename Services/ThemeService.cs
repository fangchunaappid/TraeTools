using Avalonia;
using Avalonia.Styling;

namespace TraeTools.Services;

/// <summary>
/// 主题模式：把配置里的三态字符串 <c>ThemeMode</c> 与 Avalonia 的
/// <see cref="ThemeVariant"/> 对应起来，并负责在启动时应用到当前应用。
///
/// 配色本身只存在于 <c>Styles/AppTheme.axaml</c> 的 ThemeDictionaries（Light/Dark 两套取值），
/// 本类不做任何颜色定义 —— 切换 <see cref="Application.RequestedThemeVariant"/> 即全局生效。
/// </summary>
public static class ThemeService
{
    /// <summary>可选的三种模式（界面下拉直接使用）。</summary>
    public static readonly string[] Modes = { "system", "light", "dark" };

    /// <summary>把任意输入规整为合法模式；非法值回退 "system"。</summary>
    public static string Normalize(string? mode) => mode switch
    {
        "light" or "dark" or "system" => mode,
        _ => "system",
    };

    /// <summary>模式字符串 → Avalonia 主题变体。</summary>
    public static ThemeVariant ToVariant(string? mode) => Normalize(mode) switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,   // 跟随系统
    };

    /// <summary>当前生效的模式字符串（从应用实际状态反推，而非读配置）。</summary>
    public static string Current => Application.Current?.RequestedThemeVariant switch
    {
        var v when v == ThemeVariant.Light => "light",
        var v when v == ThemeVariant.Dark => "dark",
        _ => "system",
    };

    /// <summary>
    /// 把配置里的模式应用到当前应用。启动时调用一次即可；
    /// 设置页切换主题后再次调用本方法即时生效。必须在 UI 线程调用。
    /// </summary>
    public static void Apply(string? mode)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = ToVariant(mode);
    }
}
