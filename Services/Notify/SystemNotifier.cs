using System;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;

namespace TraeTools.Services.Notify;

/// <summary>
/// 系统本地通知：走主窗口的 <see cref="WindowNotificationManager"/>（右下角通知，进系统通知中心）。
///
/// 定位说明：这是「本机在跑的时候」的即时反馈，属补充手段——
/// 云端签到由 GitHub Actions 执行，那一刻本机很可能并未开机，
/// 因此重要结果仍应依赖邮箱 / Webhook 等推送渠道。
/// </summary>
public static class SystemNotifier
{
    /// <summary>通知管理器按窗口懒创建：WindowNotificationManager 必须在 UI 线程构造。</summary>
    private static WindowNotificationManager? _manager;

    /// <summary>本机通知所需权限是否已具备。桌面端无运行时权限概念，恒为 true。</summary>
    public static bool HasPermission() => true;

    /// <summary>申请通知权限。桌面端为无操作，保留以统一各渠道的调用形态。</summary>
    public static bool RequestPermission() => true;

    /// <summary>发送一条系统本地通知。</summary>
    public static NotifyResult Send(string title, string body)
    {
        try
        {
            var win = TraeTools.ViewModels.UiHost.MainWindow;
            if (win == null)
                return NotifyResult.Fail("系统通知不可用：主窗口尚未就绪（请先打开主界面再试）");

            // 签到流程可能跑在后台线程，而通知管理器只能在 UI 线程使用，故切回 UI 线程。
            if (Dispatcher.UIThread.CheckAccess()) ShowCore(win, title, body);
            else Dispatcher.UIThread.Post(() => ShowCore(win, title, body));

            return NotifyResult.Ok0("已发送系统通知");
        }
        catch (Exception ex)
        {
            return NotifyResult.Fail("系统通知失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 实际的投递动作。内部再兜一层 try/catch：通知失败绝不应该冒泡到签到主流程
    /// （调用方已返回成功，此处失败只影响这一次提示）。
    /// </summary>
    private static void ShowCore(Window win, string title, string body)
    {
        try
        {
            _manager ??= new WindowNotificationManager(win)
            {
                Position = NotificationPosition.BottomRight,
                MaxItems = 3,
            };
            _manager.Show(new Notification(title, body));
        }
        catch { /* 单次通知失败不影响业务 */ }
    }
}
