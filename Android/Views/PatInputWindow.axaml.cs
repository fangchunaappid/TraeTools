using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TraeTools.Views;

/// <summary>粘贴 GitHub PAT 的输入对话框：返回 true 表示已提交有效文本（从 <see cref="PatText"/> 取回）。</summary>
public partial class PatInputWindow : UserControl
{
    public string? PatText { get; private set; }

    public PatInputWindow()
    {
        InitializeComponent();
    }

    /// <summary>与 <c>Window.ShowDialog&lt;TResult&gt;(Window owner)</c> 对齐的入口。</summary>
    public Task<TResult> ShowDialog<TResult>(TopLevel? owner) => OverlayDialog.ShowAsync<TResult>(this, owner);

    private void OnOpenGithubClick(object? sender, RoutedEventArgs e)
    {
        // 走平台封装：Android 用 Intent 拉起系统浏览器，失败会写日志（category=browser）
        TraeTools.Platform.BrowserLauncher.Open("https://github.com/settings/personal-access-tokens/new");
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var value = PatBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            PatBox.Text = "";
            PatBox.Focus();
            return;
        }
        PatText = value;
        OverlayDialog.CloseDialog(this, true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => OverlayDialog.CloseDialog(this, false);
}
