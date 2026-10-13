using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TraeTools.Services;

/// <summary>
/// 应用内自更新：从 GitHub Releases 查询最新正式版，下载更新包到本地，
/// 并通过一个独立 PowerShell 脚本在旧进程退出后替换**整套程序文件**并重启新版。
///
/// 更新包形态（P2-4，配合 NativeAOT 上线）：
///   · 首选 <c>TraeTools-win-x64.zip</c>：NativeAOT 产物**不是单文件**，
///     libSkiaSharp / av_libglesv2 / e_sqlite3 / libHarfBuzzSharp / libsodium 必须与 exe 同目录。
///     只替换 exe 会得到"缺 DLL 打不开"的残废程序，因此改为一整套文件的解包替换。
///   · 回退 <c>TraeTools.exe</c>：兼容只发了单文件 exe 的历史版本（老客户端首次升级路径）。
/// 校验：GitHub Release API 的 asset <c>digest</c> 字段（sha256:...）可用时逐字节校验；
/// 不可用时退化为"解包结构校验"（必须是合法 zip 且含 TraeTools.exe）。
/// </summary>
public static class UpdaterService
{
    public const string Repo = "star620/TraeTools";

    /// <summary>首选更新包资源名（AOT 目录版整包）。</summary>
    public const string PackageAssetName = "TraeTools-win-x64.zip";

    /// <summary>历史单文件 exe 资源名（兼容老客户端升级）。</summary>
    public const string LegacyExeAssetName = "TraeTools.exe";

    /// <summary>主程序文件名（更新后的启动目标）。</summary>
    public const string AppExeName = "TraeTools.exe";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub REST API 对缺 User-Agent 的匿名请求会 403，必须带上
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"TraeTools/{CurrentVersion}");
        return h;
    }

    /// <summary>运行中程序的版本号。统一从 AppVersion（version.json 优先，InformationalVersion 兜底）读取，
    /// 与"关于"页展示保持同源，避免出现"显示 v1.0.2.2 / 却判 v1.0.2.0 为旧"的错位。</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = AppVersion.Version?.Trim() ?? "";
            // GitHub tag 常见形态："v1.0.2.2" / "1.0.2.2" / "1.0.2.2-beta"。先把前导 v 去掉。
            if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V')) v = v[1..];
            // 仅取主版本号段（-xxx 后缀截断）
            var dash = v.IndexOfAny(new[] { '-', '+' });
            if (dash >= 0) v = v[..dash];
            return Version.TryParse(v, out var ver) ? ver : new Version(1, 0, 0);
        }
    }

    /// <param name="AssetUrl">下载地址。</param>
    /// <param name="AssetName">资源文件名（决定是 zip 整包还是单个 exe）。</param>
    /// <param name="IsZip">true = 目录版整包（解包后替换整套文件）；false = 单文件 exe 替换。</param>
    /// <param name="Sha256Hex">GitHub asset digest 提供的期望 SHA-256（可能为空）。</param>
    public sealed record ReleaseInfo(
        string Tag, Version Version, string AssetUrl, string AssetName, long Size, bool IsZip, string? Sha256Hex);

    // GitHub 匿名 API 每小时最多 60 次；自动检查若紧跟频繁启动会很快耗尽配额（返回 403）。
    // 用 1 小时磁盘缓存：自动（静默）检查在窗口内直接沿用上次结论，不反复打 API。
    private static readonly TimeSpan CacheWindow = TimeSpan.FromHours(1);
    private static string CacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TraeTools", "update_check_cache.txt");

    /// <summary>远程 release 中发现比本地更新的版本时返回其信息；否则返回 null。</summary>
    public static async Task<ReleaseInfo?> GetNewerAsync(CancellationToken ct = default, bool useCache = true)
    {
        // 自动检查 + 1 小时内刚查过 → 不联网，沿用"已是最新"结论
        if (useCache && IsCacheFresh()) return null;
        try
        {
            return await FetchLatestAsync(ct);
        }
        finally
        {
            TouchCache();   // 无论成败都记录本次检查，避免高频启动反复打 API
        }
    }

    private static bool IsCacheFresh()
    {
        try
        {
            if (!File.Exists(CacheFile)) return false;
            var ts = long.Parse(File.ReadAllText(CacheFile).Trim());
            if (ts <= 0) return false;
            var last = new DateTime(ts, DateTimeKind.Utc);
            return DateTime.UtcNow - last < CacheWindow;
        }
        catch { return false; }
    }

    private static void TouchCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, DateTime.UtcNow.Ticks.ToString());
        }
        catch { /* 缓存写失败不影响更新功能 */ }
    }

    private static async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync(
            $"https://api.github.com/repos/{Repo}/releases/latest",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 403 &&
                resp.Headers.TryGetValues("X-RateLimit-Reset", out var resetVals) &&
                long.TryParse(resetVals.FirstOrDefault(), out var resetUnix))
            {
                var wait = DateTimeOffset.FromUnixTimeSeconds(resetUnix).ToLocalTime();
                throw new HttpRequestException(
                    $"GitHub 更新限流（每小时最多 60 次），请到 {wait:HH:mm} 后再检查更新。");
            }
            throw new HttpRequestException($"更新检查失败：HTTP {(int)resp.StatusCode}");
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tag) ||
            !TryParseTag(tag.GetString() ?? "", out var ver))
            return null;
        if (ver <= CurrentVersion) return null;   // 本地已是最新（>= 远程）

        var picked = PickAsset(root);
        return picked == null ? null : new ReleaseInfo(tag.GetString()!, ver, picked.Value.Url, picked.Value.Name,
            picked.Value.Size, picked.Value.IsZip, picked.Value.Sha256Hex);
    }

    /// <summary>从 release 的 assets 里挑更新资源：优先整包 zip，回退单文件 exe。internal 以便独立环境验证。</summary>
    internal static (string Url, string Name, long Size, bool IsZip, string? Sha256Hex)? PickAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        (string Url, string Name, long Size, bool IsZip, string? Sha256Hex)? fallback = null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url)) continue;
            long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            var sha = ParseDigest(a);

            if (string.Equals(name, PackageAssetName, StringComparison.OrdinalIgnoreCase))
                return (url!, name, size, true, sha);
            if (fallback == null && string.Equals(name, LegacyExeAssetName, StringComparison.OrdinalIgnoreCase))
                fallback = (url!, name, size, false, sha);
        }
        return fallback;
    }

    /// <summary>解析 asset 的 digest 字段（形如 "sha256:ab12..."）；非 sha256 或缺失返回 null。internal 以便独立环境验证。</summary>
    internal static string? ParseDigest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var d) || d.ValueKind != JsonValueKind.String) return null;
        var v = d.GetString() ?? "";
        const string prefix = "sha256:";
        return v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? v[prefix.Length..].Trim() : null;
    }

    /// <summary>把 "v1.2.3" / "1.2.3" / "1.2.3-beta" 解析为 Version；失败返回 false。</summary>
    public static bool TryParseTag(string tag, out Version ver)
    {
        ver = new Version();
        var s = tag.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        var parts = s.Split('.');
        if (parts.Length < 2) return false;
        var items = new int[4];
        for (var i = 0; i < 4; i++)
        {
            var part = i < parts.Length ? parts[i].Split('-', '+')[0] : "0";
            if (!int.TryParse(part, out items[i])) return false;
        }
        ver = new Version(items[0], items[1], items[2], items[3]);
        return true;
    }

    /// <summary>下载更新资源到 destDir\fileName 并返回完整路径；sink 用于汇报进度。</summary>
    public static async Task<string> DownloadAsync(string url, string destDir, string fileName,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var dest = Path.Combine(destDir, fileName);
        await using var fs = File.Create(dest);
        var buf = new byte[81920];
        long done = 0; int read;
        while ((read = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            if (total > 0) progress?.Report((int)(done * 100 / total));
        }
        return dest;
    }

    /// <summary>SHA-256 逐字节校验；<paramref name="expectedHex"/> 为空时视为"无校验值"返回 true。</summary>
    public static bool VerifySha256(string filePath, string? expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return true;
        try
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(filePath);
            var actual = Convert.ToHexString(sha.ComputeHash(fs));
            return string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 把下载好的整包解到 targetDir 内的暂存目录（同卷 → 后续替换可走原子 rename），返回暂存目录。
    /// 解包即做结构校验：必须是合法 zip 且根目录含 <see cref="AppExeName"/>。
    /// </summary>
    public static string PreparePackage(string packagePath, string targetDir)
    {
        var staging = Path.Combine(targetDir, ".~update");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(packagePath, staging, overwriteFiles: true);
        if (!File.Exists(Path.Combine(staging, AppExeName)))
            throw new InvalidDataException($"更新包结构不合法：根目录缺少 {AppExeName}");
        return staging;
    }

    /// <summary>
    /// 用独立 PowerShell 进程异步应用更新：反复重试直到旧进程退出（文件解锁）后，
    /// 把暂存目录里的**整套文件**逐个覆盖到目标目录，清掉暂存，再启动新版。
    /// 任一文件失败即整体重试（<c>-ErrorAction Stop</c> 保证复制失败会真正抛错被 catch 住），
    /// 避免"半新半旧"。
    ///
    /// 注意：脚本以 Windows PowerShell 5.1（.NET Framework）执行，两处坑已实测确认：
    ///   · <c>[System.IO.File]::Move(src,dst,true)</c> 三参重载在 5.1 **不存在**（MethodNotFound）；
    ///   · 脚本体里 <c>$</c> 不能写成 C# 式的反引号转义（`` `$ ``），5.1 会解析报错。
    /// 故统一用 <c>Copy-Item -Force</c> + 纯 <c>$</c> 变量。
    /// </summary>
    public static void ApplyInBackground(string stagingDir, string targetDir)
    {
        var ps1 = Path.Combine(Path.GetTempPath(), $"apply_update_{Guid.NewGuid():N}.ps1");
        var body = $@"
$stage = ""{stagingDir}""
$dst   = ""{targetDir}""
$exe   = ""{AppExeName}""
$tries = 120
for ($i = 0; $i -lt $tries; $i++) {{
  $ok = $true
  try {{
    foreach ($f in Get-ChildItem -LiteralPath $stage -Recurse -File) {{
      $rel = $f.FullName.Substring($stage.Length).TrimStart('\')
      $target = Join-Path $dst $rel
      $dir = Split-Path $target -Parent
      if (-not (Test-Path -LiteralPath $dir)) {{ New-Item -ItemType Directory -Path $dir -Force | Out-Null }}
      Copy-Item -LiteralPath $f.FullName -Destination $target -Force -ErrorAction Stop
    }}
  }} catch {{ $ok = $false }}
  if ($ok) {{ break }}
  Start-Sleep -Milliseconds 250
}}
# 只有整套文件都替换成功才清暂存并重启；否则保留 .~update 供手动/下次恢复，
# 避免半新半旧的安装目录被启动。
if ($ok) {{
  Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
  $newExe = Join-Path $dst $exe
  if (Test-Path -LiteralPath $newExe) {{ Start-Process -FilePath $newExe }}
}}
Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
";
        File.WriteAllText(ps1, body, new UTF8Encoding(true));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        Process.Start(psi);
    }
}
