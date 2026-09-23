using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TranslationApp.Core.Updates;
using Xunit;

namespace TranslationApp.Tests;

public sealed class UpdateTrustTests
{
    [Fact]
    public void Verify_AcceptsValidRsaPssSignature()
    {
        using var rsa = RSA.Create(2048);
        var (envelope, publicKey) = Sign(rsa, """
            {"schemaVersion":1,"version":"1.2.3","downloadUrl":"https://example.com/TranslationApp.exe","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","sizeBytes":123,"fileName":"TranslationApp.exe"}
            """);

        var result = UpdateManifestVerifier.Verify(envelope, keyId =>
            keyId == "test-key" ? publicKey : null);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("1.2.3", result.Manifest!.Version);
    }

    [Fact]
    public void Verify_RejectsTamperedSignature()
    {
        using var rsa = RSA.Create(2048);
        var (envelope, publicKey) = Sign(rsa, """
            {"schemaVersion":1,"version":"1.2.3","downloadUrl":"https://example.com/TranslationApp.exe","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","sizeBytes":123,"fileName":"TranslationApp.exe"}
            """);
        var signed = JsonSerializer.Deserialize<SignedUpdateEnvelope>(envelope)!;
        var signature = Convert.FromBase64String(signed.Signature);
        signature[^1] ^= 0x01;
        var tampered = JsonSerializer.Serialize(signed with { Signature = Convert.ToBase64String(signature) });

        var result = UpdateManifestVerifier.Verify(tampered, _ => publicKey);

        Assert.False(result.IsValid);
        Assert.Contains("签名无效", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsUntrustedKey()
    {
        using var rsa = RSA.Create(2048);
        var (envelope, _) = Sign(rsa, ValidPayload());

        var result = UpdateManifestVerifier.Verify(envelope, _ => null);

        Assert.False(result.IsValid);
        Assert.Contains("不信任", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsInsecureDownloadUrl()
    {
        using var rsa = RSA.Create(2048);
        var (envelope, publicKey) = Sign(rsa, """
            {"schemaVersion":1,"version":"1.2.3","downloadUrl":"http://example.com/TranslationApp.exe","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","sizeBytes":123,"fileName":"TranslationApp.exe"}
            """);

        var result = UpdateManifestVerifier.Verify(envelope, _ => publicKey);

        Assert.False(result.IsValid);
        Assert.Contains("HTTPS", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseVersion_ComparesNumericParts()
    {
        Assert.True(ReleaseVersion.TryParse("v1.2.3.4", out var value));
        Assert.Equal(new ReleaseVersion(1, 2, 3, 4), value);
        Assert.True(value > new ReleaseVersion(1, 2, 3, 3));
        Assert.True(new ReleaseVersion(1, 10, 0) > new ReleaseVersion(1, 9, 9));
    }

    private static string ValidPayload() => """
        {"schemaVersion":1,"version":"1.2.3","downloadUrl":"https://example.com/TranslationApp.exe","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","sizeBytes":123,"fileName":"TranslationApp.exe"}
        """;

    private static (string Envelope, string PublicKey) Sign(RSA rsa, string payloadJson)
    {
        var payload = Encoding.UTF8.GetBytes(payloadJson);
        var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var envelope = JsonSerializer.Serialize(new SignedUpdateEnvelope(
            "test-key",
            Convert.ToBase64String(payload),
            Convert.ToBase64String(signature)));
        return (envelope, rsa.ExportSubjectPublicKeyInfoPem());
    }
}

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ta-update-{Guid.NewGuid():N}");

    public UpdateInstallerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Prepare_CopiesCandidateBesideTargetExecutable()
    {
        var source = Path.Combine(_root, "download", "TranslationApp.exe");
        var target = Path.Combine(_root, "install", "TranslationApp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(source, "candidate");
        File.WriteAllText(target, "current");

        var plan = UpdateInstaller.Prepare(source, target);

        Assert.Equal(Path.GetDirectoryName(target), Path.GetDirectoryName(plan.CandidatePath));
        Assert.True(File.Exists(plan.CandidatePath));
        Assert.True(File.Exists(plan.ScriptPath));

        try
        {
            Assert.True(File.Exists(plan.ScriptPath));
        }
        finally
        {
            File.Delete(plan.ScriptPath);
        }
    }

    [Fact]
    public void BuildScript_CleansCandidateAndScriptInFinally()
    {
        var script = UpdateInstaller.BuildScript(
            Path.Combine(_root, "candidate.exe"),
            Path.Combine(_root, "target.exe"),
            Path.Combine(_root, "target.exe.rollback"));

        Assert.Contains("$PSCommandPath", script, StringComparison.Ordinal);
        Assert.Contains("$candidate", script, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $target", script, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}
