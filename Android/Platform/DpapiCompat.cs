using System.Text;

// ⚠️ 在 System.Security.Cryptography 命名空间下定义 BCL 同名类型是刻意的，理由见下方注释。
namespace System.Security.Cryptography;

/// <summary>与 DPAPI 同名的作用域枚举（Android 上只有应用本地密钥库，语义上等同 CurrentUser）。</summary>
public enum DataProtectionScope
{
    CurrentUser,
    LocalMachine,
}

/// <summary>
/// <b>DPAPI 兼容层（仅 Android 编译）。</b>
///
/// <para>背景：共享的 <c>Services/Checkin/AppConfig.cs</c> 用
/// <c>ProtectedData.Protect/Unprotect</c> 加密 Token / Session / DeviceId 等敏感字段；
/// 而 <c>System.Security.Cryptography.ProtectedData</c> 是 Windows 专属包，
/// net10.0-android 上不存在该类型，直接编译会失败。</para>
///
/// <para>本工程的前提是<b>不改动仓库根目录下的任何文件</b>，因此这里提供一份
/// 同名同签名的兼容层，把 DPAPI 的语义映射到
/// <see cref="TraeTools.Platform.AndroidVault"/>（Keystore + AES-256-GCM），
/// 于是上游的业务代码一行不改即可在手机上正常工作。</para>
///
/// <para>两处语义差异，均已被上游代码的用法包容：
/// <list type="number">
/// <item>密文格式不同 —— AppConfig 只把结果当不透明字符串存取，跨平台拷贝时解密失败会
///       走它自己的兜底（字段置 null → 提示重新登录），不会把密文当明文使用；</item>
/// <item>失败时抛 <see cref="CryptographicException"/> —— 与 DPAPI 行为一致，
///       AppConfig 的 <c>DecryptSecret</c> 本来就 catch 全部异常。</item>
/// </list></para>
///
/// <para>注：若某个 .NET Android 版本将来自带了同名类型，编译器会以本文件为准并给出
/// CS0436 警告（构建时已抑制），行为仍然是我们的 Keystore 实现 —— 正是期望的结果。</para>
/// </summary>
public static class ProtectedData
{
    public static byte[] Protect(byte[] userData, byte[]? optionalEntropy, DataProtectionScope scope)
    {
        ArgumentNullException.ThrowIfNull(userData);
        var encrypted = TraeTools.Platform.AndroidVault.Protect(Encoding.UTF8.GetString(userData))
                        ?? throw new CryptographicException("Android Keystore 加密失败");
        return Encoding.UTF8.GetBytes(encrypted);
    }

    public static byte[] Unprotect(byte[] encryptedData, byte[]? optionalEntropy, DataProtectionScope scope)
    {
        ArgumentNullException.ThrowIfNull(encryptedData);
        var plain = TraeTools.Platform.AndroidVault.Unprotect(Encoding.UTF8.GetString(encryptedData))
                    ?? throw new CryptographicException("Android Keystore 解密失败（换机 / 清除数据 / 跨平台拷贝）");
        return Encoding.UTF8.GetBytes(plain);
    }
}
