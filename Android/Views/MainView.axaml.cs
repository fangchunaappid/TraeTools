using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TraeTools.Platform;
using TraeTools.ViewModels;

namespace TraeTools.Views;

/// <summary>
/// Android 主视图（单视图根 UserControl）。
///
/// 承担三件上游由 MainWindow 负责的事：
///   1) 把覆盖层挂给 <see cref="OverlayDialog"/>（对话框宿主）；
///   2) 每 30 秒检查一次自动签到；
///   3) 首次启动弹出 EULA。
/// 桌面端那些能力（托盘图标、最小化到托盘、任务栏图标、系统通知）在手机上无对应概念，故不移植。
/// </summary>
public partial class MainView : UserControl
{
    /// <summary>自动签到定时器：每 30 秒检查一次，到点且当天未签时自动签到。</summary>
    private readonly DispatcherTimer _autoCheckinTimer;

    /// <summary>计时器重入守卫：上一轮任务未结束前跳过本轮 Tick。</summary>
    private bool _tickBusy;
    private bool _started;
    private bool _eulaChecked;

    public MainView()
    {
        InitializeComponent();

        // 覆盖层宿主：所有对话框以 UserControl 形式挂到这里
        OverlayDialog.Attach(OverlayRoot, OverlayContent);
        CrashLog.Boot("MainView: ctor, overlay attached");

        _autoCheckinTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoCheckinTimer.Tick += async (_, _) =>
        {
            if (_tickBusy) return;
            _tickBusy = true;
            try
            {
                if (DataContext is MainViewModel vm)
                {
                    // 与桌面端 MainWindow 的定时器保持一致的两步
                    await vm.AutoCheckinIfDue();
                    await vm.RefreshSnapshotIfNeeded();
                }
            }
            catch (Exception ex)
            {
                // 定时任务异常不允许带崩 UI（未捕获异常会直接闪退）
                CrashLog.Write("AutoCheckin timer tick", ex);
            }
            finally
            {
                _tickBusy = false;
            }
        };

        // EULA 需要视觉树就绪后才能取到 TopLevel，故放到 Loaded
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_eulaChecked) return;
        _eulaChecked = true;
        CrashLog.Boot("MainView: loaded");
        try
        {
            _ = EulaCheckAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainView.OnLoaded", ex);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_started) return;
        _started = true;
        CrashLog.Boot("MainView: attached to visual tree");
        try
        {
            // 自动签到仅在 App 运行期间有效；长期无人值守请使用「云端签到」
            _autoCheckinTimer.Start();
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainView.OnAttachedToVisualTree", ex);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        try { _autoCheckinTimer.Stop(); }
        catch { /* 停止定时器失败不影响退出 */ }
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 首次启动弹出免责声明协议（EULA）。同意标记存放于数据根目录 eula_accepted，
    /// 与 SettingsViewModel 里「重置协议同意状态」用的是同一个路径，两边保持一致。
    /// </summary>
    private async Task EulaCheckAsync()
    {
        try
        {
            var flag = Path.Combine(TraeTools.Services.DataPaths.Root, "eula_accepted");
            if (File.Exists(flag)) return;

            CrashLog.Boot("EULA: showing agreement");
            var eula = new EulaWindow();
            bool agreed;
            try
            {
                agreed = await eula.ShowDialog<bool>(TopLevel.GetTopLevel(this));
            }
            catch (Exception ex)
            {
                // 兜底：对话框呈现失败则记录并视为同意，保证应用可用（日志留痕）
                CrashLog.Write("EULA dialog failed", ex);
                agreed = true;
            }
            CrashLog.Boot($"EULA: agreed={agreed}");

            if (agreed)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(flag)!);
                    File.WriteAllText(flag, DateTime.UtcNow.ToString("o"));
                }
                catch
                {
                    // 标记写失败下次仍会弹，可接受
                }
            }
            else
            {
                // 不同意：与桌面端「直接退出」对齐 —— 手机端关闭唯一 Activity 即回到桌面。
                // 不做 Process.Kill：那会让系统记录成异常退出。
                CrashLog.Boot("EULA: declined -> finish activity");
                MainActivity.Current?.Finish();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainView.EulaCheckAsync", ex);
        }
    }
}
