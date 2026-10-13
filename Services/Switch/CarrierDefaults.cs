using System.Diagnostics;

namespace TraeSwitch.Services;

/// <summary>
/// 客户端（载体）默认值与探测。
/// 新 TRAE 融合版（原 TraeWork / `TRAE SOLO CN` 与 TraeCode 合并）后实测：
///   数据根 = %APPDATA%\Trae CN；进程名 = "Trae CN"；主程序 = "D:\Trae CN\Trae CN.exe"。
/// 旧 "TRAE SOLO CN" 路径在自动迁移后仅剩空壳，只用于"识别过期配置"，不再作为切换目标。
/// </summary>
public static class CarrierDefaults
{
    public static string SettingsDir => TraeTools.Services.DataPaths.SwitchDir;

    /// <summary>客户端主程序文件名（新 TRAE 实测）。</summary>
    public const string ClientExeName = "Trae CN.exe";

    /// <summary>客户端进程名（任务管理器实测）。</summary>
    public const string ClientProcessName = "Trae CN";

    /// <summary>客户端数据根：%APPDATA%\Trae CN。</summary>
    public static string DefaultUserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ClientProcessName);

    public static string DefaultProcessName => ClientProcessName;

    /// <summary>
    /// 旧版客户端名（融合前 / 迁移源）。配置里出现这些名字即说明是"过期配置"，
    /// 需自动重探（见 SettingsStore.Normalize）——旧目录已不含现役登录态。
    /// </summary>
    public static readonly string[] LegacyClientNames = ["TRAE SOLO CN", "TraeWork", "TraeWork CN"];

    /// <summary>某个值（路径/进程名/exe）是否还指向旧版客户端。</summary>
    public static bool IsLegacyClientReference(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && LegacyClientNames.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));

    // ---------------- 指纹白名单 ----------------

    /// <summary>
    /// 快照/切换的固定白名单（15 项，相对数据根；见 TRAE数据文档.md §5.1）。
    /// 关键：<c>state.vscdb</c> 才是登录令牌真源，其 <c>-wal</c>/<c>-shm</c> 边车必须成对快照
    /// （强杀后最新登录写入可能只在 WAL 里）。旧的"扫描目录猜载体"实现漏掉整族 vscdb 文件，
    /// 且一旦被 EnsureFingerprint 固化就永不更新（切换失效的根因 2）。
    /// 这里改为**固定清单**、不再扫描猜测：不存在的条目由 Backup/OverwriteEntry 自动跳过，
    /// 而条目本身必须保留——恢复前要按它删目标侧边车（见 VaultService.RestoreEntrySet）。
    /// </summary>
    public static readonly string[] FingerprintWhitelist =
    [
        @"User\globalStorage\storage.json",                            // 登录态键 + 设备遥测
        @"User\globalStorage\state.vscdb",                             // SQLite：登录令牌真源
        @"User\globalStorage\state.vscdb-wal",                         // WAL 边车（必须与主库成对）
        @"User\globalStorage\state.vscdb-shm",                         // SHM 边车
        @"User\globalStorage\state.vscdb.backup",                      // 令牌库自动备份
        @"machineid",                                                    // 机器标识
        @"aha",                                                          // 目录：设备认证数据
        @"Preferences",
        @"Local State",
        @"Local Storage\leveldb",                                       // 目录：Web 侧登录/偏好 KV
        @"Local Storage\config.db",
        @"Network",                                                      // 目录：Cookie
        @"Partitions\trae-webview",                                     // 目录
        @"Partitions\icube-web-crawler-shared-session-v1.0",            // 目录
        @"Session Storage",                                              // 目录
    ];

    /// <summary>
    /// 指纹"必须项"：缺任意一条即认为指纹过期/不完整，需要重置为固定白名单（P0-3 自愈）。
    /// 至少覆盖 state.vscdb 及其全部边车——旧版固化下来的窄指纹正是缺这些。
    /// </summary>
    public static readonly string[] RequiredFingerprintRels =
    [
        @"User\globalStorage\storage.json",
        @"User\globalStorage\state.vscdb",
        @"User\globalStorage\state.vscdb-wal",
        @"User\globalStorage\state.vscdb-shm",
        @"User\globalStorage\state.vscdb.backup",
    ];

    /// <summary>返回固定 15 项指纹（不依赖目录扫描，保证老/新环境一致、可预期）。</summary>
    public static List<string> DetectFingerprint() => [.. FingerprintWhitelist];

    /// <summary>相对路径规范化（统一反斜杠），用于比较。</summary>
    public static string NormalizeRel(string rel) => rel.Replace('/', '\\').Trim();

    /// <summary>指纹是否为空或缺必须项（需重置为固定白名单）。</summary>
    public static bool IsStaleFingerprint(IEnumerable<string>? fingerprint)
    {
        if (fingerprint == null) return true;
        var set = new HashSet<string>(fingerprint.Select(NormalizeRel), StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return true;
        return RequiredFingerprintRels.Any(r => !set.Contains(NormalizeRel(r)));
    }

    /// <summary>统计白名单中在 rootDir 实际存在的条目数（仅用于日志/展示）。</summary>
    public static int CountPresent(string? rootDir)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return 0;
        return FingerprintWhitelist.Count(rel => Exists(rootDir, rel));
    }

    public static bool Exists(string rootDir, string rel)
    {
        var p = Path.Combine(rootDir, rel);
        return File.Exists(p) || Directory.Exists(p);
    }

    // ---------------- 客户端 exe 探测 ----------------

    /// <summary>
    /// 默认客户端可执行文件路径。
    /// 优先探测正在运行的客户端主模块路径，其次常见安装位置（结果缓存），
    /// 全部未命中时回退到实测默认 D 盘路径；用户可在 settings.json 的 ClientExe 中显式覆盖。
    /// </summary>
    public static string DefaultClientExe
    {
        get
        {
            if (string.IsNullOrEmpty(_detectedExe))
            {
                lock (DetectLock)
                {
                    if (string.IsNullOrEmpty(_detectedExe))
                        _detectedExe = AutoDetectClientExe() ?? @"D:\Trae CN\Trae CN.exe";   // 全部未命中：回退实测默认
                }
            }
            return _detectedExe;
        }
    }

    private static readonly object DetectLock = new();
    private static string _detectedExe = "";

    /// <summary>清空探测缓存，下次读取 DefaultClientExe 会重新探测（供 UI「重新探测」使用）。</summary>
    public static void ResetDetection() => _detectedExe = "";

    /// <summary>按运行进程 → 候选目录探测客户端 exe；均未命中返回 null。</summary>
    public static string? AutoDetectClientExe()
    {
        // 1) 正在运行的客户端：主模块路径最可靠（配置里存错路径时自动纠正，P0-1）
        var running = TryGetRunningClientExe();
        if (running != null) return running;

        // 2) 候选安装目录：精确文件名优先，其次目录内名字含 Trae 的主程序，最后目录内唯一 exe
        foreach (var dir in CandidateClientDirs())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var exact = Path.Combine(dir, ClientExeName);
                if (File.Exists(exact)) return exact;

                var exes = Directory.GetFiles(dir, "*.exe");
                var hit = exes.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f).Contains("Trae", StringComparison.OrdinalIgnoreCase))
                    ?? (exes.Length == 1 ? exes[0] : null);
                if (hit != null) return hit;
            }
            catch { /* 单目录判断失败继续下一个 */ }
        }
        return null;
    }

    /// <summary>从正在运行的 "Trae CN" 进程读取主模块路径（只读；失败/无进程返回 null）。</summary>
    private static string? TryGetRunningClientExe()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(ClientProcessName))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
                catch { /* 32/64 位或权限问题：忽略该进程 */ }
                finally { try { p.Dispose(); } catch { /* 忽略 */ } }
            }
        }
        catch { /* 枚举进程失败忽略 */ }
        return null;
    }

    /// <summary>候选客户端目录：实测默认 + 便携版位置 + 各固定盘的常见安装目录。</summary>
    private static List<string> CandidateClientDirs()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var dirs = new List<string>
        {
            @"D:\Trae CN",                                        // 实测安装位置（安装器允许装到任意盘根）
            @"D:\Trae CN\Trae CN",                               // 少数安装器的嵌套目录
            Path.Combine(appData, ClientProcessName),              // 便携/绿色版
            Path.Combine(localAppData, "Programs", ClientProcessName),
            Path.Combine(programFiles, ClientProcessName),
            Path.Combine(localAppData, ClientProcessName),
        };
        try
        {
            foreach (var drv in DriveInfo.GetDrives())
            {
                if (!drv.IsReady || drv.DriveType != DriveType.Fixed) continue;
                var root = drv.RootDirectory.FullName;
                dirs.Add(Path.Combine(root, ClientProcessName));
                dirs.Add(Path.Combine(root, "Program Files", ClientProcessName));
                dirs.Add(Path.Combine(root, "Program Files (x86)", ClientProcessName));
            }
        }
        catch { /* 枚举磁盘失败忽略 */ }
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
