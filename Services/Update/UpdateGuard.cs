using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TraeSwitch.Services;

namespace TraeTools.Services;

/// <summary>
/// 客户端自动更新开关：读写客户端用户配置 settings.json 里的 <c>update.mode</c>。
/// update.mode 是客户端官方配置项（取值 manual / start / default）：
///   · manual / none → 不排定自动检查更新（「手动检查更新」入口仍然保留）
///   · default      → 默认策略（定时自动检查更新）
/// 因此把它设为 manual 即可让客户端不再定时检查新版本，从而不再弹出更新提示。
///
/// 安全约定：
///   · 只改用户级配置文件，绝不触碰客户端安装目录，也不代管客户端进程（不会自动重启客户端）；
///   · 首次开启时把原文件整份备份为 <c>settings.json.traetools.bak</c>，关闭开关时按备份还原该键原始取值，
///     不会连带覆盖用户在期间改动的其它设置；
///   · 写文件走「临时文件 + 原子移动」，避免写一半导致配置损坏。
/// 改动在客户端下次启动时生效（更新检查器在启动时读取该配置）。
/// </summary>
public static class UpdateGuard
{
    /// <summary>客户端配置键名。</summary>
    public const string Key = "update.mode";

    /// <summary>开启开关时写入的取值（保留手动检查入口，比 none 更稳妥）。</summary>
    public const string SuppressedValue = "manual";

    /// <summary>关闭开关、且没有备份可还原时写回的标准默认取值。</summary>
    public const string DefaultValue = "default";

    /// <summary>客户端用户级配置 settings.json（update.mode 为 APPLICATION 作用域，存于用户级配置）。</summary>
    public static string SettingsFile =>
        Path.Combine(CarrierDefaults.DefaultUserDataDir, "User", "settings.json");

    /// <summary>整文件备份路径（首次开启时生成，关闭开关时按它还原该键）。</summary>
    public static string BackupFile => SettingsFile + ".traetools.bak";

    /// <summary>精确匹配 <c>"update.mode": "值"</c>（容忍空白），用于读取 / 替换该键。</summary>
    private static readonly Regex KeyPattern = new(
        "\"" + Regex.Escape(Key) + "\"\\s*:\\s*\"(?<v>[^\"]*)\"",
        RegexOptions.CultureInvariant);

    /// <summary>读取当前 update.mode 取值；配置不存在或未设置该键时返回 null。</summary>
    public static string? ReadMode()
    {
        try
        {
            var path = SettingsFile;
            if (!File.Exists(path)) return null;
            var m = KeyPattern.Match(File.ReadAllText(path));
            return m.Success ? m.Groups["v"].Value : null;
        }
        catch { return null; }
    }

    /// <summary>是否已屏蔽自动更新检查（manual / none 均视为已屏蔽）。</summary>
    public static bool IsSuppressed()
    {
        var v = ReadMode();
        return v is not null
               && (v.Equals("manual", StringComparison.OrdinalIgnoreCase)
                   || v.Equals("none", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 设置开关，返回给用户看的结果消息。
    ///   · suppress=true  → 首次开启时整文件备份，再写入 manual。
    ///   · suppress=false → 优先按备份还原该键的原始取值（备份无该键则写 default），随后删除备份。
    /// </summary>
    public static string Apply(bool suppress)
    {
        var path = SettingsFile;
        if (!File.Exists(path))
            throw new FileNotFoundException("未找到客户端配置文件：" + path);

        var current = File.ReadAllText(path);

        if (suppress)
        {
            // 仅在首次开启时备份，避免每次开启都用「当前（可能已被改过的）值」覆盖原始备份
            if (!File.Exists(BackupFile))
                File.Copy(path, BackupFile, overwrite: true);

            WriteAtomic(path, SetKey(current, SuppressedValue));
            return "已关闭客户端自动更新检查，重启客户端后不再弹更新提示。可随时用同一开关还原。";
        }

        // 关闭：尽量还原成开启前的原始取值；无备份则回到标准默认
        var target = DefaultValue;
        if (File.Exists(BackupFile))
        {
            try
            {
                var m = KeyPattern.Match(File.ReadAllText(BackupFile));
                if (m.Success) target = m.Groups["v"].Value;
            }
            catch { /* 备份不可读：退化为 default */ }
        }

        WriteAtomic(path, SetKey(current, target));
        try { if (File.Exists(BackupFile)) File.Delete(BackupFile); } catch { /* 删除失败忽略 */ }
        return $"已恢复客户端更新检查（update.mode = {target}），重启客户端后生效。";
    }

    /// <summary>
    /// 在 JSON(C) 文本中设置顶层键 update.mode：已存在则替换其取值，否则插入到最外层对象末尾。
    /// 采用文本级替换而非整文件反序列化，保留用户原有的缩进、注释与换行风格。
    /// </summary>
    private static string SetKey(string json, string value)
    {
        var m = KeyPattern.Match(json);
        if (m.Success)
            return json[..m.Index] + $"\"{Key}\": \"{value}\"" + json[(m.Index + m.Length)..];

        // 未设置该键：插入到最外层对象闭合大括号之前
        int close = json.LastIndexOf('}');
        if (close < 0)
            throw new InvalidOperationException("settings.json 格式无法识别（未找到对象结尾）。");

        int open = json.IndexOf('{');
        bool hasProps = open >= 0 && json[(open + 1)..close].Trim().Length > 0;

        var head = json[..close].TrimEnd();
        var tail = json[close..];
        return head + (hasProps ? "," : "") + $"\n  \"{Key}\": \"{value}\"\n" + tail;
    }

    /// <summary>原子写入：先写临时文件再替换目标，避免写到一半损坏客户端配置。</summary>
    private static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".traetools.tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }
}
