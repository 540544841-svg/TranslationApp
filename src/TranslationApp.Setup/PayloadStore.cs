using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace TranslationApp.Setup;

/// <summary>
/// 安装器随身的载荷（主程序单 EXE）。作为嵌入资源打进安装器，因此安装包永远只有一个文件，
/// 断网也能装——这是「单文件、零外置、无下载」承诺在安装环节的延续。
///
/// 指纹与版本在打包时由 build\make-setup.ps1 算出，写进程序集元数据；
/// 安装时重算一次 SHA-256 与之比对，对不上就拒绝落印（开发构建没有指纹，跳过比对）。
/// </summary>
internal sealed class PayloadStore
{
    private const string ResourceName = "TranslationApp.Setup.Payload.exe";

    private static readonly Lazy<PayloadStore> Lazy = new(() => new PayloadStore());

    private readonly AssemblyMetadataAttribute[] _metadata;

    private PayloadStore()
    {
        _metadata = typeof(PayloadStore).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToArray();
        Present = typeof(PayloadStore).Assembly.GetManifestResourceInfo(ResourceName) is not null;
        if (Present)
        {
            using var stream = Open();
            SizeBytes = stream.Length;
        }
    }

    public static PayloadStore Current => Lazy.Value;

    /// <summary>载荷是否在包里（开发构建未跑发布时为 false）。</summary>
    public bool Present { get; }

    /// <summary>载荷字节数（未压缩的原样大小）。</summary>
    public long SizeBytes { get; }

    /// <summary>打包时登记的 SHA-256（大写十六进制）；开发构建为空串。</summary>
    public string ExpectedSha256 => Metadata("PayloadSha256");

    /// <summary>打包时登记的主程序版本；开发构建为空串。</summary>
    public string Version => Metadata("PayloadVersion") is { Length: > 0 } v ? v : "1.0.0";

    /// <summary>打包时登记的字节数（0 表示未登记）。</summary>
    public long ExpectedBytes =>
        long.TryParse(Metadata("PayloadBytes"), out var n) ? n : 0;

    /// <summary>载荷流。调用方负责释放。</summary>
    public Stream Open() =>
        typeof(PayloadStore).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("安装包里没有主程序载荷。");

    /// <summary>重算载荷 SHA-256（大写十六进制）。</summary>
    public string ComputeSha256()
    {
        using var stream = Open();
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    /// <summary>把载荷原样写到目标文件。</summary>
    public void ExtractTo(string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var source = Open();
        using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }

    private string Metadata(string key) =>
        _metadata.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal))?.Value ?? "";
}
