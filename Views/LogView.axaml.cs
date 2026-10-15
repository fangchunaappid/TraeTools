using System;
using Avalonia.Controls;
using Avalonia.Threading;
using TraeTools.ViewModels;

namespace TraeTools.Views;

/// <summary>
/// 日志页视图。
///
/// 唯一需要代码配合的地方是「自动滚动」：VM 不知道滚动位置，
/// 因此当它追加了新日志（或新打开了日志文件）时会触发 <see cref="LogViewModel.ScrollToEndRequested"/>，
/// 本视图收到后把对应的 ScrollViewer 滚到底部。
///
/// 滚动必须延迟到布局完成之后执行：若在集合变更的同一帧内调用 ScrollToEnd，
/// 新增行尚未参与测量，Extent 还是旧值，滚动会停在倒数第二行附近。
/// 这里用 DispatcherPriority.Background 排队，等本帧布局结束再滚动。
/// </summary>
public partial class LogView : UserControl
{
    private LogViewModel? _vm;

    public LogView()
    {
        InitializeComponent();

        // 用 lambda 订阅，避免显式书写事件参数类型
        // （DetachedFromVisualTree 的参数类型命名空间在各版本间有过调整）
        DataContextChanged += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => Release();
    }

    /// <summary>DataContext 变化时重新挂接：ViewModel 由 MainViewModel 长期持有，视图可能被多次创建。</summary>
    private void Rebind()
    {
        Release();
        _vm = DataContext as LogViewModel;
        if (_vm != null) _vm.ScrollToEndRequested += OnScrollToEndRequested;
    }

    /// <summary>离开可视树时退订，避免静态/长生命周期对象持有视图造成泄漏。</summary>
    private void Release()
    {
        if (_vm != null) _vm.ScrollToEndRequested -= OnScrollToEndRequested;
        _vm = null;
    }

    private void OnScrollToEndRequested()
    {
        try
        {
            // 等本帧布局完成再滚：否则新增行的 Extent 尚未更新，滚不到底
            Dispatcher.UIThread.Post(ScrollNow, DispatcherPriority.Background);
        }
        catch { /* 视图异常不影响日志功能 */ }
    }

    private void ScrollNow()
    {
        try
        {
            var target = _vm?.IsRecentTab == true
                ? this.FindControl<ScrollViewer>("RecentScroll")
                : this.FindControl<ScrollViewer>("FileScroll");
            target?.ScrollToEnd();
        }
        catch { /* 忽略 */ }
    }
}
