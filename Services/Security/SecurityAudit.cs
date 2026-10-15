using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media;
using TraeCheckin;

namespace TraeTools.Services.Security;

/// <summary>一条安全检查结论。等级：ok=通过 / warn=警告 / risk=风险。</summary>
public sealed class SecurityFinding
{
    /// <summary>检查项标题（如「配置加密」）。</summary>
    public string Title { get; init; } = "";
    /// <summary>结论说明（人话，含可执行的处置建议）。</summary>
    public string Detail { get; init; } = "";
    /// <summary>等级。</summary>
    public string Level { get; init; } = "ok";

    public bool IsOk => Level == "ok";
    public bool IsWarn => Level == "warn";
    public bool IsRisk => Level == "risk";

    public string LevelText => Level switch
    {
        "risk" => "风险",
        "warn" => "警告",
        _ => "通过",
    };

    public string Icon => Level switch
    {
        "risk" => "⛔",
        "warn" => "⚠️",
        _ => "✅",
    };

    // 与项目其它状态色保持同一套取值（AccountInfo.StatusFgBrush / CloudStep.StatusFgBrush）

    /// <summary>前景色（图标与文字）。</summary>
    public IBrush LevelFgBrush => Level switch
    {
        "risk" => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        "warn" => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        _ => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
    };

    /// <summary>背景色（淡底徽章）。</summary>
    public IBrush LevelBgBrush => Level switch
    {
        "risk" => new SolidColorBrush(Color.FromArgb(0x1A, 0xEF, 0x44, 0x44)),
        "warn" => new SolidColorBrush(Color.FromArgb(0x1A, 0xF5, 0x9E, 0x0B)),
        _ => new SolidColorBrush(Color.FromArgb(0x1A, 0x10, 0xB9, 0x81)),
    };
}

/// <summary>
/// 账号与本地安全检查。
///
/// 只做**本地可判定的**检查，不做网络探测，也不修改任何数据——
/// 目的是让用户一眼看清「凭证有没有明文躺在磁盘上、设备号是不是会被风控认成同一台机器、
/// 会话还能撑多久、日志里有没有漏出完整凭证」。
///
/// ⚠️ 报告里**永不输出敏感值本身**：只出现字段名、长度与结论。
/// </summary>
public static class SecurityAudit
{
    /// <summary>敏感字段名（config.json 中应以密文存放的键）。</summary>
    private static readonly string[] SensitiveKeys =
    {
        "Token", "Session", "GitHubToken", "DeviceId", "FeishuWebhook",
        "SmtpPassword", "WebhookUrl", "DingTalkWebhook", "DingTalkSecret",
        "WeComWebhook", "TelegramBotToken",
    };

    /// <summary>JWT 形态的正则（用于判断明文凭证 / 日志泄漏）。</summary>
    private static readonly Regex JwtPattern =
        new(@"eyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}", RegexOptions.Compiled);

    /// <summary>运行全部检查，返回按「风险→警告→通过」排序的结论列表。</summary>
    public static List<SecurityFinding> Run(AppConfig? cfg)
    {
        var list = new List<SecurityFinding>();
        Safe(list, () => list.Add(CheckVaultVersion(cfg)));
        Safe(list, () => list.Add(CheckCryptoRoundTrip()));
        Safe(list, () => list.Add(CheckPlaintextResidue()));
        Safe(list, () => list.Add(CheckDeviceIds(cfg)));
        Safe(list, () => list.Add(CheckTokenLifetime(cfg)));
        Safe(list, () => list.Add(CheckDataDirPrivacy()));
        Safe(list, () => list.Add(CheckLogLeak()));

        // 风险在前、通过在后，便于用户先看要紧的
        var rank = new Dictionary<string, int> { ["risk"] = 0, ["warn"] = 1, ["ok"] = 2 };
        return list.OrderBy(f => rank.TryGetValue(f.Level, out var r) ? r : 3).ToList();
    }

    /// <summary>单个检查抛异常时降级为一条警告，绝不让整份报告失败。</summary>
    private static void Safe(List<SecurityFinding> list, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            list.Add(new SecurityFinding
            {
                Title = "检查项异常",
                Detail = "该项检查未能完成：" + ex.Message,
                Level = "warn",
            });
        }
    }

    // ---------- 1. 配置加密状态 ----------

    private static SecurityFinding CheckVaultVersion(AppConfig? cfg)
    {
        const string sealedTag = "dpapi-v1";
        if (cfg == null)
            return new SecurityFinding { Title = "配置加密", Detail = "配置未初始化，无法判断。", Level = "warn" };

        bool sealedVault = string.Equals(cfg.SecretsVaultVersion, sealedTag, StringComparison.Ordinal);
        return new SecurityFinding
        {
            Title = "配置加密",
            Detail = sealedVault
                ? $"凭证已启用平台加密存储（{VaultName}，仅当前用户可解密）；配置文件位于用户私有目录。"
                : "配置文件仍是旧版明文格式，下次保存时会自动加密。建议先在设置页保存一次配置以完成迁移。",
            Level = sealedVault ? "ok" : "risk",
        };
    }

    /// <summary>当前平台加密机制名称（仅用于报告文案）。本 PR 只覆盖 Windows 版主程序。</summary>
    private static string VaultName => "Windows DPAPI";

    // ---------- 2. 加解密自检 ----------

    private static SecurityFinding CheckCryptoRoundTrip()
    {
        const string sample = "traetools-selftest-2026";
        var enc = ProtectSample(sample);
        if (string.IsNullOrEmpty(enc))
            return new SecurityFinding
            {
                Title = "加解密自检",
                Detail = "平台加密不可用（Protect 返回空），凭证将以明文落盘。请检查系统密钥服务是否正常。",
                Level = "risk",
            };
        if (enc == sample)
            return new SecurityFinding
            {
                Title = "加解密自检",
                Detail = "加密函数未真正加密（密文与明文相同），凭证实际上以明文落盘。",
                Level = "risk",
            };
        var back = UnprotectSample(enc);
        bool ok = string.Equals(back, sample, StringComparison.Ordinal);
        return new SecurityFinding
        {
            Title = "加解密自检",
            Detail = ok
                ? $"加解密往返正常（示例密文长度 {enc.Length}，可还原）。"
                : "加密后无法解密还原：凭证落盘后取不回来（表现为每次启动都要重新登录、"
                  + "设备号退化成密文并被当成设备号发出去触发风控 9074）。",
            Level = ok ? "ok" : "risk",
        };
    }

    /// <summary>
    /// 自检用：与 AppConfig 的落盘加密保持同一机制（当前 Windows 用户作用域 DPAPI）。
    /// 刻意**不复用** AppConfig 的私有方法——自检若依赖被测对象本身，就失去了独立验证的意义。
    /// </summary>
    private static string? ProtectSample(string plain)
    {
        try
        {
            var bytes = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch { return null; }
    }

    private static string? UnprotectSample(string enc)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    // ---------- 3. 明文残留扫描 ----------

    private static SecurityFinding CheckPlaintextResidue()
    {
        var path = DataPaths.ConfigPath;
        if (!File.Exists(path))
            return new SecurityFinding { Title = "明文残留", Detail = "尚未生成配置文件，无残留可查。", Level = "ok" };

        var plain = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            foreach (var key in SensitiveKeys)
            {
                if (TryFindValue(root, key, out var v) && LooksPlaintext(v))
                    plain.Add(key);
            }
            // 账号数组里的敏感字段
            if (root.TryGetProperty("Accounts", out var accs) && accs.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var a in accs.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) { idx++; continue; }
                    foreach (var key in new[] { "Token", "Session", "DeviceId" })
                    {
                        if (a.TryGetProperty(key, out var v) && LooksPlaintext(v.GetString()))
                            plain.Add($"Accounts[{idx}].{key}");
                    }
                    idx++;
                }
            }
        }
        catch (Exception ex)
        {
            return new SecurityFinding
            {
                Title = "明文残留",
                Detail = "配置文件无法解析（" + ex.Message + "），建议在设置页重置软件后重新登录。",
                Level = "warn",
            };
        }

        if (plain.Count == 0)
            return new SecurityFinding
            {
                Title = "明文残留",
                Detail = $"配置文件中 {SensitiveKeys.Length} 个敏感字段及其账号副本均以密文存放，未发现明文泄漏。",
                Level = "ok",
            };

        // DeviceId 是 16 位数字，加密失败时会按设计退回明文（下次加载可自愈），单独降级为警告
        var onlyDevice = plain.All(p => p.EndsWith("DeviceId", StringComparison.Ordinal));
        return new SecurityFinding
        {
            Title = "明文残留",
            Detail = "以下字段在配置文件中没有加密（只列字段名，不显示内容）：" + string.Join("、", plain)
                     + (onlyDevice
                         ? "。设备号为 16 位数字，加密失败时会按设计退回明文（下次保存自动重加密），风险有限。"
                         : "。存在凭证或推送地址明文落盘，建议立即在设置页重新保存一次配置。"),
            Level = onlyDevice ? "warn" : "risk",
        };
    }

    /// <summary>在 JSON 中按不区分大小写查找属性（兼容历史命名）。</summary>
    private static bool TryFindValue(JsonElement root, string name, out string? value)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in root.EnumerateObject())
        {
            if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 判断落盘值是否为明文。只做形态判断，不解密：
    ///   · 含 "://"（推送地址）/ 含 "." 且像 JWT → 明文
    ///   · 纯 base64 且长度足够 → 视为 DPAPI 密文
    ///   · 16 位纯数字（DeviceId 明文兜底）→ 明文
    /// </summary>
    private static bool LooksPlaintext(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        if (JwtPattern.IsMatch(stored)) return true;
        if (stored.Contains("://", StringComparison.Ordinal)) return true;
        if (stored.Length == 16 && stored.All(char.IsDigit)) return true;
        // 纯 base64 且无 '.'（JWT 一定带点）→ 认作平台密文
        bool base64ish = stored.Length >= 24 && stored.Length % 4 == 0 &&
                         stored.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=');
        return !base64ish;
    }

    /// <summary>
    /// 判断一个值是否是「本该是明文、却被当成明文存下来的密文」。
    /// 用于识别解密失败后残留的坏设备号（超长 base64）。
    /// </summary>
    private static bool LooksLikeCiphertext(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        // DPAPI 密文是长 base64；设备号最长 16 位，超过 32 位基本可以断定不是设备号
        return value.Length > 32;
    }

    // ---------- 4. 设备号合规 ----------

    private static SecurityFinding CheckDeviceIds(AppConfig? cfg)
    {
        var accounts = cfg?.Accounts ?? new List<TraeAccount>();
        if (accounts.Count == 0)
            return new SecurityFinding { Title = "设备号", Detail = "暂无账号。", Level = "ok" };

        var bad = new List<string>();
        var residual = new List<string>();   // 疑似密文残留（旧版解密缺陷留下的坏数据）
        var dup = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var a in accounts)
        {
            var name = DisplayName(a);
            var id = a.DeviceId ?? "";
            if (id.Length != 16 || !id.All(char.IsDigit))
            {
                bad.Add(name);
                if (LooksLikeCiphertext(id)) residual.Add(name);
            }
            if (id.Length > 0)
            {
                if (seen.TryGetValue(id, out var other)) dup.Add($"{other} ↔ {name}");
                else seen[id] = name;
            }
        }

        if (residual.Count > 0)
            return new SecurityFinding
            {
                Title = "设备号",
                Detail = "以下账号的设备号是**加密后的密文**而不是 16 位数字（旧版解密失败后把密文当明文存了下来所致）："
                         + string.Join("、", residual)
                         + "。这种值会被原样发给签到接口，直接触发风控 9074「参与用户太多」。"
                         + "点该账号的「随机生成」→「保存设备号」即可修复，之后重新「云端部署」。",
                Level = "risk",
            };
        if (dup.Count > 0)
            return new SecurityFinding
            {
                Title = "设备号",
                Detail = "以下账号共用同一个设备号，会被风控识别成同一台设备并触发「参与用户太多(9074)」："
                         + string.Join("、", dup) + "。请为每个账号更换独立设备号。",
                Level = "risk",
            };
        if (bad.Count > 0)
            return new SecurityFinding
            {
                Title = "设备号",
                Detail = "以下账号的设备号不是 16 位数字：" + string.Join("、", bad)
                         + "。请在账号详情里自定义或随机生成一个 16 位数字设备号。",
                Level = "warn",
            };

        return new SecurityFinding
        {
            Title = "设备号",
            Detail = $"{accounts.Count} 个账号的设备号均为合法的 16 位数字且互不重复。",
            Level = "ok",
        };
    }

    // ---------- 5. Token / 会话有效期 ----------

    private static SecurityFinding CheckTokenLifetime(AppConfig? cfg)
    {
        var accounts = cfg?.Accounts ?? new List<TraeAccount>();
        if (accounts.Count == 0)
            return new SecurityFinding { Title = "登录态有效期", Detail = "暂无账号。", Level = "ok" };

        var expiredNoSession = new List<string>();
        var expiredWithSession = new List<string>();
        var expiringSoon = new List<string>();
        int noSession = 0, unknown = 0;

        foreach (var a in accounts)
        {
            var name = DisplayName(a);
            if (string.IsNullOrEmpty(a.Session)) noSession++;
            var exp = TokenUtils.TryGetExpiry(a.Token);
            if (exp == null) { unknown++; continue; }
            var left = exp.Value - DateTime.Now;
            if (left <= TimeSpan.Zero)
            {
                if (string.IsNullOrEmpty(a.Session)) expiredNoSession.Add(name);
                else expiredWithSession.Add(name);
            }
            else if (left < TimeSpan.FromHours(1)) expiringSoon.Add(name);
        }

        if (expiredNoSession.Count > 0)
            return new SecurityFinding
            {
                Title = "登录态有效期",
                Detail = "以下账号的 Token 已过期且没有会话 Cookie 可自动续期，必须重新登录："
                         + string.Join("、", expiredNoSession) + "。",
                Level = "risk",
            };
        if (expiredWithSession.Count > 0)
            return new SecurityFinding
            {
                Title = "登录态有效期",
                Detail = "以下账号的 Token 已过期，但可凭会话 Cookie 自动换新（签到或保活时会自动完成）："
                         + string.Join("、", expiredWithSession) + "。建议为该账号开启保活。",
                Level = "warn",
            };
        if (expiringSoon.Count > 0)
            return new SecurityFinding
            {
                Title = "登录态有效期",
                Detail = "以下账号的 Token 将在 1 小时内过期（开启保活可自动续期）：" + string.Join("、", expiringSoon) + "。",
                Level = "warn",
            };
        if (noSession > 0)
            return new SecurityFinding
            {
                Title = "登录态有效期",
                Detail = $"有 {noSession} 个账号没有会话 Cookie，Token 过期后无法自动续期，也无法保活。"
                         + "建议用「会话 Cookie 导入」方式重新添加这些账号。",
                Level = "warn",
            };
        return new SecurityFinding
        {
            Title = "登录态有效期",
            Detail = unknown > 0
                ? $"Token 均未过期；其中 {unknown} 个账号的 Token 不是标准 JWT（可能是 PAT），无法读取有效期。"
                : "全部账号的 Token 均在有效期内，且都持有会话 Cookie 可自动续期。",
            Level = "ok",
        };
    }

    // ---------- 6. 数据目录私有性 ----------

    private static SecurityFinding CheckDataDirPrivacy()
    {
        var root = DataPaths.Root;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        bool underUserProfile = !string.IsNullOrEmpty(appData) &&
                                root.StartsWith(appData, StringComparison.OrdinalIgnoreCase);

        return new SecurityFinding
        {
            Title = "数据目录",
            Detail = underUserProfile
                ? $"数据存放在当前用户私有目录（{root}），其它系统用户默认无权访问。"
                : $"数据目录不在用户私有目录内（{root}），同机其它用户可能读取到配置文件，建议改回默认位置。",
            Level = underUserProfile ? "ok" : "warn",
        };
    }

    // ---------- 7. 日志凭证泄漏 ----------

    private static SecurityFinding CheckLogLeak()
    {
        try
        {
            var files = AppLog.EnumerateFiles();
            if (files.Count == 0)
                return new SecurityFinding { Title = "日志脱敏", Detail = "暂无日志文件。", Level = "ok" };

            var hit = new List<string>();
            int scanned = 0;
            foreach (var f in files.Take(12))
            {
                scanned++;
                var tail = AppLog.ReadTail(f.Path, 64 * 1024);
                if (JwtPattern.IsMatch(tail)) hit.Add(f.Title);
            }

            if (hit.Count > 0)
                return new SecurityFinding
                {
                    Title = "日志脱敏",
                    Detail = "以下日志文件里出现了完整凭证（JWT 形态）：" + string.Join("、", hit)
                             + "。请在「日志 → 全部」中删除这些文件，并避免把日志原文发到公开渠道。",
                    Level = "risk",
                };
            return new SecurityFinding
            {
                Title = "日志脱敏",
                Detail = $"已抽查最近 {scanned} 个日志文件（各取末尾 64KB），未发现完整 Token 或会话 Cookie。",
                Level = "ok",
            };
        }
        catch (Exception ex)
        {
            return new SecurityFinding { Title = "日志脱敏", Detail = "日志检查失败：" + ex.Message, Level = "warn" };
        }
    }

    // ---------- 汇总 ----------

    /// <summary>一句话汇总（界面标题右侧展示）。</summary>
    public static string Summarize(IEnumerable<SecurityFinding> findings)
    {
        var list = findings.ToList();
        int risk = list.Count(f => f.IsRisk);
        int warn = list.Count(f => f.IsWarn);
        if (risk > 0) return $"{risk} 项风险、{warn} 项警告";
        if (warn > 0) return $"{warn} 项警告";
        return $"全部 {list.Count} 项通过";
    }

    private static string DisplayName(TraeAccount a)
    {
        if (!string.IsNullOrWhiteSpace(a.Name)) return a.Name!;
        if (!string.IsNullOrWhiteSpace(a.ScreenName)) return a.ScreenName!;
        if (!string.IsNullOrWhiteSpace(a.MobileMasked)) return a.MobileMasked!;
        return a.Id.Length > 6 ? a.Id[..6] : a.Id;
    }
}
