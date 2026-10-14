using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TraeTools.Views;

/// <summary>
/// 通用确认/提示对话框。
/// <c>ShowDialog&lt;bool&gt;</c> 返回：true=点了确认/知道了，false=取消。
///
/// <para>调用方是共享的 ViewModel（<c>dlg.ShowDialog&lt;bool&gt;(owner)</c>），
/// 因此这里提供与 <c>Window.ShowDialog&lt;TResult&gt;(Window)</c> 同名、同参数形态、
/// 同返回类型的方法，把呈现交给 <see cref="OverlayDialog"/>。</para>
/// </summary>
public partial class PromptWindow : UserControl
{
    public PromptWindow() { InitializeComponent(); }

    public PromptWindow(string title, string message, string confirmText = "确定",
        string cancelText = "取消", bool showCancel = true)
        : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        if (showCancel)
        {
            CancelButton.Content = cancelText;
            CancelButton.IsVisible = true;
        }
        else
        {
            CancelButton.IsVisible = false;
        }
    }

    /// <summary>
    /// 与 <c>Window.ShowDialog&lt;TResult&gt;(Window owner)</c> 对齐的入口。
    /// owner 接收 <c>UiHost.MainWindow</c>（本平台为 <c>TopLevel?</c>）；覆盖层是全屏的，不使用它。
    /// </summary>
    public Task<TResult> ShowDialog<TResult>(TopLevel? owner) => OverlayDialog.ShowAsync<TResult>(this, owner);

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => OverlayDialog.CloseDialog(this, true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => OverlayDialog.CloseDialog(this, false);
}
