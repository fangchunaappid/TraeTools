namespace TraeCheckin;

/// <summary>
/// 开机自启动（Android 实现）。
///
/// <para>Windows 版通过注册表 <c>HKCU\...\CurrentVersion\Run</c> 注册 exe 路径；
/// Android 在应用层面<b>没有等价的、无需额外权限的通用机制</b>：
/// <list type="bullet">
/// <item><c>BOOT_COMPLETED</c> 广播 + 前台服务需要 <c>RECEIVE_BOOT_COMPLETED</c> 权限，
///       且 Android 12+ 起后台启动前台服务受限，Play 政策也不允许「静默常驻」；</item>
/// <item>本应用真正的「无人值守签到」场景由「云端签到」承担
///       （GitHub Actions 每天定时跑 checkin.py，不依赖手机开机）。</item>
/// </list>
/// 因此这里保留与原实现完全一致的 API，但固定返回「未启用 / 设置无效」，
/// 让设置页能如实展示「本平台不支持」，而不是静默假装成功。</para>
/// </summary>
internal static class AutoStartManager
{
    /// <summary>是否已注册开机自启动 —— Android 恒为 false。</summary>
    public static bool IsEnabled() => false;

    /// <summary>开启或关闭开机自启动 —— Android 为空操作（无注册表可写）。</summary>
    public static void SetEnabled(bool enabled)
    {
        // 有意留空：调用方（设置页）会依据 IsEnabled() 的返回值把开关复位并给出提示。
    }
}
