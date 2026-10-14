using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace TraeTools.ViewModels;

/// <summary>
/// 解析当前可用于承载对话框的宿主，供页面 VM 弹模态与访问剪贴板 / 文件选择器。
///
/// <para><b>关键设计（整个 Android 移植能「零改动共享 VM」的原因）</b>：
/// 上游桌面版的 <c>MainWindow</c> 属性声明为 <c>Window?</c>，各 VM 的用法只有四种 ——</para>
/// <list type="bullet">
/// <item><c>dlg.ShowDialog&lt;T&gt;(owner)</c>：把 owner 透传给对话框；</item>
/// <item><c>owner != null ? … : …</c>：判空守卫；</item>
/// <item><c>top?.Clipboard</c>：复制 Token / 授权码；</item>
/// <item><c>win.StorageProvider</c>：文件选择器。</item>
/// </list>
/// <para>这四者全部是 <see cref="TopLevel"/> 的成员，而 <c>Window</c> 只是 <c>TopLevel</c> 的子类。
/// 上游 VM 一律用 <c>var</c> 接收该属性，因此这里把返回类型<b>放宽成 <c>TopLevel?</c></b> 后：</para>
/// <list type="bullet">
/// <item>上游 VM 一行都不用改；</item>
/// <item>Android 单视图平台也能拿到非 null 的宿主（单视图的根视图反查 TopLevel），
///       <c>owner != null</c> 的守卫照样通过，弹窗与剪贴板全部可用。</item>
/// </list>
/// <para>若这里坚持返回 <c>Window?</c>，Android 上恒为 null，
/// 上游那些 <c>owner != null ? await … : false</c> 的确认框会被整体跳过（功能性缺失）。</para>
/// </summary>
internal static class UiHost
{
    /// <summary>
    /// 对话框宿主 / 剪贴板来源。桌面端是主窗口，Android 是承载单视图的根 TopLevel；
    /// 尚未挂载到视觉树时返回 null（调用方已有判空守卫）。
    /// </summary>
    public static TopLevel? MainWindow => Resolve();

    private static TopLevel? Resolve()
    {
        try
        {
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime;

            // 桌面端（本工程不编译该分支，保留以便共享代码语义完整）
            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.MainWindow;

            // Android：单视图生命周期，从根视图反查 TopLevel
            if (lifetime is ISingleViewApplicationLifetime singleView && singleView.MainView is Control root)
                return TopLevel.GetTopLevel(root);
        }
        catch
        {
            // 视觉树尚未就绪 / 生命周期切换中：返回 null，由调用方的守卫兜底
        }
        return null;
    }
}
