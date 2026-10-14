using System;
using System.IO;

namespace TraeTools.Services;

/// <summary>
/// 统一数据根目录（Android 实现）。
///
/// Windows 版落在 <c>%APPDATA%\TraeTools</c>；Android 没有「用户漫游目录」的概念，
/// 应用私有目录就是 <c>Context.FilesDir</c>（<c>/data/data/&lt;包名&gt;/files</c>，
/// 非 root 不可读、随卸载清除），因此根目录取它的 <c>TraeTools</c> 子目录。
///
/// <para>目录布局与桌面端保持一致（config.json / data / logs / switch / vault），
/// 这样 <c>Services/Checkin/*</c>、<c>Services/Switch/*</c> 等共享代码无需任何分支。</para>
///
/// <para><c>Migrate()</c> 在本平台是空操作：TraeCheckin / TraeSwitch 的历史目录只存在于 Windows。</para>
/// </summary>
public static class DataPaths
{
    /// <summary>根目录：应用私有 files 目录下的 TraeTools 子目录。</summary>
    public static string Root
    {
        get
        {
            try
            {
                var files = Android.App.Application.Context?.FilesDir?.AbsolutePath;
                if (!string.IsNullOrEmpty(files))
                    return Path.Combine(files!, "TraeTools");
            }
            catch
            {
                // Context 尚未就绪时走下面的兜底路径
            }
            // 兜底：极早期调用（Application.Context 为 null）时退化到 HOME 目录，
            // 保证 Root 永远返回可用字符串而不是抛异常。
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal), "TraeTools");
        }
    }

    /// <summary>签到配置 config.json。</summary>
    public static string ConfigPath => Path.Combine(Root, "config.json");

    /// <summary>数据目录：存放 checkin.db、history_*.txt、credits_total_*.txt、usage_*.jsonl。</summary>
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Root, "data");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>日志目录：存放各模块调试日志（checkin_log / account_log / cloud_log / switch_log / usage_log）。</summary>
    public static string LogsDir
    {
        get
        {
            var dir = Path.Combine(Root, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>账号切换配置（settings.json）。</summary>
    public static string SwitchDir => Path.Combine(Root, "switch");

    /// <summary>登录态备份 vault。手机端不做客户端切换，该目录仅用于放置共享代码。</summary>
    public static string VaultDir => Path.Combine(Root, "vault");

    /// <summary>确保所有子目录存在（启动时调用一次）。</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(SwitchDir);
        Directory.CreateDirectory(VaultDir);
    }

    /// <summary>
    /// Windows 版用于把 TraeCheckin / TraeSwitch 历史目录迁移到统一根；
    /// Android 是全新平台、没有历史目录可迁移，因此只保证目录存在。
    /// 保留同名方法是为了让共享的调用方（如各 ViewModel 的初始化路径）不改一行代码。
    /// </summary>
    public static void Migrate()
    {
        try
        {
            EnsureDirectories();
        }
        catch
        {
            // 目录创建失败不阻塞启动：后续写入会各自失败并提示
        }
    }
}
