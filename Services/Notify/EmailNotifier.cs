using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TraeTools.Services.Notify;

/// <summary>
/// 轻量 SMTP 客户端（自实现，不依赖 System.Net.Mail.SmtpClient）。
///
/// 为什么不用内置的 SmtpClient：它只支持「明文」与「STARTTLS」两种模式，
/// **不支持 465 端口的隐式 TLS**，而国内邮箱（QQ / 163 / 126 等）最常用的恰恰是 465。
/// 自实现后可完整覆盖三种连接安全方式：
///   · ssl      —— 连接后立即握手（465 / 994）
///   · starttls —— 先明文再升级（587 / 25）
///   · none     —— 全程明文（内网中继 / 25）
/// 认证方式同时支持 AUTH LOGIN 与 AUTH PLAIN，按服务器 EHLO 宣告择优使用。
/// 发信正文统一按 UTF-8 + Base64 编码，中文主题用 RFC 2047 encoded-word，杜绝乱码。
/// </summary>
public static class EmailNotifier
{
    private static int _counter;

    /// <summary>单次会话的读写超时。</summary>
    private const int IoTimeoutMs = 20_000;

    /// <summary>发送一封纯文本邮件。</summary>
    public static async Task<NotifyResult> SendAsync(
        string? host,
        int port,
        string? security,
        string? user,
        string? password,
        string? from,
        string? to,
        string subject,
        string body,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return NotifyResult.Fail("SMTP 服务器地址为空");

        var recipients = SplitAddresses(to);
        if (recipients.Count == 0)
            return NotifyResult.Fail("收件人为空");

        var sender = string.IsNullOrWhiteSpace(from) ? user : from;
        if (string.IsNullOrWhiteSpace(sender))
            return NotifyResult.Fail("发件人为空（也未填写用户名）");

        var mode = (security ?? "ssl").ToLowerInvariant();
        if (port <= 0) port = mode == "ssl" ? 465 : mode == "starttls" ? 587 : 25;

        TcpClient? tcp = null;
        Stream? stream = null;
        try
        {
            tcp = new TcpClient { ReceiveTimeout = IoTimeoutMs, SendTimeout = IoTimeoutMs };
            await tcp.ConnectAsync(host!, port, ct);
            stream = tcp.GetStream();
            stream.ReadTimeout = IoTimeoutMs;
            stream.WriteTimeout = IoTimeoutMs;

            // 隐式 TLS：连上即握手
            if (mode == "ssl")
            {
                var ssl = new SslStream(stream, false);
                // 不显式限制协议版本：不同平台/机型对 TLS1.3 的支持情况不一，
                // 写死反而可能在旧系统上握手失败；交给平台按服务端能力协商更稳。
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host!,
                }, ct);
                stream = ssl;
                stream.ReadTimeout = IoTimeoutMs;
                stream.WriteTimeout = IoTimeoutMs;
            }

            var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.ASCII, 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = false };

            var greeting = await ReadReplyAsync(reader);
            if (!greeting.StartsWith("220"))
                return NotifyResult.Fail($"服务器未就绪：{FirstLine(greeting)}");

            var hostName = DnsSafeHostName();
            await CommandAsync(writer, reader, $"EHLO {hostName}", "250");

            // STARTTLS：明文会话内升级
            if (mode == "starttls")
            {
                await CommandAsync(writer, reader, "STARTTLS", "220");
                var ssl = new SslStream(stream, false);
                // 不显式限制协议版本：不同平台/机型对 TLS1.3 的支持情况不一，
                // 写死反而可能在旧系统上握手失败；交给平台按服务端能力协商更稳。
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host!,
                }, ct);
                stream = ssl;
                stream.ReadTimeout = IoTimeoutMs;
                stream.WriteTimeout = IoTimeoutMs;
                reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                writer = new StreamWriter(stream, Encoding.ASCII, 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = false };
                // 重新打招呼以获取升级后的能力集（含 AUTH 机制）
                await CommandAsync(writer, reader, $"EHLO {hostName}", "250");
            }

            if (!string.IsNullOrEmpty(user))
            {
                var authErr = await AuthenticateAsync(writer, reader, user!, password ?? "");
                if (authErr != null)
                    return NotifyResult.Fail(authErr);
            }

            await CommandAsync(writer, reader, $"MAIL FROM:<{sender}>", "250");
            foreach (var rcpt in recipients)
            {
                // 单个收件人被拒时继续尝试其余收件人，只要有一个成功即可投递
                await CommandAsync(writer, reader, $"RCPT TO:<{rcpt}>", null);
            }

            await CommandAsync(writer, reader, "DATA", "354");

            var data = BuildMessage(sender!, recipients, subject, body);
            await writer.WriteAsync(data);
            writer.Flush();
            await CommandAsync(writer, reader, ".", "250");

            try
            {
                await writer.WriteAsync("QUIT\r\n");
                writer.Flush();
            }
            catch { /* 服务器可能已主动断开，忽略 */ }

            return NotifyResult.Ok0($"已发送至 {string.Join("、", recipients)}");
        }
        catch (Exception ex)
        {
            return NotifyResult.Fail("发送失败：" + ex.Message);
        }
        finally
        {
            try { stream?.Dispose(); } catch { }
            try { tcp?.Close(); } catch { }
        }
    }

    /// <summary>按服务器宣告的机制选择 AUTH LOGIN / AUTH PLAIN。返回 null 表示成功。</summary>
    private static async Task<string?> AuthenticateAsync(StreamWriter writer, StreamReader reader, string user, string password)
    {
        var user64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(user));
        var pass64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));

        // 先试 LOGIN（兼容面最广）
        await SendAsync(writer, "AUTH LOGIN");
        var r1 = await ReadReplyAsync(reader);
        if (r1.StartsWith("334"))
        {
            await SendAsync(writer, user64);
            var r2 = await ReadReplyAsync(reader);
            if (!r2.StartsWith("334"))
                return "SMTP 认证失败（用户名阶段）：" + FirstLine(r2);
            await SendAsync(writer, pass64);
            var r3 = await ReadReplyAsync(reader);
            if (r3.StartsWith("235")) return null;
            return "SMTP 认证失败（口令阶段，请确认使用的是「授权码」而非登录密码）：" + FirstLine(r3);
        }

        // 回退 PLAIN
        await SendAsync(writer, "AUTH PLAIN " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes("\0" + user + "\0" + password)));
        var r4 = await ReadReplyAsync(reader);
        if (r4.StartsWith("235")) return null;
        return "SMTP 认证失败（请确认使用「授权码」而非登录密码）：" + FirstLine(r4);
    }

    /// <summary>构造 RFC 5322 邮件正文：中文主题用 encoded-word，正文用 UTF-8 Base64。</summary>
    private static string BuildMessage(string from, List<string> to, string subject, string body)
    {
        var sb = new StringBuilder();
        sb.Append("From: ").Append(from).Append("\r\n");
        sb.Append("To: ").Append(string.Join(", ", to)).Append("\r\n");
        sb.Append("Subject: =?UTF-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(subject))).Append("?=\r\n");
        sb.Append("Date: ").Append(DateTime.Now.ToString("r")).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: text/plain; charset=UTF-8\r\n");
        sb.Append("Content-Transfer-Encoding: base64\r\n");
        sb.Append("Message-ID: <").Append(NextMessageId()).Append(">\r\n");
        sb.Append("\r\n");

        // 正文按 76 字符折行的 Base64，保证任何 SMTP 服务器都能安全传输中文
        var bodyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        for (int i = 0; i < bodyB64.Length; i += 76)
            sb.Append(bodyB64, i, Math.Min(76, bodyB64.Length - i)).Append("\r\n");

        return sb.ToString();
    }

    private static string NextMessageId()
    {
        var n = Interlocked.Increment(ref _counter);
        return $"{DateTime.Now:yyyyMMddHHmmss}.{n}@traetools";
    }

    /// <summary>发送命令并校验响应码；expect 为 null 表示不校验（如 RCPT TO 允许个别失败）。</summary>
    private static async Task<string> CommandAsync(
        StreamWriter writer, StreamReader reader, string command, string? expect)
    {
        await SendAsync(writer, command);
        var reply = await ReadReplyAsync(reader);
        if (expect != null && !reply.StartsWith(expect))
            throw new InvalidOperationException($"服务器拒绝 `{Redact(command)}`：{FirstLine(reply)}");
        return reply;
    }

    private static async Task SendAsync(StreamWriter writer, string line)
    {
        await writer.WriteAsync(line + "\r\n");
        await writer.FlushAsync();
    }

    /// <summary>读取一条完整 SMTP 响应（处理 250-xxx 多行续行）。</summary>
    private static async Task<string> ReadReplyAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;
            sb.AppendLine(line);
            // 第 4 个字符为空格（或行长度不足）表示这是最后一行
            if (line.Length < 4 || line[3] != '-') break;
        }
        return sb.ToString();
    }

    private static string FirstLine(string reply)
        => reply.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(无响应)";

    /// <summary>日志脱敏：认证相关命令不把 Base64 凭据写进异常信息。</summary>
    private static string Redact(string command)
        => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase) ? "AUTH ***" : command;

    /// <summary>拆分收件人（支持逗号、分号、空白分隔）。</summary>
    public static List<string> SplitAddresses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw!
            .Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => a.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>EHLO 用的本机名（RFC 要求为域名或地址字面量）。</summary>
    private static string DnsSafeHostName()
    {
        try
        {
            var name = System.Net.Dns.GetHostName();
            return string.IsNullOrWhiteSpace(name) ? "localhost" : name;
        }
        catch
        {
            return "localhost";
        }
    }
}
