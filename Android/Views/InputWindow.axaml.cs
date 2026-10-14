using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace TraeTools.Views;

/// <summary>
/// 通用单行文本输入对话框。
/// <c>ShowDialog&lt;string&gt;</c> 返回：确认时返回输入文本（trim 后），取消返回 null。
/// </summary>
public partial class InputWindow : UserControl
{
    public string? ResultText { get; private set; }

    public InputWindow()
    {
        InitializeComponent();
    }

    /// <param name="title">标题。</param>
    /// <param name="message">提示文案。</param>
    /// <param name="placeholder">输入框占位符。</param>
    public InputWindow(string title, string message, string placeholder)
        : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        InputBox.PlaceholderText = placeholder;
    }

    /// <summary>与 <c>Window.ShowDialog&lt;TResult&gt;(Window owner)</c> 对齐的入口。</summary>
    public Task<TResult> ShowDialog<TResult>(TopLevel? owner) => OverlayDialog.ShowAsync<TResult>(this, owner);

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var value = InputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            InputBox.Focus();
            return;
        }
        ResultText = value;
        OverlayDialog.CloseDialog(this, ResultText);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => OverlayDialog.CloseDialog(this, null);

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOkClick(sender, e);
    }
}
