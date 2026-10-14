using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TraeTools.Platform;
using TraeTools.ViewModels;
using TraeTools.Views;

namespace TraeTools;

/// <summary>
/// Android 版应用入口。
///
/// 与仓库根的 App.axaml.cs 的区别只有两处：
///   1) 没有 <c>IClassicDesktopStyleApplicationLifetime</c> 分支（单视图平台无 Window）；
///   2) Application 一律写全称 <c>Avalonia.Application</c>，与 Android.App.Application 区分。
/// </summary>
public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        try
        {
            AvaloniaXamlLoader.Load(this);
            CrashLog.Boot("App.Initialize completed");
        }
        catch (Exception ex)
        {
            CrashLog.Write("App.Initialize", ex, true);
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        CrashLog.Install(text => CrashReportActivity.Launch(text));
        CrashLog.Boot($"App.OnFrameworkInitializationCompleted lifetime={ApplicationLifetime?.GetType().FullName}");

        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // 上次运行崩溃过：本次先进错误页（可复制堆栈 / 重试），避免反复闪退
            var flag = CrashLog.ReadFlag();
            if (!string.IsNullOrEmpty(flag))
            {
                CrashLog.Boot("App: previous crash detected -> startup error view");
                singleView.MainView = BuildErrorView(singleView, "上次启动崩溃了", flag!);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            try
            {
                // 单视图生命周期：MainView（UserControl）作为根视图全屏展示。
                // 不能用 MainWindow（Window）：单视图平台创建 Window 会抛 NotSupportedException。
                singleView.MainView = new MainView
                {
                    DataContext = new MainViewModel(),
                };
                CrashLog.Boot("App: single-view MainView created");
            }
            catch (Exception ex)
            {
                // 主界面构建失败也不能闪退：显示可读的错误页
                CrashLog.Write("App: create MainView failed", ex, true);
                singleView.MainView = BuildErrorView(singleView, "界面初始化失败", ex.ToString());
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>构建启动错误页：异常堆栈 + 启动日志 + 「清除并重新加载界面」。</summary>
    private static Control BuildErrorView(
        ISingleViewApplicationLifetime lifetime,
        string title,
        string detail)
    {
        var body = detail + Environment.NewLine + Environment.NewLine
                 + "==== 启动日志（尾部）====" + Environment.NewLine
                 + CrashLog.ReadRecent();

        var text = new SelectableTextBlock
        {
            Text = title + Environment.NewLine + Environment.NewLine + body,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontFamily = new Avalonia.Media.FontFamily("Consolas,monospace"),
            FontSize = 12,
            Margin = new Thickness(16),
        };

        var retry = new Button
        {
            Content = "清除崩溃标记并重新加载界面",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(16, 0, 16, 24),
            Padding = new Thickness(16, 8),
        };
        retry.Click += (_, _) =>
        {
            CrashLog.ClearFlag();
            try
            {
                lifetime.MainView = new MainView { DataContext = new MainViewModel() };
                CrashLog.Boot("App: retry create MainView ok");
            }
            catch (Exception ex)
            {
                CrashLog.Write("App: retry create MainView failed", ex, true);
            }
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Children.Add(new ScrollViewer { Content = text });
        Grid.SetRow(retry, 1);
        grid.Children.Add(retry);
        return grid;
    }
}
