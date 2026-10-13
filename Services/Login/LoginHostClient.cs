using System.Diagnostics;
using System.Text.Json;

namespace TraeTools.Services.Login;

/// <summary>
/// 独立登录宿主（<c>TraeTools.LoginHost.exe</c>）的调用方（P2-1）。
/// 主程序不再引用 WinForms / WebView2（NativeAOT 的两个硬阻塞），而是：
///   找宿主 exe → 起进程 → 等退出 → 读回临时 JSON。宿主缺失时上层回退到"手动粘贴 Token"。
/// </summary>
public static class LoginHostClient
{
    public sealed record LoginResult(string Token, string? Session);

    /// <summary>宿主可执行文件名。</summary>
    public const string HelperFileName = "TraeTools.LoginHost.exe";

    /// <summary>环境变量覆盖（测试/便携部署用）。</summary>
    public const string HelperPathEnvVar = "TRAETOOLS_LOGIN_HOST";

    /// <summary>宿主 exe 查找顺序：环境变量 → 主程序同目录 → 同目录 loginhost\ 子目录。</summary>
    public static string? ResolveHelperPath()
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable(HelperPathEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env!);
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, HelperFileName));
        candidates.Add(Path.Combine(baseDir, "loginhost", HelperFileName));

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { /* 非法路径跳过 */ }
        }
        return null;
    }

    public static bool IsAvailable() => ResolveHelperPath() != null;

    /// <summary>
    /// 启动宿主并等待其结束，返回取得的凭据；用户取消或超时返回 null。
    /// initialToken 用于让宿主忽略 localStorage 里的残留旧 token（只有"新 token"才被接受）。
    /// </summary>
    public static Task<LoginResult?> RunAsync(
        string userDataDir, string? initialToken, bool debugCapture = false, TimeSpan? timeout = null)
        => RunCoreAsync(userDataDir, initialToken, debugCapture, timeout, null);

    /// <summary>
    /// 核心实现。<paramref name="extraArgs"/> 供独立环境验证注入 <c>--selftest</c>（不显示窗口、直接回写结果），
    /// 生产路径始终传 null。
    /// </summary>
    internal static async Task<LoginResult?> RunCoreAsync(
        string userDataDir, string? initialToken, bool debugCapture, TimeSpan? timeout, IEnumerable<string>? extraArgs)
    {
        var exe = ResolveHelperPath()
            ?? throw new FileNotFoundException("未找到登录宿主程序：" + HelperFileName);

        var outFile = Path.Combine(Path.GetTempPath(), "traetools_login_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--user-data-dir");
            psi.ArgumentList.Add(userDataDir);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(outFile);
            if (!string.IsNullOrEmpty(initialToken))
            {
                psi.ArgumentList.Add("--initial-token");
                psi.ArgumentList.Add(initialToken!);
            }
            if (debugCapture) psi.ArgumentList.Add("--debug-capture");
            if (extraArgs != null)
                foreach (var a in extraArgs) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动登录宿主进程：" + exe);

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(30));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 尽力结束 */ }
                return null;
            }

            if (!File.Exists(outFile)) return null;
            return ParseResult(await File.ReadAllTextAsync(outFile));
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* 清理失败忽略 */ }
        }
    }

    /// <summary>解析宿主回传的 JSON（AOT 友好：只用 JsonDocument，不用反射序列化）。</summary>
    public static LoginResult? ParseResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("token", out var t) || t.ValueKind != JsonValueKind.String) return null;
            var token = t.GetString();
            if (string.IsNullOrWhiteSpace(token)) return null;

            string? session = null;
            if (doc.RootElement.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.String)
                session = s.GetString();

            return new LoginResult(token!, string.IsNullOrWhiteSpace(session) ? null : session);
        }
        catch { return null; }
    }
}
