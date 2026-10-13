using System.Text.Json;

namespace TraeSwitch.Services;

/// <summary>
/// 切换后"是否真的进入了账号"的只读探测：
/// 读客户端 storage.json 里的登录凭据键（iCubeAuthInfo://*）判断是否已登录，
/// 用文件长度/修改时间判断客户端启动后是否真的写入过会话（能写 = 云会话在工作）。
/// </summary>
public static class AuthProbe
{
    /// <summary>登录凭据键：两把都存在且非空才视为"已登录"。</summary>
    private static readonly string[] AuthKeys =
        ["iCubeAuthInfo://usertag", "iCubeAuthInfo://icube.cloudide"];

    public static string StoragePath(string rootDir) =>
        Path.Combine(rootDir, "User", "globalStorage", "storage.json");

    public sealed record Stamp(long Len, DateTime Utc);

    /// <summary>取文件指纹；文件不存在返回 null。</summary>
    public static Stamp? GetStamp(string path)
    {
        if (!File.Exists(path)) return null;
        return new Stamp(new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
    }

    public static bool StampChanged(Stamp? baseline, Stamp? now)
    {
        if (baseline == null && now == null) return false;
        if (baseline == null || now == null) return true;
        return baseline != now;
    }

    /// <summary>
    /// 读登录凭据是否齐全。返回语义（P1-1 的成功判据基础）：
    /// true=已登录（两把键都非空）；false=**确定**未登录（键缺失/为空——客户端 clearUserInfo 的表现）；
    /// null=暂时读不到/读不完整（文件被占用，或客户端正在整体重写导致 JSON 截断），不要据此判"未登录"。
    /// </summary>
    public static bool? HasAuthCreds(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch { return null; }
        return HasAuthCredsText(json);
    }

    /// <summary>同 <see cref="HasAuthCreds"/>，但直接对 JSON 文本判定（解析失败视为瞬态 null）。</summary>
    public static bool? HasAuthCredsText(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return null; }   // 客户端正在改写/截断：瞬态，不能当"已登出"
        using (doc)
        {
            foreach (var key in AuthKeys)
            {
                if (!doc.RootElement.TryGetProperty(key, out var v) ||
                    v.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(v.GetString()))
                    return false;
            }
            return true;
        }
    }
}
