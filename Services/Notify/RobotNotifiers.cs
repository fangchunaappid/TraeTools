using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TraeTools.Services.Json;

namespace TraeTools.Services.Notify;

/// <summary>推送结果（统一结构，便于界面显示每个渠道的成败）。</summary>
public sealed class NotifyResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";

    public static NotifyResult Fail(string msg) => new() { Ok = false, Message = msg };
    public static NotifyResult Ok0(string msg = "已发送") => new() { Ok = true, Message = msg };
}

/// <summary>
/// 钉钉 / 企业微信共用的文本消息体（两者协议一致：<c>{"msgtype":"text","text":{"content":"…"}}</c>）。
///
/// 为什么必须是具名类型：本项目的 JSON 一律走 NativeAOT 安全的**源生成**（见 Services/Json/AppJson.cs），
/// 而源生成器无法为匿名类型生成元数据 —— 用匿名类型会在裁剪/AOT 下静默失败。
/// </summary>
internal sealed class TextRobotPayload
{
    [JsonPropertyName("msgtype")]
    public string MsgType { get; init; } = "text";

    [JsonPropertyName("text")]
    public TextRobotBody Text { get; init; } = new();
}

/// <summary>文本消息体（<see cref="TextRobotPayload"/> 的嵌套对象）。</summary>
internal sealed class TextRobotBody
{
    [JsonPropertyName("content")]
    public string Content { get; init; } = "";
}

/// <summary>Telegram <c>sendMessage</c> 请求体。</summary>
internal sealed class TelegramSendPayload
{
    [JsonPropertyName("chat_id")]
    public string ChatId { get; init; } = "";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [JsonPropertyName("disable_web_page_preview")]
    public bool DisableWebPagePreview { get; init; }
}

/// <summary>
/// 基于 HTTP 的推送渠道集合：通用 Webhook、钉钉机器人、企业微信群机器人、Telegram Bot。
///
/// 四者共同点是「一次 HTTP 请求即可完成投递」，因此不引入任何第三方 SDK，
/// 只用 HttpClient 与 System.Text.Json，两个平台行为完全一致。
/// 全部渠道都做了「失败不抛异常、只返回结果」的约定，单个渠道故障不影响其它渠道与签到主流程。
/// </summary>
public static class RobotNotifiers
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // ==================== 通用 Webhook ====================

    /// <summary>
    /// 通用 Webhook：把 {title} / {content} 占位符替换进用户自定义的请求体模板后 POST。
    /// 一份配置即可兼容 Server酱（{"title":"…","desp":"{content}"}）、
    /// PushPlus、Bark、以及任何接受 JSON 的自建服务。
    /// </summary>
    public static async Task<NotifyResult> SendWebhookAsync(
        string? url, string? bodyTemplate, string? contentType, string title, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return NotifyResult.Fail("Webhook 地址为空");

        var template = string.IsNullOrWhiteSpace(bodyTemplate)
            ? "{\"title\":\"{title}\",\"content\":\"{content}\"}"
            : bodyTemplate!;

        // 替换时对内容做 JSON 字符串转义，避免消息里的引号/换行把请求体破坏成非法 JSON
        var body = template
            .Replace("{title}", JsonEscape(title), StringComparison.Ordinal)
            .Replace("{content}", JsonEscape(content), StringComparison.Ordinal);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(body, Encoding.UTF8,
                string.IsNullOrWhiteSpace(contentType) ? "application/json" : contentType!);
            using var resp = await Http.SendAsync(req, ct);
            var text = await SafeReadAsync(resp, ct);
            return resp.IsSuccessStatusCode
                ? NotifyResult.Ok0($"HTTP {(int)resp.StatusCode}")
                : NotifyResult.Fail($"HTTP {(int)resp.StatusCode} {Truncate(text)}");
        }
        catch (Exception ex)
        {
            return NotifyResult.Fail("请求失败：" + ex.Message);
        }
    }

    // ==================== 钉钉群机器人 ====================

    /// <summary>
    /// 钉钉自定义机器人。地址可直接粘贴完整 webhook；
    /// 若机器人启用了「加签」安全设置，把密钥一并填入即可自动完成 HMAC-SHA256 签名
    /// （否则钉钉会返回 310000 sign not match）。
    /// </summary>
    public static async Task<NotifyResult> SendDingTalkAsync(
        string? webhook, string? secret, string title, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webhook)) return NotifyResult.Fail("钉钉 Webhook 为空");

        var url = webhook!.Trim();
        if (!string.IsNullOrWhiteSpace(secret))
        {
            try
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var toSign = ts + "\n" + secret!.Trim();
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret!.Trim()));
                var sign = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(toSign)));
                var sep = url.Contains('?') ? "&" : "?";
                url = $"{url}{sep}timestamp={ts}&sign={Uri.EscapeDataString(sign)}";
            }
            catch (Exception ex)
            {
                return NotifyResult.Fail("加签失败：" + ex.Message);
            }
        }

        // 具名类型 + 源生成（匿名类型无法被源生成器登记）
        var payload = AppJson.Serialize(new TextRobotPayload
        {
            Text = new TextRobotBody { Content = $"{title}\n{content}" },
        });
        return await PostJsonAsync(url, payload, "钉钉", ct);
    }

    // ==================== 企业微信群机器人 ====================

    /// <summary>企业微信（群机器人）：完整 webhook 地址已含 key 参数，直接 POST 即可。</summary>
    public static async Task<NotifyResult> SendWeComAsync(
        string? webhook, string title, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webhook)) return NotifyResult.Fail("企业微信 Webhook 为空");
        var payload = AppJson.Serialize(new TextRobotPayload
        {
            Text = new TextRobotBody { Content = $"{title}\n{content}" },
        });
        return await PostJsonAsync(webhook!.Trim(), payload, "企业微信", ct);
    }

    // ==================== Telegram Bot ====================

    /// <summary>
    /// Telegram Bot：POST https://api.telegram.org/bot&lt;token&gt;/sendMessage。
    /// 注意：该域名在国内网络下通常不可直连，需要代理；
    /// 请求失败时会明确提示这一点，避免用户误以为是配置写错。
    /// </summary>
    public static async Task<NotifyResult> SendTelegramAsync(
        string? botToken, string? chatId, string title, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(botToken)) return NotifyResult.Fail("Bot Token 为空");
        if (string.IsNullOrWhiteSpace(chatId)) return NotifyResult.Fail("Chat ID 为空");

        var token = botToken!.Trim();
        // 容错：用户可能把整条 URL 粘进来，这里只取 token 部分
        if (token.Contains("api.telegram.org"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(token, @"/bot(?<t>[^/]+)/");
            if (m.Success) token = m.Groups["t"].Value;
        }

        var url = $"https://api.telegram.org/bot{token}/sendMessage";
        var payload = AppJson.Serialize(new TelegramSendPayload
        {
            ChatId = chatId!.Trim(),
            Text = $"{title}\n{content}",
            DisableWebPagePreview = true,
        });

        return await PostJsonAsync(url, payload, "Telegram", ct);
    }

    // ==================== 公共 ====================

    private static async Task<NotifyResult> PostJsonAsync(string url, string payload, string label, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            var text = await SafeReadAsync(resp, ct);
            if (resp.IsSuccessStatusCode)
            {
                // 钉钉/企业微信即使 HTTP 200 也可能在响应体里报错（errcode != 0）
                if (TryReadErrCode(text, out var code, out var msg) && code != 0)
                    return NotifyResult.Fail($"{label}返回错误 {code}：{msg}");
                return NotifyResult.Ok0($"{label} HTTP {(int)resp.StatusCode}");
            }
            return NotifyResult.Fail($"{label} HTTP {(int)resp.StatusCode} {Truncate(text)}");
        }
        catch (Exception ex)
        {
            // Telegram 的域名在国内网络下通常不可直连，失败时明确提示，避免用户误以为配置写错
            var hint = label == "Telegram" ? "（Telegram 在国内网络通常需代理，请确认网络环境）" : "";
            return NotifyResult.Fail($"{label}请求失败：{ex.Message}{hint}");
        }
    }

    /// <summary>解析钉钉/企业微信响应体中的 errcode/errmsg（Telegram 用 ok 字段，不适用）。</summary>
    private static bool TryReadErrCode(string text, out int code, out string message)
    {
        code = 0;
        message = "";
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("errcode", out var ec) && ec.TryGetInt32(out var c))
            {
                code = c;
                if (root.TryGetProperty("errmsg", out var em) && em.ValueKind == JsonValueKind.String)
                    message = em.GetString() ?? "";
                return true;
            }
        }
        catch { /* 非 JSON 响应则忽略 */ }
        return false;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var one = s!.Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length <= 160 ? one : one[..160] + "…";
    }

    /// <summary>把一段文本转义为可安全嵌入 JSON 字符串字面量的形式（不含两侧引号）。</summary>
    public static string JsonEscape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s!.Length + 16);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
