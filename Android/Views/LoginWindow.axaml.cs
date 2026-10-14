using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TraeCheckin;
using TraeTools.ViewModels;

namespace TraeTools.Views;

/// <summary>
/// 登录对话框（Android）。
///
/// 桌面端优先走独立宿主进程里的内嵌 WebView2 自动读取 token + session；
/// Android 没有 WebView2 宿主进程（<c>Services/Login/**</c> 也不在本工程编译范围内），
/// 因此只保留「手动粘贴 Cloud-IDE-Token」这一条通道 ——
/// 校验、写入账号、持久化的逻辑与桌面端逐字一致。
/// </summary>
public partial class LoginWindow : UserControl
{
    private readonly TraeAccount? _account;

    public LoginWindow()
    {
        InitializeComponent();
    }

    public LoginWindow(TraeAccount? account)
        : this()
    {
        _account = account;
    }

    /// <summary>与 <c>Window.ShowDialog&lt;TResult&gt;(Window owner)</c> 对齐的入口。</summary>
    public Task<TResult> ShowDialog<TResult>(TopLevel? owner) => OverlayDialog.ShowAsync<TResult>(this, owner);

    /// <summary>手机端获取 Token 的引导（桌面端此处是内嵌浏览器自动登录）。</summary>
    private void OnAutoLoginClick(object? sender, RoutedEventArgs e)
    {
        AutoStatus.Text =
            "1）在电脑浏览器打开 www.trae.cn 并登录；\n" +
            "2）按 F12 打开开发者工具 → Application → Local Storage → 找到 Cloud-IDE-Token；\n" +
            "3）复制它的值（一串以 eyJ 开头的 JWT，很长）；\n" +
            "4）回到本页粘贴到下方输入框并点「保存」。";
    }

    private void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
        => TraeTools.Platform.BrowserLauncher.Open("https://www.trae.cn");

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var pasted = TokenBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(pasted))
        {
            StatusText.Text = "请先粘贴 Cloud-IDE-Token";
            return;
        }
        if (_account == null)
        {
            StatusText.Text = "无当前账号，无法保存";
            return;
        }
        var initial = _account.Token;
        if (!TokenUtils.ShouldAcceptNewToken(initial, pasted))
        {
            StatusText.Text = "Token 无效（长度不足或与当前账号相同），请重新复制";
            return;
        }
        _account.Token = pasted;
        _account.TokenUpdatedAt = DateTime.Now;
        var uid = TokenUtils.ParseAccountUid(pasted);
        if (!string.IsNullOrEmpty(uid)) _account.AccountUid = uid;
        MainViewModel.AppConfig?.Save();
        OverlayDialog.CloseDialog(this, true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
        => OverlayDialog.CloseDialog(this, false);
}
