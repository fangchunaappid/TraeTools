namespace TraeSwitch.Services;

public static class CarrierDefaults
{
    public static string SettingsDir => TraeTools.Services.DataPaths.SwitchDir;

    public static string DefaultUserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TRAE SOLO CN");

    /// <summary>
    /// 默认 Trae 客户端可执行文件路径。
    /// 优先自动探测常见安装位置（已缓存结果），全部未命中时回退到历史默认 D 盘路径，
    /// 用户可在 settings.json 的 ClientExe 中显式覆盖。
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
                        _detectedExe = AutoDetectClientExe() ?? @"D:\TRAE SOLO CN\TRAE SOLO CN.exe";   // 全部未命中：回退历史默认
                }
            }
            return _detectedExe;
        }
    }

    public static string DefaultProcessName => "TRAE SOLO CN";

    private static readonly object DetectLock = new();
    private static string _detectedExe = "";

    /// <summary>按候选列表探测客户端 exe；命中即返回完整路径，未命中返回 null。</summary>
    private static string? AutoDetectClientExe()
    {
        const string exeName = "TRAE SOLO CN.exe";
        var candidates = new List<string>
        {
            @"D:\TRAE SOLO CN\" + exeName,                                                   // 历史默认（D 盘安装）
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TRAE SOLO CN", exeName), // 绿色版/便携版
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TRAE SOLO CN", exeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TRAE SOLO CN", exeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TRAE SOLO CN", exeName),
        };
        // 常见安装位置：任意固定盘（C/D/E…）的 Program Files / Program Files (x86)（修复 #25：装到 D:\Program Files 时探测不到）
        try
        {
            foreach (var drv in DriveInfo.GetDrives())
            {
                if (!drv.IsReady || drv.DriveType != DriveType.Fixed) continue;
                candidates.Add(Path.Combine(drv.RootDirectory.FullName, "Program Files", "TRAE SOLO CN", exeName));
                candidates.Add(Path.Combine(drv.RootDirectory.FullName, "Program Files (x86)", "TRAE SOLO CN", exeName));
            }
        }
        catch { /* 枚举失败忽略 */ }
        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return c;
            }
            catch { /* 单个候选判断失败继续下一个 */ }
        }
        return null;
    }

    /// <summary>
    /// 自动检测「随账号切换而变化」的登录态载体（相对 DefaultUserDataDir 的路径），
    /// 用于 settings.json Fingerprint 为空时回填。优先文档化的 globalStorage/storage.json，
    /// 再补充实际的 leveldb 目录；未安装客户端或未检测到任何载体时返回空列表。
    /// </summary>
    public static List<string> DetectFingerprint()
    {
        var found = new List<string>();
        var root = DefaultUserDataDir;
        if (!Directory.Exists(root)) return found;
        try
        {
            var storage = Path.Combine(root, "User", "globalStorage", "storage.json");
            if (File.Exists(storage)) found.Add("User/globalStorage/storage.json");

            // 登录态索引（LevelDB）目录：按相对路径记录，深度受限避免遍历过深/大目录
            found.AddRange(ScanLevelDbDirs(root, depth: 0));
        }
        catch { /* 检测失败返回已找到部分 */ }
        return found.Count > 12 ? found.Take(12).ToList() : found;
    }

    private static List<string> ScanLevelDbDirs(string root, int depth, List<string>? result = null, string current = "")
    {
        result ??= new List<string>();
        if (depth > 5 || result.Count >= 12) return result;
        var dir = current.Length == 0 ? root : Path.Combine(root, current);
        foreach (var child in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(child);
            string rel = current.Length == 0 ? name : current + "/" + name;
            if (name.Equals("leveldb", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(rel);
                if (result.Count >= 12) break;
            }
            else if (!name.StartsWith("Cache", StringComparison.OrdinalIgnoreCase)
                     && !name.Equals("Code Cache", StringComparison.OrdinalIgnoreCase))
            {
                ScanLevelDbDirs(root, depth + 1, result, rel);
            }
        }
        return result;
    }
}