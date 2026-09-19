using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace TranslationApp.Core.Settings;

/// <summary>
/// 敏感信息加密存储（FR-010 / FR-018）：使用 Windows DPAPI，作用域为当前用户，
/// 密钥由系统托管，密文换机器/换用户后无法解密（符合预期）。
/// 密文以 "enc:" 前缀标识，便于识别历史遗留的明文值。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretStore
{
    private const string Prefix = "enc:";

    /// <summary>附加熵：与产品标识绑定，避免同机其他程序直接解开本程序的密文。</summary>
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("TranslationApp.SecretStore.v1");

    /// <summary>加密明文；空值原样返回空串。</summary>
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return "";
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            // 加密不可用（极端环境）：宁可不保存，也不明文落盘
            return "";
        }
    }

    /// <summary>解密；非密文（无前缀）按明文返回 null 表示不可用。</summary>
    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return "";
        }

        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null; // 不是本程序写入的密文
        }

        try
        {
            var raw = Convert.FromBase64String(protectedText[Prefix.Length..]);
            var bytes = ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null; // 解密失败（换机器/换用户/数据损坏）
        }
    }

    /// <summary>是否为密文格式。</summary>
    public static bool IsEncrypted(string? value) =>
        value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}
