using Avalonia.Controls;
using Avalonia.Controls.Templates;
using TraeTools.ViewModels;
using TraeTools.Views;

namespace TraeTools;

/// <summary>
/// ViewModel → View 解析（App.axaml 中注册为数据模板，MainWindow 的 CurrentPage 走这里）。
/// P2-2：NativeAOT 下不能用 <c>Type.GetType</c> + <c>Activator.CreateInstance</c>——
/// 类型可能被裁剪掉、按名称反射也不可靠。改为**显式映射表**：编译期确定、AOT/裁剪安全。
/// 新增页面时记得在此登记（漏登记会显示 "Not Found"，不会静默空白）。
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null) return null;

        return param switch
        {
            DashboardViewModel => new DashboardView(),
            CheckinViewModel => new CheckinView(),
            CloudViewModel => new CloudView(),
            UsageViewModel => new UsageView(),
            SettingsViewModel => new SettingsView(),
            SwitchViewModel => new SwitchView(),
            _ => new TextBlock { Text = "Not Found: " + param.GetType().FullName },
        };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
