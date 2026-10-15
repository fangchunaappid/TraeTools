using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TraeTools.Services.Json;

namespace TraeTools.Services.Login;

/// <summary>账号备份导出/导入结果。</summary>
public sealed class BackupResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public int Imported { get; init; }
    public int Updated { get; init; }
    public int Skipped { get; init; }
    /// <summary>导出时生成的文本内容（由调用方负责写入用户选择的文件）。</summary>
    public string? Text { get; init; }
}

/// <summary>
/// 账号导入 / 导出（可跨设备迁移）。
///
/// 为什么不复用平台的 DPAPI / Keystore 加密：那两者都是「本机绑定」的——
/// DPAPI 换 Windows 用户或换机器即解不开，Keystore 换机解不开，加密后的备份
/// 恰恰无法完成「换机迁移」这一唯一目的。
/// 因此这里改用**用户口令派生的对称密钥**：PBKDF2-SHA256（20 万次）派生 256 位密钥，
/// AES-256-GCM 认证加密（同时保证机密性与完整性，口令错误或被篡改都会解密失败），
/// 备份文件因此可以安全地走网盘 / 聊天工具传输。
/// </summary>
public static class AccountBackup
{
    private const string Magic = "TraeTools";
    private const string Kind = "account-backup";
    private const int FormatVersion = 1;
    private const int KdfIterations = 200_000;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>
    /// 备份文件容器。
    /// 可见性为 internal（而非 private）是**必须的**：JSON 走 NativeAOT 安全的源生成，
    /// 而源生成上下文集中在 Services/Json/AppJson.cs，那里需要能引用到本类型来登记
    /// [JsonSerializable]（详见该文件注释）。
    /// </summary>
    internal sealed class Envelope
    {
        public string App { get; set; } = Magic;
        public string Type { get; set; } = Kind;
        public int Version { get; set; } = FormatVersion;
        public string CreatedAt { get; set; } = "";
        public string Kdf { get; set; } = "PBKDF2-SHA256";
        public int Iterations { get; set; } = KdfIterations;
        public string Salt { get; set; } = "";
        public string Cipher { get; set; } = "AES-256-GCM";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Payload { get; set; } = "";
    }

    /// <summary>明文负载（可见性说明同 <see cref="Envelope"/>）。</summary>
    internal sealed class Payload
    {
        public string ExportedAt { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public List<TraeCheckin.TraeAccount> Accounts { get; set; } = new();
    }

    /// <summary>
    /// 导出全部账号为加密备份文本。
    /// 口令至少 6 位（过短的口令对这份包含登录凭证的文件而言没有实际保护意义）。
    /// </summary>
    public static BackupResult Export(IEnumerable<TraeCheckin.TraeAccount> accounts, string? passphrase)
    {
        try
        {
            if (string.IsNullOrEmpty(passphrase) || passphrase!.Length < 6)
                return new BackupResult { Ok = false, Message = "口令至少 6 位" };

            var list = accounts.Where(a => !string.IsNullOrEmpty(a.Token) || !string.IsNullOrEmpty(a.Session)).ToList();
            if (list.Count == 0)
                return new BackupResult { Ok = false, Message = "没有可导出的账号（尚无账号已登录）" };

            var payload = new Payload
            {
                ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                AppVersion = TraeTools.Services.AppVersion.Display,
                Accounts = list,
            };
            // 明文负载随后立即被 AES-GCM 加密，因此用紧凑输出即可（源生成，AOT/裁剪安全）
            var plain = Encoding.UTF8.GetBytes(AppJson.Serialize(payload));

            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var key = DeriveKey(passphrase!, salt, KdfIterations);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagBytes];

            using (var aes = new AesGcm(key, TagBytes))
                aes.Encrypt(nonce, plain, cipher, tag);

            var envelope = new Envelope
            {
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Iterations = KdfIterations,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Payload = Convert.ToBase64String(cipher),
            };

            return new BackupResult
            {
                Ok = true,
                Text = AppJsonIndented.Serialize(envelope),
                Imported = list.Count,
                Message = $"已生成备份，共 {list.Count} 个账号",
            };
        }
        catch (Exception ex)
        {
            return new BackupResult { Ok = false, Message = "导出失败：" + ex.Message };
        }
    }

    /// <summary>解密并解析备份文件文本，返回其中的账号列表。不做合并。</summary>
    public static (bool Ok, string Message, List<TraeCheckin.TraeAccount> Accounts) Read(string? text, string? passphrase)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                return (false, "备份内容为空", new List<TraeCheckin.TraeAccount>());
            if (string.IsNullOrEmpty(passphrase))
                return (false, "请输入备份口令", new List<TraeCheckin.TraeAccount>());

            var env = AppJsonIndented.Deserialize<Envelope>(text!);
            if (env == null || env.App != Magic || env.Type != Kind)
                return (false, "这不是 TraeTools 的账号备份文件", new List<TraeCheckin.TraeAccount>());
            if (env.Version > FormatVersion)
                return (false, $"备份文件版本（{env.Version}）高于当前程序支持的版本（{FormatVersion}），请先升级软件", new List<TraeCheckin.TraeAccount>());

            var salt = Convert.FromBase64String(env.Salt);
            var nonce = Convert.FromBase64String(env.Nonce);
            var tag = Convert.FromBase64String(env.Tag);
            var cipher = Convert.FromBase64String(env.Payload);
            var key = DeriveKey(passphrase!, salt, env.Iterations > 0 ? env.Iterations : KdfIterations);

            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, TagBytes))
            {
                try
                {
                    aes.Decrypt(nonce, cipher, tag, plain);
                }
                catch (CryptographicException)
                {
                    // GCM 认证失败：口令错误或文件被篡改，二者无法区分也无法定位，故合并提示
                    return (false, "口令错误，或备份文件已损坏", new List<TraeCheckin.TraeAccount>());
                }
            }

            var payload = AppJson.Deserialize<Payload>(Encoding.UTF8.GetString(plain));
            if (payload?.Accounts == null || payload.Accounts.Count == 0)
                return (false, "备份中没有账号", new List<TraeCheckin.TraeAccount>());

            return (true, $"共 {payload.Accounts.Count} 个账号", payload.Accounts);
        }
        catch (Exception ex)
        {
            return (false, "读取备份失败：" + ex.Message, new List<TraeCheckin.TraeAccount>());
        }
    }

    /// <summary>
    /// 把备份中的账号合并进当前配置：
    ///   · 命中同一账号（AccountUid 相同，或 Id 相同）→ 刷新凭证（Token/Session/DeviceId），保留本地 Id；
    ///   · 否则作为新账号追加（重新分配 Id，避免与本地冲突）。
    /// 调用方负责随后 Save。
    /// </summary>
    public static BackupResult Merge(TraeCheckin.AppConfig cfg, List<TraeCheckin.TraeAccount> incoming)
    {
        try
        {
            int updated = 0, added = 0, skipped = 0;

            foreach (var src in incoming)
            {
                var match = cfg.Accounts.FirstOrDefault(a =>
                    (!string.IsNullOrEmpty(src.AccountUid) && a.AccountUid == src.AccountUid)
                    || a.Id == src.Id);

                if (match != null)
                {
                    match.Token = src.Token ?? match.Token;
                    match.Session = src.Session ?? match.Session;
                    if (!string.IsNullOrWhiteSpace(src.DeviceId)) match.DeviceId = src.DeviceId;
                    match.TokenUpdatedAt = src.TokenUpdatedAt ?? match.TokenUpdatedAt;
                    match.AccountUid ??= src.AccountUid;
                    match.Name ??= src.Name;
                    match.ScreenName ??= src.ScreenName;
                    match.MobileMasked ??= src.MobileMasked;
                    match.AvatarUrl ??= src.AvatarUrl;
                    updated++;
                    continue;
                }

                // 设备号必须在本机唯一，否则多账号共用会触发 9074 风控
                var deviceId = src.DeviceId;
                if (string.IsNullOrWhiteSpace(deviceId) || cfg.Accounts.Any(a => a.DeviceId == deviceId))
                    deviceId = Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString();

                cfg.Accounts.Add(new TraeCheckin.TraeAccount
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = src.Name,
                    Token = src.Token,
                    Session = src.Session,
                    DeviceId = deviceId,
                    AccountUid = src.AccountUid,
                    TokenUpdatedAt = src.TokenUpdatedAt,
                    RemainingCredits = src.RemainingCredits,
                    Enabled = src.Enabled,
                    IsMember = src.IsMember,
                    ScreenName = src.ScreenName,
                    MobileMasked = src.MobileMasked,
                    AvatarUrl = src.AvatarUrl,
                    IsStudent = src.IsStudent,
                });
                added++;
            }

            if (string.IsNullOrEmpty(cfg.ActiveAccountId) && cfg.Accounts.Count > 0)
                cfg.ActiveAccountId = cfg.Accounts[0].Id;

            return new BackupResult
            {
                Ok = added + updated > 0,
                Imported = added,
                Updated = updated,
                Skipped = skipped,
                Message = $"导入完成：新增 {added} 个，更新 {updated} 个",
            };
        }
        catch (Exception ex)
        {
            return new BackupResult { Ok = false, Message = "合并失败：" + ex.Message };
        }
    }

    /// <summary>PBKDF2-SHA256 派生 256 位密钥。</summary>
    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            iterations <= 0 ? KdfIterations : iterations,
            HashAlgorithmName.SHA256,
            32);

    /// <summary>导出文件名建议：TraeTools-账号备份-yyyyMMdd-HHmm.json。</summary>
    public static string SuggestedFileName()
        => $"TraeTools-账号备份-{DateTime.Now:yyyyMMdd-HHmm}.json";
}
