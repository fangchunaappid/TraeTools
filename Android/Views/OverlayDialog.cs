using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace TraeTools.Views;

/// <summary>
/// 覆盖层模态对话框宿主（Android）。
///
/// <para><b>为什么必须有这一层</b>：Avalonia 的 Android 后端是单视图平台，
/// <c>new Window()</c> 会在 <c>Avalonia.Android.WindowingPlatformStub.CreateWindow()</c>
/// 抛 <c>NotSupportedException</c>，所以整个移植里所有对话框内容都写成 <see cref="UserControl"/>，
/// 由本类把它们挂到主视图的全屏覆盖层上呈现，并用 <see cref="TaskCompletionSource{TResult}"/>
/// 模拟「模态 + 返回值」的语义。</para>
///
/// <para><b>与上游 VM 的衔接</b>：上游 ViewModel 调用的是 <c>dlg.ShowDialog&lt;bool&gt;(owner)</c>
/// （<c>Window</c> 的实例方法）。<c>Android\Views\</c> 下的对话框类因此都额外提供了一个
/// 同名的 <c>ShowDialog&lt;TResult&gt;(TopLevel? owner)</c> 方法，内部转调本类 ——
/// 于是共享 ViewModel 一行都不用改。</para>
/// </summary>
public static class OverlayDialog
{
    private static Panel? _root;
    private static ContentPresenter? _host;
    private static UserControl? _current;
    private static TaskCompletionSource<object?>? _tcs;

    /// <summary>由主视图在构造时挂接覆盖层容器。</summary>
    public static void Attach(Panel root, ContentPresenter host)
    {
        _root = root;
        _host = host;
    }

    /// <summary>
    /// 显示对话框并等待其关闭，返回值由 <see cref="CloseDialog"/> 传入。
    /// <paramref name="owner"/> 只是为了与 <c>Window.ShowDialog(owner)</c> 的调用形态对齐 ——
    /// 覆盖层是全屏的，不需要 owner，允许为 null。
    /// </summary>
    public static async Task<T> ShowAsync<T>(UserControl dialog, TopLevel? owner = null)
    {
        try
        {
            var result = await ShowCore(dialog).Task;
            return result is T typed ? typed : default!;
        }
        catch (Exception ex)
        {
            // 呈现失败时降级返回默认值：绝不让对话框问题带崩应用（Android 上即闪退），
            // 调用方拿到 false/null 会按「用户取消」处理。
            Platform.CrashLog.Write("OverlayDialog.ShowAsync", ex);
            return default!;
        }
    }

    /// <summary>对话框按钮的统一关闭入口（各对话框的 Click 处理里调用）。</summary>
    public static void CloseDialog(UserControl dialog, object? result) => Finish(dialog, result);

    private static TaskCompletionSource<object?> ShowCore(UserControl dialog)
    {
        if (_root == null || _host == null)
            throw new InvalidOperationException("OverlayDialog 尚未 Attach（主视图未就绪）");
        if (_tcs != null)
            throw new InvalidOperationException("已有对话框在显示中，不允许叠加");

        // 手机屏宽只有桌面端的 1/3：清掉为桌面端写死的尺寸约束，
        // 让对话框横向铺满覆盖层、纵向由各自的 VerticalAlignment 决定
        //（确认框 Center 只占内容高度；EULA 这类长内容用 Stretch 吃满可用高度并在内部滚动）。
        try
        {
            dialog.Width = double.NaN;
            dialog.MinWidth = 0;
            dialog.MinHeight = 0;
            dialog.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        }
        catch
        {
            // 尺寸调整失败不影响显示
        }

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tcs = tcs;
        _current = dialog;
        _host.Content = dialog;
        _root.IsVisible = true;
        return tcs;
    }

    private static void Finish(UserControl dialog, object? result)
    {
        if (!ReferenceEquals(_current, dialog) || _tcs == null || _host == null || _root == null)
            return;

        _host.Content = null;
        _root.IsVisible = false;

        var tcs = _tcs;
        _tcs = null;
        _current = null;
        tcs.TrySetResult(result);
    }
}
