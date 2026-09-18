using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace TraeTools.Services;

/// <summary>
/// 应用版本信息读取：优先读取 exe 同目录的 version.json（外置配置），
/// 缺失时回退到 AssemblyInformationalVersionAttribute（编译时嵌入）。
///
/// version.json 与 csproj &lt;InformationalVersion&gt; 的同步约定：
///   发版前手工保持两者一致；外置文件的目的是让分发出去的 exe 也可被替换 version.json
///   而不必重编（方便小版本号修订/紧急热修补版本号）。
/// </summary>
public static class AppVersion
{
    private static readonly Lazy<AppVersionInfo> _info = new(Load);

    public static string Display => _info.Value.Display;
    public static string Tag => _info.Value.Tag;
    public static string Version => _info.Value.Version;
    public static string Channel => _info.Value.Channel;
    public static string BuildDate => _info.Value.BuildDate;
    public static DateTime? BuildDateTime => _info.Value.BuildDateTime;

    private static AppVersionInfo Load()
    {
        // 1) 优先读取 exe 同目录的 version.json（外置文件覆盖嵌入版本）
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "version.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(version))
                {
                    var channel = root.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? "stable" : "stable";
                    var buildDate = root.TryGetProperty("buildDate", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() ?? "" : "";
                    DateTime? buildDt = null;
                    if (DateTime.TryParse(buildDate, out var parsed)) buildDt = parsed;
                    return new AppVersionInfo(version, channel, buildDate, buildDt);
                }
            }
        }
        catch { /* JSON 解析失败回退嵌入版本 */ }

        // 2) 回退到程序集 InformationalVersion
        var asmVer = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "1.0.0";
        return new AppVersionInfo(asmVer, "stable", "", null);
    }

    private sealed record AppVersionInfo(string Version, string Channel, string BuildDate, DateTime? BuildDateTime)
    {
        /// <summary>展示用：v1.0.2.2</summary>
        public string Display => $"v{Version}";

        /// <summary>Git tag 格式：v1.0.2.2</summary>
        public string Tag => $"v{Version}";
    }
}