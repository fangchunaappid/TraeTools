using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace TraeTools.Services;

/// <summary>
/// 自绘无边框窗口在 Windows 上的原生细节：请求 DWM 圆角。
/// 只在 Windows 生效；Win10 等不支持该属性的系统会静默失败（返回非零错误码），不影响功能。
/// </summary>
public static class WindowChrome
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>按窗口状态决定圆角：最大化时不圆角（否则四角会露出底色）。</summary>
    public static void ApplyCorners(Window window)
        => ApplyCorners(window, window.WindowState != WindowState.Maximized);

    public static void ApplyCorners(Window window, bool rounded)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            int prefer = rounded ? DwmwcpRound : DwmwcpDoNotRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref prefer, sizeof(int));
        }
        catch { /* 不支持时忽略 */ }
    }
}
