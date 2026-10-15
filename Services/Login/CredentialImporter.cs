using System;
using System.Text;
using System.Text.Json;

namespace TraeTools.Services.Login;

/// <summary>凭证类型：无法识别 / Cloud-IDE-Token(JWT) / X-Cloudide-Session(会话 Cookie)。</summary>
public enum CredentialKind
{
    Unknown,
    Token,
    Session,
}

/// <summary>
/// 登录凭证的清洗、识别与导入。
///
/// 存在的意义：用户从浏览器「开发者工具 → Application → Cookies」或
/// 「Network → 请求头」复制出来的内容往往带外壳（`X-Cloudide-Session=xxx;`、
/// `Authorization: Cloud-IDE-JWT xxx`、成对引号、换行），直接粘贴会因长度/格式判定
/// 失败而报「Token 无效」。本类负责把这些外壳剥掉并判定真实类型，
/// 从而让「剪贴板一键导入」与「会话 Cookie 导入」在真实的复制粘贴场景下可用。
/// </summary>
public static class CredentialImporter
{
    /// <summary>会话 Cookie 名（Trae 网页端）。</summary>
    private const string SessionCookieName = "X-Cloudide-Session";

    /// <summary>Authorization 头的方案前缀。</summary>
    private const string JwtScheme = "Cloud-IDE-JWT";

    /// <summary>JWT 最小可接受长度（Cloud-IDE-Token 实际远长于此）。</summary>
    private const int MinTokenLength = 40;

    /// <summary>会话串最小可接受长度（低于此长度不足以成为有效会话）。</summary>
    private const int MinSessionLength = 16;

    /// <summary>
    /// 剥掉常见外壳：Cookie 名与分号、Authorization 方案前缀、包裹引号、
    /// `--cookie "..."` 形式的命令行外壳、多余空白与换行。
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();

        // 去掉可能整段粘贴进来的 `--cookie "..."` / `-H "Cookie: ..."` 外层
        s = s.Trim('"', '\'');

        // 取 Cookie 头里的目标字段：X-Cloudide-Session=<值>
        var idx = s.IndexOf(SessionCookieName + "=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            s = s[(idx + SessionCookieName.Length + 1)..];
            // 值到分号/空白/换行结束
            var end = s.IndexOfAny(new[] { ';', '\r', '\n', ' ', '\t', '"', '\'' });
            if (end >= 0) s = s[..end];
        }

        // 去掉 Authorization 方案前缀
        s = s.Trim();
        if (s.StartsWith(JwtScheme, StringComparison.OrdinalIgnoreCase))
            s = s[JwtScheme.Length..].TrimStart(':', ' ');

        // 去掉可能残留的 `Cookie:` / `Authorization:` 头名
        foreach (var name in new[] { "Cookie:", "Authorization:", "Set-Cookie:" })
        {
            if (s.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                s = s[name.Length..].Trim();
        }

        // JWT 中间可能被换行折断（复制长 token 时常见），移除所有空白
        s = s.Replace("\r", "").Replace("\n", "").Replace("\t", "").Trim();
        return s.Trim('"', '\'');
    }

    /// <summary>
    /// 判定凭证类型：形如 `header.payload.signature` 且 payload 可解出 JSON 的判为 Token；
    /// 其余足够长的非空串判为会话（Trae 的会话串是无结构的长随机串）。
    /// </summary>
    public static CredentialKind Classify(string? raw)
    {
        var s = Normalize(raw);
        if (s.Length == 0) return CredentialKind.Unknown;

        var parts = s.Split('.');
        if (parts.Length == 3 && LooksLikeJwt(s)) return CredentialKind.Token;

        return s.Length >= MinSessionLength ? CredentialKind.Session : CredentialKind.Unknown;
    }

    /// <summary>校验 Token 是否可接受（长度门槛 + 与旧值不同的判定沿用 TokenUtils 口径）。</summary>
    public static bool IsUsableToken(string? token)
        => !string.IsNullOrEmpty(token) && token!.Length >= MinTokenLength;

    /// <summary>校验会话串是否可接受。</summary>
    public static bool IsUsableSession(string? session)
        => !string.IsNullOrEmpty(session) && session!.Length >= MinSessionLength;

    /// <summary>把任意长度的凭证打码展示（首尾各保留 6 位）。</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "（空）";
        if (value!.Length <= 16) return new string('*', 8);
        return value[..6] + "…" + value[^6..];
    }

    /// <summary>JWT 形态判定：三段 base64url，且 payload 能解出 JSON 对象。</summary>
    private static bool LooksLikeJwt(string s)
    {
        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        foreach (var p in parts)
        {
            if (p.Length == 0) return false;
            foreach (var c in p)
            {
                bool ok = char.IsLetterOrDigit(c) || c == '-' || c == '_';
                if (!ok) return false;
            }
        }
        // payload 必须是 JSON（真实 JWT 一定如此；随机串撞出的三段式不会）
        return TryDecodeBase64UrlJson(parts[1]);
    }

    private static bool TryDecodeBase64UrlJson(string segment)
    {
        try
        {
            var padded = segment.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 导入结果：成功与否 + 面向用户的一句话说明 + 解析出的字段。
    /// </summary>
    public sealed class ImportResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public string? Token { get; init; }
        public string? Session { get; init; }
        public CredentialKind Kind { get; init; }
    }

    /// <summary>
    /// 导入一段用户提供的凭证文本：
    ///   · 识别为 Token → 直接作为 Token 返回；
    ///   · 识别为会话 → 调 GetUserToken 换取 Token（同时保留会话以便后续静默续期）。
    /// </summary>
    public static async System.Threading.Tasks.Task<ImportResult> ImportAsync(
        string? raw, TraeCheckin.TraeApiClient? api, CredentialKind forced = CredentialKind.Unknown)
    {
        var s = Normalize(raw);
        if (s.Length == 0)
            return new ImportResult { Ok = false, Message = "内容为空，请先复制凭证" };

        var kind = forced == CredentialKind.Unknown ? Classify(s) : forced;

        if (kind == CredentialKind.Token)
        {
            if (!IsUsableToken(s))
                return new ImportResult { Ok = false, Message = "Token 长度不足，请确认复制完整", Kind = kind };
            return new ImportResult
            {
                Ok = true,
                Kind = kind,
                Token = s,
                Message = "已识别为 Cloud-IDE-Token",
            };
        }

        if (kind == CredentialKind.Session)
        {
            if (!IsUsableSession(s))
                return new ImportResult { Ok = false, Message = "会话串长度不足，请确认复制完整", Kind = kind };

            if (api == null)
                return new ImportResult { Ok = false, Message = "网络组件未就绪，无法用会话换取 Token", Kind = kind };

            var token = await api.GetUserTokenAsync(s);
            if (string.IsNullOrEmpty(token))
            {
                return new ImportResult
                {
                    Ok = false,
                    Kind = kind,
                    Session = s,
                    Message = "已识别为会话 Cookie，但换取 Token 失败（会话可能已过期）：" + (api.LastError ?? "未知原因"),
                };
            }
            return new ImportResult
            {
                Ok = true,
                Kind = kind,
                Token = token,
                Session = s,
                Message = "已识别为会话 Cookie，并成功换取 Token",
            };
        }

        return new ImportResult { Ok = false, Message = "无法识别的凭证格式", Kind = CredentialKind.Unknown };
    }
}
