using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTools.Models;

namespace TraeTools.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> 的增量部分：顶栏的「当前账号 + 下拉切换」。
///
/// 上游原本只能在「账号切换」页里选账号再点切换；本文件把同一套能力提到顶栏，
/// 任何页面都能一眼看到当前账号并直接切走。
///
/// 关键约束：**不另起一套切换实现**——账号列表与选中项直接复用上游
/// <see cref="SwitchViewModel"/> 的集合与选中属性，执行也走它既有的
/// SwitchAccount 命令（内部复用 TraeSwitch 的 SwitcherService 冷切换编排）。
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// 顶栏下拉的账号列表：直接复用账号切换页的集合，避免两处状态各自维护导致不一致。
    /// </summary>
    public ObservableCollection<AccountInfo> TopAccounts => _switch.Accounts;

    /// <summary>
    /// 顶栏下拉当前选中的账号。写入即同步到切换页的选中项（两边永远同一个值）。
    /// </summary>
    public AccountInfo? TopSelectedAccount
    {
        get => _switch.SelectedAccount;
        set
        {
            if (ReferenceEquals(_switch.SelectedAccount, value)) return;
            _switch.SelectedAccount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>当前真正生效（激活）的账号文案，用于顶栏右侧提示；无账号时给出明确说明。</summary>
    public string TopActiveAccountText
    {
        get
        {
            var cfg = AppConfig;
            if (cfg == null || cfg.Accounts.Count == 0) return "尚未添加账号";
            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId) ?? cfg.Accounts[0];
            var name = string.IsNullOrEmpty(acc.Name) ? $"账号@{acc.Id[..Math.Min(6, acc.Id.Length)]}" : acc.Name;
            return $"当前生效：{name}";
        }
    }

    /// <summary>切换是否进行中（顶栏按钮据此禁用，避免并发切换）。</summary>
    public bool TopIsSwitching => _switch.IsSwitching;

    /// <summary>
    /// 刷新顶栏展示。账号增删、激活账号变化后调用（调用方负责在 UI 线程上调用）。
    /// </summary>
    public void RefreshTopBar()
    {
        try
        {
            OnPropertyChanged(nameof(TopAccounts));
            OnPropertyChanged(nameof(TopSelectedAccount));
            OnPropertyChanged(nameof(TopActiveAccountText));
            OnPropertyChanged(nameof(TopIsSwitching));
        }
        catch { /* 顶栏刷新失败不影响功能 */ }
    }

    /// <summary>
    /// 顶栏「切换」：先跳到账号切换页（切换过程的 5 步进度与日志在那里可见），
    /// 再执行上游既有的冷切换命令。
    /// </summary>
    [RelayCommand]
    private async Task SwitchToSelectedAccountAsync()
    {
        try
        {
            if (TopSelectedAccount == null)
            {
                Navigate("switch");
                return;
            }

            Navigate("switch");
            OnPropertyChanged(nameof(TopIsSwitching));
            await _switch.SwitchAccountCommand.ExecuteAsync(null);
        }
        catch { /* 失败原因由切换页日志呈现 */ }
        finally
        {
            RefreshTopBar();
        }
    }
}
