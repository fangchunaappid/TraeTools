using System.Security.Cryptography;

namespace TraeSwitch.Services;

/// <summary>
/// 目录树指纹：对指定根目录（排除噪声目录）逐文件算 SHA-256，
/// 通过两次快照 diff 找出"随账号切换而变化"的载体文件集合。
/// </summary>
public static class CarrierProfiler
{
    /// <summary>默认跳过的大体积/无关目录（相对名匹配任意层级）。</summary>
    public static readonly string[] DefaultExcludeDirs =
    [
        "Cache", "CachedData", "CachedConfigurations", "CachedProfilesData",
        "Code Cache", "GPUCache", "Crashpad", "DawnGraphiteCache", "DawnWebGPUCache",
        "ShaderCache", "Dictionaries", "blob_storage", "Service Worker",
        "Shared Dictionary", "logs", "Backups"
    ];

    private static readonly HashSet<string> DefaultExcludes =
        new(DefaultExcludeDirs, StringComparer.OrdinalIgnoreCase);

    /// <summary>目录名是否属于默认跳过集合（缓存、日志等，与账号登录态无关）。</summary>
    public static bool IsExcludedDir(string name) => DefaultExcludes.Contains(name);

    /// <summary>
    /// 递归枚举 root 下全部文件（只返回文件，不返回目录），并跳过 <see cref="DefaultExcludeDirs"/> 中的目录。
    /// 载体快照与目录扫描共用这一个枚举器：Chromium 的 Cache/GPUCache 等子树动辄上千个文件、
    /// 数十 MB，却与登录态完全无关，一旦被快照收录，建档会长时间冻界面、切换还要把它们原样写回去。
    /// </summary>
    public static IEnumerable<string> EnumerateTree(string root, IEnumerable<string>? excludeDirs = null)
    {
        var ex = excludeDirs == null
            ? DefaultExcludes
            : new HashSet<string>(excludeDirs, StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Directory.Exists(entry))
                {
                    if (!ex.Contains(Path.GetFileName(entry))) stack.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>扫描 root，返回 相对路径(反斜杠→正斜杠)→小写 SHA-256（同样跳过 <paramref name="excludeDirs"/>）。</summary>
    public static Dictionary<string, string> HashTree(string root, IEnumerable<string> excludeDirs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in EnumerateTree(root, excludeDirs))
            result[Path.GetRelativePath(root, f).Replace('\\', '/')] = HashFile(f);
        return result;
    }

    public static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(sha.ComputeHash(fs));
    }

    /// <summary>返回两个快照间出现差异的相对路径（新增/修改/删除）。</summary>
    public static HashSet<string> DiffChangedPaths(
        IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, hash) in after)
        {
            if (!before.TryGetValue(rel, out var old) || !string.Equals(old, hash, StringComparison.OrdinalIgnoreCase))
                changed.Add(rel);
        }
        foreach (var rel in before.Keys)
        {
            if (!after.ContainsKey(rel)) changed.Add(rel);
        }
        return changed;
    }
}
