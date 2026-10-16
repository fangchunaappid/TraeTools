using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeTools.Services;
using TraeTools.Services.Notify;
using TraeTools.Services.Security;

namespace TraeTools.ViewModels;

/// <summary>
/// <see cref="SettingsViewModel"/> 的增量部分：六渠道推送配置、日志与外观、自定义设备号、安全检查。
///
/// 为什么拆成 partial 文件而不是直接改 SettingsViewModel.cs：
/// 本次 PR 的硬约束是「只加功能、不动上游架构」，把新增内容集中在独立文件里，
/// 上游那份 880 行的文件保持**零修改**，评审时一眼能分清哪些是新增、哪些是原有。
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>加载期间为 true：抑制「改属性即保存」的副作用，避免加载过程写坏配置。</summary>
    private bool _extLoading;

    // ==================== 推送总开关 ====================

    /// <summary>签到完成后是否推送（关掉则所有渠道都不发）。</summary>
    [ObservableProperty]
    private bool _notifyOnCheckin = true;

    // ==================== 渠道：系统本地通知 ====================

    [ObservableProperty]
    private bool _notifySystemEnabled;

    /// <summary>本机系统通知是否可用（桌面端恒 true；留作界面提示用）。</summary>
    public bool SystemNotifySupported => SystemNotifier.HasPermission();

    // ==================== 渠道：自定义 SMTP 邮箱 ====================

    [ObservableProperty]
    private bool _emailEnabled;

    [ObservableProperty]
    private string _smtpHost = "";

    /// <summary>SMTP 端口，可自定义（465=隐式 SSL、587=STARTTLS、25=明文）。</summary>
    [ObservableProperty]
    private int _smtpPort = 465;

    [ObservableProperty]
    private string _smtpSecurity = "ssl";

    [ObservableProperty]
    private string _smtpUser = "";

    [ObservableProperty]
    private string _smtpPassword = "";

    [ObservableProperty]
    private string _smtpFrom = "";

    [ObservableProperty]
    private string _smtpTo = "";

    /// <summary>连接安全方式下拉项。</summary>
    public string[] SmtpSecurityOptions { get; } = { "ssl", "starttls", "none" };

    // ==================== 渠道：通用 Webhook ====================

    [ObservableProperty]
    private bool _webhookEnabled;

    [ObservableProperty]
    private string _webhookUrl = "";

    [ObservableProperty]
    private string _webhookBodyTemplate = "{\"title\":\"{title}\",\"content\":\"{content}\"}";

    [ObservableProperty]
    private string _webhookContentType = "application/json";

    // ==================== 渠道：钉钉机器人 ====================

    [ObservableProperty]
    private bool _dingTalkEnabled;

    [ObservableProperty]
    private string _dingTalkWebhook = "";

    /// <summary>加签密钥（机器人安全设置选择「加签」时必填）。</summary>
    [ObservableProperty]
    private string _dingTalkSecret = "";

    // ==================== 渠道：企业微信机器人 ====================

    [ObservableProperty]
    private bool _weComEnabled;

    [ObservableProperty]
    private string _weComWebhook = "";

    // ==================== 渠道：Telegram Bot ====================

    [ObservableProperty]
    private bool _telegramEnabled;

    [ObservableProperty]
    private string _telegramBotToken = "";

    [ObservableProperty]
    private string _telegramChatId = "";

    // ==================== 日志与外观 ====================

    /// <summary>日志页是否自动滚动到最新一行。</summary>
    [ObservableProperty]
    private bool _logAutoScroll = true;

    /// <summary>主题模式："system" / "light" / "dark"。</summary>
    [ObservableProperty]
    private string _themeMode = "system";

    /// <summary>主题模式下拉项。</summary>
    public string[] ThemeModeOptions { get; } = { "system", "light", "dark" };

    /// <summary>切换主题即时生效并落盘（不等「保存」按钮，符合用户对主题切换的预期）。</summary>
    partial void OnThemeModeChanged(string value)
    {
        if (_extLoading) return;
        try
        {
            ThemeService.Apply(value);
            var cfg = MainViewModel.AppConfig;
            if (cfg != null)
            {
                cfg.ThemeMode = ThemeService.Normalize(value);
                cfg.Save();
            }
        }
        catch { /* 主题切换失败不影响其它设置 */ }
    }

    // ==================== 自定义设备号 ====================

    /// <summary>用户手填的 16 位设备号（与选中账号绑定）。</summary>
    [ObservableProperty]
    private string _customDeviceId = "";

    // ==================== 安全检查 ====================

    [ObservableProperty]
    private bool _securityAuditRunning;

    [ObservableProperty]
    private string _securitySummary = "";

    /// <summary>安全检查结论列表（只读报告，不含任何敏感值本身）。</summary>
    public ObservableCollection<SecurityFinding> SecurityFindings { get; } = new();

    public bool HasSecurityFindings => SecurityFindings.Count > 0;

    // ====================================================================
    //                          加载 / 保存
    // ====================================================================

    /// <summary>
    /// 从 AppConfig 载入增量设置。由 MainViewModel 在构造后与每次进入设置页时调用
    /// （上游 SettingsViewModel 的构造函数不便改动，故用显式方法对外暴露）。
    /// </summary>
    public void LoadExtendedSettings()
    {
        _extLoading = true;
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null) return;

            NotifyOnCheckin = cfg.NotifyOnCheckin;
            NotifySystemEnabled = cfg.NotifySystemEnabled;

            EmailEnabled = cfg.EmailEnabled;
            SmtpHost = cfg.SmtpHost;
            SmtpPort = cfg.SmtpPort;
            SmtpSecurity = string.IsNullOrWhiteSpace(cfg.SmtpSecurity) ? "ssl" : cfg.SmtpSecurity;
            SmtpUser = cfg.SmtpUser;
            SmtpPassword = cfg.SmtpPassword ?? "";
            SmtpFrom = cfg.SmtpFrom;
            SmtpTo = cfg.SmtpTo;

            WebhookEnabled = cfg.WebhookEnabled;
            WebhookUrl = cfg.WebhookUrl ?? "";
            WebhookBodyTemplate = cfg.WebhookBodyTemplate;
            WebhookContentType = cfg.WebhookContentType;

            DingTalkEnabled = cfg.DingTalkEnabled;
            DingTalkWebhook = cfg.DingTalkWebhook ?? "";
            DingTalkSecret = cfg.DingTalkSecret ?? "";

            WeComEnabled = cfg.WeComEnabled;
            WeComWebhook = cfg.WeComWebhook ?? "";

            TelegramEnabled = cfg.TelegramEnabled;
            TelegramBotToken = cfg.TelegramBotToken ?? "";
            TelegramChatId = cfg.TelegramChatId;

            LogAutoScroll = cfg.LogAutoScroll;
            ThemeMode = ThemeService.Normalize(cfg.ThemeMode);

            CustomDeviceId = cfg.Accounts.FirstOrDefault(a => a.Id == SelectedAccount?.Id)?.DeviceId ?? "";
        }
        catch { /* 载入失败保留默认值 */ }
        finally { _extLoading = false; }
    }

    /// <summary>把界面上的增量设置写回 AppConfig 并落盘。</summary>
    private void PersistExtendedSettings()
    {
        var cfg = MainViewModel.AppConfig;
        if (cfg == null) return;

        cfg.NotifyOnCheckin = NotifyOnCheckin;
        cfg.NotifySystemEnabled = NotifySystemEnabled;

        cfg.EmailEnabled = EmailEnabled;
        cfg.SmtpHost = SmtpHost.Trim();
        cfg.SmtpPort = SmtpPort is > 0 and <= 65535 ? SmtpPort : 465;
        cfg.SmtpSecurity = string.IsNullOrWhiteSpace(SmtpSecurity) ? "ssl" : SmtpSecurity;
        cfg.SmtpUser = SmtpUser.Trim();
        cfg.SmtpPassword = string.IsNullOrEmpty(SmtpPassword) ? null : SmtpPassword;
        cfg.SmtpFrom = SmtpFrom.Trim();
        cfg.SmtpTo = SmtpTo.Trim();

        cfg.WebhookEnabled = WebhookEnabled;
        cfg.WebhookUrl = string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl.Trim();
        cfg.WebhookBodyTemplate = WebhookBodyTemplate;
        cfg.WebhookContentType = WebhookContentType.Trim();

        cfg.DingTalkEnabled = DingTalkEnabled;
        cfg.DingTalkWebhook = string.IsNullOrWhiteSpace(DingTalkWebhook) ? null : DingTalkWebhook.Trim();
        cfg.DingTalkSecret = string.IsNullOrWhiteSpace(DingTalkSecret) ? null : DingTalkSecret.Trim();

        cfg.WeComEnabled = WeComEnabled;
        cfg.WeComWebhook = string.IsNullOrWhiteSpace(WeComWebhook) ? null : WeComWebhook.Trim();

        cfg.TelegramEnabled = TelegramEnabled;
        cfg.TelegramBotToken = string.IsNullOrWhiteSpace(TelegramBotToken) ? null : TelegramBotToken.Trim();
        cfg.TelegramChatId = TelegramChatId.Trim();

        cfg.LogAutoScroll = LogAutoScroll;
        cfg.ThemeMode = ThemeService.Normalize(ThemeMode);

        cfg.Save();
    }

    /// <summary>保存全部推送渠道配置。</summary>
    [RelayCommand]
    private void SaveNotifySettings()
    {
        try
        {
            PersistExtendedSettings();
            PushStatus = "推送配置已保存 ✓";
        }
        catch (Exception ex)
        {
            PushStatus = "保存失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 向**所有已启用**的渠道发一条测试消息，逐渠道回显成败。
    /// 走与签到通知完全相同的 NotifyDispatcher，因此测试通过即代表真到点时会通。
    /// </summary>
    [RelayCommand]
    private async Task TestAllChannelsAsync()
    {
        try
        {
            PersistExtendedSettings();
            var cfg = MainViewModel.AppConfig;
            if (cfg == null)
            {
                PushStatus = "配置未就绪";
                return;
            }

            PushStatus = "正在测试推送…";
            var outcomes = await NotifyDispatcher.SendAsync(
                cfg,
                "【TraeTools】推送测试",
                $"这是一条测试消息。\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                includeSystem: true);

            PushStatus = NotifyDispatcher.Summarize(outcomes)
                         + (outcomes.Count > 0 ? "：" + string.Join("、", outcomes.Select(o => o.Channel)) : "");
        }
        catch (Exception ex)
        {
            PushStatus = "测试异常：" + ex.Message;
        }
    }

    // ====================================================================
    //                        自定义设备号
    // ====================================================================

    /// <summary>为选中账号随机生成一个 16 位设备号（填充到输入框，仍需点「保存」才落盘）。</summary>
    [RelayCommand]
    private void RandomizeCustomDeviceId()
    {
        try
        {
            CustomDeviceId = Random.Shared
                .NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString();
            PushStatus = "已生成新设备号，点「保存设备号」生效";
        }
        catch (Exception ex)
        {
            PushStatus = "生成失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 保存手填的设备号。校验：16 位纯数字（Trae 风控要求 x-device-id 为数字设备号，
    /// 用 GUID 会返回 9074）、且不得与其它账号重复（多账号共用同一设备号同样触发 9074）。
    /// </summary>
    [RelayCommand]
    private void SaveCustomDeviceId()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == SelectedAccount?.Id);
            if (cfg == null || acc == null)
            {
                PushStatus = "请先选择账号";
                return;
            }

            var id = (CustomDeviceId ?? "").Trim();
            if (id.Length != 16 || !id.All(char.IsDigit))
            {
                PushStatus = "设备号必须是 16 位纯数字";
                return;
            }
            if (cfg.Accounts.Any(a => a.Id != acc.Id && a.DeviceId == id))
            {
                PushStatus = "该设备号已被其它账号使用（多账号共用会触发风控 9074）";
                return;
            }

            acc.DeviceId = id;
            cfg.Save();
            DeviceIdText = id;   // 联动上游既有的设备号展示
            PushStatus = $"设备号已保存：{id}（本地生效；云端需重新部署同步）";
            AccountHelpers.AppLog("account", "", $"自定义设备号：账号 {acc.Id[..Math.Min(6, acc.Id.Length)]} 设为 {id}");
        }
        catch (Exception ex)
        {
            PushStatus = "保存失败：" + ex.Message;
        }
    }

    // ====================================================================
    //                          安全检查
    // ====================================================================

    /// <summary>
    /// 运行「账号与本地安全检查」：只读本地可判定的项，不做网络探测、不修改任何数据。
    /// 报告里永不输出敏感值本身（只出现字段名、长度与结论）。
    /// </summary>
    [RelayCommand]
    private async Task RunSecurityAuditAsync()
    {
        try
        {
            SecurityAuditRunning = true;
            var cfg = MainViewModel.AppConfig;

            // 读取磁盘文件 + 解密自检，放到后台线程，避免卡住界面
            var findings = await Task.Run(() => SecurityAudit.Run(cfg));

            SecurityFindings.Clear();
            foreach (var f in findings) SecurityFindings.Add(f);
            OnPropertyChanged(nameof(HasSecurityFindings));
            SecuritySummary = SecurityAudit.Summarize(findings);
        }
        catch (Exception ex)
        {
            SecuritySummary = "检查失败：" + ex.Message;
        }
        finally
        {
            SecurityAuditRunning = false;
        }
    }
}
