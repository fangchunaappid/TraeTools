using System.Text.Json;
using TraeTools.Services.Json;

namespace TraeSwitch.Services;

public sealed record VaultMeta(string Account, string CreatedUtc, List<VaultEntry> Entries);
public sealed record VaultEntry(string Rel, long Len, string Sha256Hex);

/// <summary>账号建档展示信息：建档时间（本地）与载体文件数；未建档为 null。</summary>
public sealed record VaultInfo(DateTime? CreatedLocal, int EntryCount);

/// <summary>
/// 按账号把"载体"原样字节备份到 vaultRoot\&lt;Account&gt;\，支持恢复与哈希校验。
/// Fingerprint 条目可为「单文件」或「目录」：目录条目会把整棵子树作为载体处理，
/// 恢复时先删除 live 目标再整树写回，避免 LevelDB 等滚动日志产生孤儿文件。
/// 只做字节拷贝，不解密。
/// </summary>
public sealed class VaultService(string rootDir, string vaultRoot)
{
    /// <summary>客户端单实例锁文件名（残留会让新实例误判"已有实例在运行"，恢复前需删除）。</summary>
    private const string ClientLockName = "code.lock";

    /// <summary>
    /// state.vscdb 的 WAL/SHM 边车（相对数据根）。恢复主库前必须先删目标侧残留——
    /// 客户端被强杀是常态，最新登录写入可能只在 -wal 里，不删就会回放**旧账号**数据（根因 3）。
    /// </summary>
    public static readonly string[] VscdbSidecarRels =
    [
        @"User\globalStorage\state.vscdb-wal",
        @"User\globalStorage\state.vscdb-shm",
    ];

    /// <summary>
    /// "必须回写"集合（P1-3）：storage.json 因每次运行都改写、无账号判别力而被排除出识别评分
    /// （见 <see cref="IsRuntimeStateFile"/>），但它承载账号特异凭据（轮换后的 refresh token），
    /// 切换成功后必须回写 vault 保鲜，否则同一账号第二次切换 401。
    /// </summary>
    public static readonly string[] MustSyncBackRels = [@"User\globalStorage\storage.json"];

    public string AccountDir(string account) => Path.Combine(vaultRoot, Sanitize(account));
    public string MetaPath(string account) => Path.Combine(AccountDir(account), "meta.json");

    /// <summary>展开指纹条目为 root 相对文件路径（目录则递归其中全部文件）。</summary>
    public static IEnumerable<string> ExpandFingerprint(string rootDir, IEnumerable<string> fingerprint)
    {
        foreach (var rel in fingerprint)
        {
            var p = Path.Combine(rootDir, rel);
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    yield return Path.GetRelativePath(rootDir, f);
            }
            else if (File.Exists(p))
            {
                yield return rel;
            }
        }
    }

    /// <summary>
    /// 把 srcRoot\rel 覆盖写到 dstRoot\rel。目录条目先整删 dst 目标再复制整树，
    /// 文件条目直接覆盖。任一侧不存在该条目则跳过。
    /// </summary>
    public static void OverwriteEntry(string srcRoot, string dstRoot, string rel)
    {
        var src = Path.Combine(srcRoot, rel);
        var dst = Path.Combine(dstRoot, rel);
        if (Directory.Exists(src))
        {
            if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dst, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, overwrite: true);
            }
        }
        else if (File.Exists(src))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            // 对称删除：先删目标再写，避免"半新半旧"（SQLite 主库尤其不能留旧文件被追加）
            if (File.Exists(dst)) File.Delete(dst);
            File.Copy(src, dst, overwrite: true);
        }
    }

    public async Task<int> BackupAsync(string account, IEnumerable<string> fingerprint)
    {
        var dir = AccountDir(account);
        Directory.CreateDirectory(dir);
        var entries = new List<VaultEntry>();
        foreach (var rel in fingerprint)
        {
            var src = Path.Combine(rootDir, rel);
            if (Directory.Exists(src))
            {
                foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    var dst = Path.Combine(dir, Path.GetRelativePath(rootDir, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(f, dst, overwrite: true);
                    entries.Add(new VaultEntry(Path.GetRelativePath(rootDir, f), new FileInfo(f).Length, CarrierProfiler.HashFile(f)));
                }
            }
            else if (File.Exists(src))
            {
                var dst = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                entries.Add(new VaultEntry(rel, new FileInfo(src).Length, CarrierProfiler.HashFile(src)));
            }
        }
        var meta = new VaultMeta(account, DateTime.UtcNow.ToString("o"), entries);
        await File.WriteAllTextAsync(MetaPath(account), AppJsonIndented.Serialize(meta));
        return entries.Count;
    }

    /// <summary>读取某账号 meta 的载体条目；无 meta 文件返回 null。</summary>
    public List<VaultEntry>? ReadEntries(string account)
    {
        if (!File.Exists(MetaPath(account))) return null;
        try
        {
            var meta = AppJsonIndented.Deserialize<VaultMeta>(File.ReadAllText(MetaPath(account)));
            return meta?.Entries ?? [];
        }
        catch { return null; }
    }

    /// <summary>某账号建档的展示信息；未建档返回 null。</summary>
    public VaultInfo? GetInfo(string account)
    {
        if (!File.Exists(MetaPath(account))) return null;
        try
        {
            var meta = AppJsonIndented.Deserialize<VaultMeta>(File.ReadAllText(MetaPath(account)));
            if (meta == null) return null;
            DateTime? created = DateTime.TryParse(meta.CreatedUtc, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                ? t.ToLocalTime() : null;
            return new VaultInfo(created, meta.Entries.Count);
        }
        catch { return null; }
    }

    /// <summary>leveldb 内部管理文件：内容与账号无关、随每次运行/compaction 改写，识别时应排除。</summary>
    private static bool IsLeveldbNoise(string rel)
    {
        var name = Path.GetFileName(rel);
        return name == "LOG" || name == "LOG.old" || name == "CURRENT"
            || name == "LOCK" || name.StartsWith("MANIFEST-", StringComparison.Ordinal);
    }

    /// <summary>
    /// 运行痕迹文件：含时间戳/会话状态，每次客户端运行都会改写，与账号身份无关。
    /// 建档时间不同的两个账号这些文件哈希恰好不同，若计入判别集会稀释区分度。
    /// 注意（P1-3）：storage.json 只在"识别归属"时不参与评分；它含账号特异凭据，
    /// 切换成功后必须按 <see cref="MustSyncBackRels"/> 回写 vault 保鲜。
    /// </summary>
    private static bool IsRuntimeStateFile(string rel)
    {
        var norm = rel.Replace('\\', '/').ToLowerInvariant();
        return norm == "local storage/config.db"
            || norm == "network/network persistent state"
            || norm == "user/globalstorage/storage.json";
    }

    /// <summary>识别评分时不应计入的文件（leveldb 管理噪声 + 运行痕迹）。</summary>
    private static bool IsNonDiscriminative(string rel)
        => IsLeveldbNoise(rel) || IsRuntimeStateFile(rel);

    /// <summary>计算 live 与某账号快照的相似度 0..1：meta 中每条"非噪声"载体文件在 live 原路径
    /// 存在且哈希一致即命中；命中数 / 可比数。live 侧多出的文件不判负。
    /// 客户端运行会改写 LOG/滚动日志/运行痕迹文件，故排除它们后评分才稳定。无档返回 null。
    /// </summary>
    public double? MatchScore(string account)
    {
        var entries = ReadEntries(account);
        if (entries == null || entries.Count == 0) return null;
        int comparable = 0, hit = 0;
        foreach (var e in entries)
        {
            if (IsNonDiscriminative(e.Rel)) continue;
            comparable++;
            var live = Path.Combine(rootDir, e.Rel);
            if (!File.Exists(live)) continue;
            try
            {
                if (string.Equals(CarrierProfiler.HashFile(live), e.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                    hit++;
            }
            catch { /* 被占用/读失败，跳过该条 */ }
        }
        return comparable == 0 ? (double?)null : (double)hit / comparable;
    }

    /// <summary>判断"当前 live 登录态是否等于该账号建档快照"（识别归属）：
    /// 相似度达到 <paramref name="minScore"/> 即视为匹配。默认 0.75——因为客户端每次运行
    /// 会改写噪声文件，全等判定无法用于"第二次建档/运行后识别"。
    /// </summary>
    public bool MatchesLive(string account, double minScore = 0.75)
        => MatchScore(account) is double s && s >= minScore;

    /// <summary>
    /// 计算"判别文件"集合：跨账号哈希互不相同、或仅部分账号拥有的非噪声载体文件。
    /// 识别只用判别文件评分——同机客户端共享的文件在账号间相同、无判别力，只会把
    /// 相似度向中间拉、降低区分度。
    /// </summary>
    public HashSet<string> BuildDiscriminantRels(IEnumerable<string> accounts)
    {
        var list = accounts as string[] ?? accounts.ToArray();
        if (list.Length == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // rel -> (出现过的哈希集合, 拥有该文件的账号集合)
        // 注意：元组是值类型，计数必须用引用类型（HashSet）记录，直接 int++ 只改副本、字典里恒为 0。
        var byRel = new Dictionary<string, (HashSet<string> Shas, HashSet<string> Accounts)>(StringComparer.OrdinalIgnoreCase);
        foreach (var acc in list)
        {
            var entries = ReadEntries(acc);
            if (entries == null) continue;
            foreach (var e in entries)
            {
                if (IsNonDiscriminative(e.Rel)) continue;
                if (!byRel.TryGetValue(e.Rel, out var t))
                    byRel[e.Rel] = t = (new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                t.Shas.Add(e.Sha256Hex);
                t.Accounts.Add(acc);
            }
        }
        // 有判别力：所有账号都拥有但内容互不相同（Shas>1），
        // 或仅部分账号拥有（缺失本身即账号差异）。全部账号同内容则无判别力。
        return new HashSet<string>(
            byRel.Where(kv => kv.Value.Shas.Count > 1 || kv.Value.Accounts.Count < list.Length)
                 .Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 仅针对判别文件计算 live 与某账号的匹配度 0..1：该账号 meta 中属于
    /// <paramref name="discriminant"/> 的文件在 live 存在且哈希一致的命中比例。
    /// 判别文件都是"账号特异"文件，同账号命中应接近 1、其他账号应显著更低。
    /// </summary>
    public double? MatchScoreOnRels(string account, ISet<string> discriminant)
    {
        var entries = ReadEntries(account);
        if (entries == null || entries.Count == 0) return null;
        int total = 0, hit = 0;
        foreach (var e in entries)
        {
            if (!discriminant.Contains(e.Rel)) continue;
            total++;
            var live = Path.Combine(rootDir, e.Rel);
            if (!File.Exists(live)) continue;
            try
            {
                if (string.Equals(CarrierProfiler.HashFile(live), e.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                    hit++;
            }
            catch { /* 被占用/读失败，跳过该条 */ }
        }
        return total == 0 ? (double?)null : (double)hit / total;
    }

    public async Task RestoreAsync(string account, IEnumerable<string> fingerprint)
    {
        RestoreEntrySet(AccountDir(account), rootDir, fingerprint);
        await Task.CompletedTask;
    }

    /// <summary>
    /// 把 srcRoot 中的 <paramref name="rels"/> 恢复回 dstRoot（切换与回滚共用），恢复前后处理两件"环境清洁"的事：
    ///  1) <c>code.lock</c>：客户端单实例锁，残留会让新实例误判已有实例在跑；
    ///  2) <c>state.vscdb-wal</c>/<c>-shm</c>：写回主库前先把目标侧边车删掉，否则客户端启动会回放旧账号的
    ///     未 checkpoint 写入 —— "切了等于没切/显示未登录"的直接来源（P0-4）。
    /// 边车的删除与存档里有没有边车无关：存档没有（干净落盘）时更要删掉 live 侧的陈旧边车。
    /// </summary>
    public static void RestoreEntrySet(string srcRoot, string dstRoot, IEnumerable<string> rels)
    {
        var list = rels as string[] ?? rels.ToArray();

        DeleteIfExists(Path.Combine(dstRoot, ClientLockName));

        if (list.Any(IsVscdbFamily))
            foreach (var side in VscdbSidecarRels)
                DeleteIfExists(Path.Combine(dstRoot, side));

        foreach (var rel in list)
            OverwriteEntry(srcRoot, dstRoot, rel);
    }

    /// <summary>state.vscdb 及其全部边车（-wal/-shm/.backup）是否属于该条目。</summary>
    private static bool IsVscdbFamily(string rel)
        => NormalizeRel(rel).StartsWith(@"User\globalStorage\state.vscdb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 删除文件；删除失败**不吞异常**：边车/锁删不掉时继续切换正是"切换无效"的成因，
    /// 必须让上层中止并回滚，而不是静默带病前进。
    /// </summary>
    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>相对路径规范化（统一反斜杠）；兼容历史 meta 里用正斜杠记录的条目。</summary>
    internal static string NormalizeRel(string rel) => rel.Replace('/', '\\');

    /// <summary>
    /// 切换成功后，把 live 的 storage.json（客户端已用可靠的刷新令牌刷新过）回写该账号 vault（P0-5）。
    /// vault 快照若长期不更新，里面的 refresh token 会在每次成功切换后被服务端轮换/作废，
    /// 表现为"同一账号最多只能切换成功一次"（此后 401 refresh token is invalid）。
    /// 只回写 MUST-SYNC 集合：此刻客户端正在运行，SQLite 主库/边车处于打开状态，拷出来必然不自洽，
    /// 不能作为新快照；storage.json 是客户端整体重写的小 JSON，可安全覆盖。
    /// 返回是否有文件被回写；失败（被占用等）静默返回 false，由调用方提示用户重新建档。
    /// </summary>
    public bool SyncBackStorageToVault(string account, IEnumerable<string>? fingerprint = null)
    {
        try
        {
            var allowed = fingerprint == null
                ? null
                : new HashSet<string>(fingerprint.Select(NormalizeRel), StringComparer.OrdinalIgnoreCase);
            bool any = false;
            foreach (var rel in MustSyncBackRels)
            {
                if (allowed is { Count: > 0 } && !allowed.Contains(NormalizeRel(rel))) continue;
                var src = Path.Combine(rootDir, rel);
                if (!File.Exists(src)) continue;
                var dst = Path.Combine(AccountDir(account), rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                UpdateMetaEntry(account, rel, src);
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    /// <summary>刷新 meta.json 中某个载体的长度/哈希（建档时间保持不变，避免"已建档时间"被刷新冲掉）。</summary>
    private void UpdateMetaEntry(string account, string rel, string livePath)
    {
        var entries = ReadEntries(account) ?? [];
        var norm = NormalizeRel(rel);
        var entry = new VaultEntry(norm, new FileInfo(livePath).Length, CarrierProfiler.HashFile(livePath));
        int idx = entries.FindIndex(e => string.Equals(NormalizeRel(e.Rel), norm, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) entries[idx] = entry;
        else entries.Add(entry);

        var created = DateTime.UtcNow.ToString("o");
        try
        {
            if (File.Exists(MetaPath(account)))
            {
                var old = AppJsonIndented.Deserialize<VaultMeta>(File.ReadAllText(MetaPath(account)));
                if (!string.IsNullOrWhiteSpace(old?.CreatedUtc)) created = old!.CreatedUtc;
            }
        }
        catch { /* meta 损坏则用当前时间重建 */ }

        File.WriteAllText(MetaPath(account), AppJsonIndented.Serialize(new VaultMeta(account, created, entries)));
    }

    /// <summary>
    /// 校验 vault 快照是否仍与 live 一致：以 meta 记录的载体为准（= 建档时**实际存在**的文件）逐条比对哈希。
    /// 不用 live 侧展开做基准——白名单是固定 15 项，客户端跑起来后 live 会多出建档时不存在的 -wal/-shm 边车，
    /// 用 live 做基准会把正常状态误判成"校验未通过"。
    /// </summary>
    public async Task<bool> VerifyAsync(string account)
    {
        var entries = ReadEntries(account);
        if (entries == null || entries.Count == 0) return false;
        foreach (var e in entries)
        {
            var vaultFile = Path.Combine(AccountDir(account), e.Rel);
            var liveFile = Path.Combine(rootDir, e.Rel);
            if (!File.Exists(vaultFile) || !File.Exists(liveFile)) return false;
            if (!string.Equals(
                    CarrierProfiler.HashFile(vaultFile), CarrierProfiler.HashFile(liveFile),
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return await Task.FromResult(true);
    }

    /// <summary>
    /// 账号名 → 安全的 vault 目录名：无效文件名字符替换为 _。
    /// . 不在 InvalidFileNameChars 里，必须单独拦截 "." 与 ".."，否则会逃逸出 vaultRoot。
    /// </summary>
    internal static string Sanitize(string account)
    {
        var bad = Path.GetInvalidFileNameChars();
        var name = new string(account.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") name = "untitled";
        return name;
    }

}
