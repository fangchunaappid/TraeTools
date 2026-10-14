using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TraeTools.Views;

/// <summary>
/// 首次启动的最终用户许可协议对话框。
/// <c>ShowDialog&lt;bool&gt;</c> 返回值：true=同意并继续，false=不同意并退出。
/// </summary>
public partial class EulaWindow : UserControl
{
    public EulaWindow()
    {
        InitializeComponent();
        // 勾选同意后才允许点「同意并继续」
        AgreeCheck.IsCheckedChanged += (_, _) =>
        {
            AgreeButton.IsEnabled = AgreeCheck.IsChecked == true;
        };
    }

    /// <summary>与 <c>Window.ShowDialog&lt;TResult&gt;(Window owner)</c> 对齐的入口。</summary>
    public Task<TResult> ShowDialog<TResult>(TopLevel? owner) => OverlayDialog.ShowAsync<TResult>(this, owner);

    private void OnAgreeClick(object? sender, RoutedEventArgs e)
        => OverlayDialog.CloseDialog(this, true);

    private void OnDeclineClick(object? sender, RoutedEventArgs e)
        => OverlayDialog.CloseDialog(this, false);
}
