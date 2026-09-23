using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TranslationApp.Core.Updates;

/// <summary>
/// 更新可信根。只信任随程序编译进 EXE 的公钥，绝不从更新服务器读取公钥，
/// 否则攻击者可以同时替换清单与公钥，签名就失去意义。
/// </summary>
public static class UpdateTrust
{
    /// <summary>当前内置发布公钥标识。</summary>
    public const string ReleaseKeyId = "release-2026-01";

    /// <summary>
    /// 发布公钥。私钥不得进入仓库或发布包，只保存在发布机的离线密钥库中。
    /// 更换发布密钥时必须同时提升 <see cref="ReleaseKeyId"/> 并发布一次普通更新，
    /// 让旧版本先把新公钥带过去。
    /// </summary>
    public const string ReleasePublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA6wZb249xEkub6RDynndX
        t8z+xKMMJVrGZ39dQFfFcGXWXE9PBBroDJ6FZ2qIcS2vUfYreoQ1i/fCeujVQnqf
        1Xid7VShjuwEjRa6cqKgtCeSK1R8OZby9sGxj6DaNKFY1Os2P+dm2czerFRl7wEM
        1QOXWbalBSFKNE40F//6OkM6D8DcCdGEvTcKdi1m1TV1kgX91ZVMUkRHyOhct4/b
        oOs/vN86LNz2meCxY95h81BzJhn7ks7YKRtx8lyZiUrim5vFZd7rTox/Bmv1D9lg
        ptFnUZ58NbTRlVvs8C9gQ9Hz9B6htrP58Y4Cnflodjs0KucerCO6F2TKABQlIqNr
        Z6JJquPA37PXLK5TMs2qTJFQlzYFezZlG2VLKE8x2M5tZ9fyrQywb/nEAIoHWnqJ
        bC9n45sZkevQH7tRc6Ys/uIXjoI7WI3L+t83QNbRfMuFqo0pcs/lctlYaF7XPcuo
        EyAXvh9qUD4Y0qlDQkSS4qiY1XXnydcyApM/6lwE+tadAgMBAAE=
        -----END PUBLIC KEY-----
        """;

    private static readonly IReadOnlyDictionary<string, string> Keys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ReleaseKeyId] = ReleasePublicKeyPem,
        };

    public static bool TryGetPublicKey(string keyId, out string publicKeyPem) =>
        Keys.TryGetValue(keyId, out publicKeyPem!);
}

/// <summary>更新清单的签名信封。payload 是 UTF-8 JSON 的 Base64，签名覆盖其解码后的原始字节。</summary>
public sealed record SignedUpdateEnvelope(string KeyId, string Payload, string Signature);

/// <summary>已验签的更新清单。</summary>
public sealed record UpdateManifest(
    int SchemaVersion,
    string Version,
    string DownloadUrl,
    string Sha256,
    long SizeBytes,
    string FileName = "TranslationApp.exe",
    string MinimumVersion = "",
    bool Mandatory = false,
    string Notes = "");

/// <summary>清单验签结果；失败时带稳定的用户可读原因。</summary>
public sealed record UpdateManifestVerification(UpdateManifest? Manifest, string? Error)
{
    public bool IsValid => Manifest is not null;
}

/// <summary>RSA-PSS(SHA-256) 验签与更新清单解析，全部逻辑保持纯函数，便于安全测试。</summary>
public static class UpdateManifestVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static UpdateManifestVerification Verify(string envelopeJson, Func<string, string?> publicKeyResolver)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson))
        {
            return new UpdateManifestVerification(null, "更新清单为空");
        }

        SignedUpdateEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedUpdateEnvelope>(envelopeJson, JsonOptions);
        }
        catch (JsonException)
        {
            return new UpdateManifestVerification(null, "更新清单不是合法 JSON");
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.KeyId))
        {
            return new UpdateManifestVerification(null, "更新清单缺少 keyId");
        }

        var pem = publicKeyResolver(envelope.KeyId);
        if (string.IsNullOrWhiteSpace(pem))
        {
            return new UpdateManifestVerification(null, $"不信任更新签名密钥 {envelope.KeyId}");
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException)
        {
            return new UpdateManifestVerification(null, "更新清单的 payload 或 signature 不是合法 Base64");
        }

        if (!VerifyRsaPss(payload, signature, pem))
        {
            return new UpdateManifestVerification(null, "更新清单签名无效");
        }

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(Encoding.UTF8.GetString(payload), JsonOptions);
        }
        catch (JsonException)
        {
            return new UpdateManifestVerification(null, "已验签的更新清单内容无法解析");
        }

        var error = Validate(manifest);
        return error is null
            ? new UpdateManifestVerification(manifest, null)
            : new UpdateManifestVerification(null, error);
    }

    internal static bool VerifyRsaPss(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature, string publicKeyPem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? Validate(UpdateManifest? manifest)
    {
        if (manifest is null) return "更新清单内容为空";
        if (manifest.SchemaVersion != 1) return $"不支持的更新清单版本：{manifest.SchemaVersion}";
        if (string.IsNullOrWhiteSpace(manifest.Version)) return "更新清单缺少版本号";
        if (!ReleaseVersion.TryParse(manifest.Version, out _)) return "更新版本号格式不正确";
        if (!string.IsNullOrWhiteSpace(manifest.MinimumVersion) &&
            !ReleaseVersion.TryParse(manifest.MinimumVersion, out _))
        {
            return "最低版本号格式不正确";
        }
        if (string.IsNullOrWhiteSpace(manifest.DownloadUrl) ||
            !Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !IsLoopbackHttp(uri)))
        {
            return "更新下载地址必须使用 HTTPS（本机回环测试地址除外）";
        }

        if (manifest.SizeBytes is <= 0 or > UpdateService.MaxDownloadBytes)
        {
            return "更新包大小超出允许范围";
        }

        if (!IsSha256(manifest.Sha256)) return "更新包 SHA-256 格式不正确";
        if (!IsSafeExeName(manifest.FileName)) return "更新包文件名不安全";
        return null;
    }

    private static bool IsLoopbackHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp &&
        (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => Uri.IsHexDigit(c));

    private static bool IsSafeExeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
               !value.Contains("..", StringComparison.Ordinal) &&
               value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}

/// <summary>轻量发布版本号；支持 v1.2.3、1.2.3.4 与 -preview/+build 后缀。</summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, int Revision = 0)
    : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Trim().TrimStart('v', 'V');
        var cut = normalized.IndexOfAny(['-', '+']);
        if (cut >= 0) normalized = normalized[..cut];
        var parts = normalized.Split('.');
        if (parts.Length is < 1 or > 4) return false;

        Span<int> values = stackalloc int[4];
        for (var i = 0; i < 4; i++)
        {
            if (i >= parts.Length)
            {
                values[i] = 0;
                continue;
            }

            if (!int.TryParse(parts[i], out values[i]) || values[i] < 0) return false;
        }

        version = new ReleaseVersion(values[0], values[1], values[2], values[3]);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        return result != 0 ? result : Revision.CompareTo(other.Revision);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
}
