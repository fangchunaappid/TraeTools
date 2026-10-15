using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TraeTools.Services;
using TraeTools.ViewModels;
using TraeTools.Views;

namespace TraeTools;

public partial class App : Application
{
    /// <summary>系统托盘图标全局引用（TrayIcon 无源头引用会被 GC 回收，故存于此）。</summary>
    public static TrayIcon? TrayRef;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();

            // 主题必须在窗口创建前应用：否则会先按默认变体绘制一帧再切过去（可见闪一下）。
            // MainViewModel 的构造已完成 AppConfig.Load()，此处读到的就是用户设置。
            ThemeService.Apply(MainViewModel.AppConfig?.ThemeMode);

            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}