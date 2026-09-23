using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TranslationApp.Core.Translation;

namespace TranslationApp.Core.Updates;

public enum UpdateCheckStatus
{
    UpToDate,
    Available,
    NotConfigured,
    InvalidManifest,
    NetworkError,
    MinimumVersionRequired,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string Message,
    UpdateManifest? Manifest = null,
    ReleaseVersion CurrentVersion = default,
    ReleaseVersion LatestVersion = default)
{
    public bool HasUpdate => Status == UpdateCheckStatus.Available && Manifest is not null;
}

public sealed record UpdateDownloadResult(
    bool Success,
    string? FilePath,
    string Message,
    long BytesWritten = 0);

/// <summary>
/// 更新检查与下载。只接受已经过内置公钥验签的清单；
/// 下载内容流式写入临时文件并同步计算 SHA-256，哈希不符立即删除，不把未验证文件交给安装器。
/// </summary>
public sealed class UpdateService
{
    public const long MaxManifestBytes = 1024 * 1024;
    public const long MaxDownloadBytes = 250L * 1024 * 1024;

    private readonly Func<HttpClient> _httpClient;
    private readonly Func<string, string?> _publicKeyResolver;

    public UpdateService(Func<HttpClient> httpClient, Func<string, string?>? publicKeyResolver = null)
    {
        _httpClient = httpClient;
        _publicKeyResolver = publicKeyResolver ?? (keyId =>
            UpdateTrust.TryGetPublicKey(keyId, out var pem) ? pem : null);
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string? manifestUrl,
        ReleaseVersion currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured, "尚未配置更新清单地址", CurrentVersion: currentVersion);
        }

        if (!Uri.TryCreate(manifestUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))))
        {
            return new UpdateCheckResult(UpdateCheckStatus.InvalidManifest, "更新地址必须使用 HTTPS（本机回环地址除外）", CurrentVersion: currentVersion);
        }

        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
            };
            using var response = await _httpClient().SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxManifestBytes)
            {
                return new UpdateCheckResult(UpdateCheckStatus.InvalidManifest, "更新清单超过 1MB 上限", CurrentVersion: currentVersion);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            json = await ReadLimitedTextAsync(stream, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.NetworkError, $"检查更新失败：{ex.Message}", CurrentVersion: currentVersion);
        }

        var verified = UpdateManifestVerifier.Verify(json, _publicKeyResolver);
        if (!verified.IsValid)
        {
            return new UpdateCheckResult(UpdateCheckStatus.InvalidManifest, verified.Error ?? "更新清单验签失败", CurrentVersion: currentVersion);
        }

        var manifest = verified.Manifest!;
        ReleaseVersion.TryParse(manifest.Version, out var latest);
        if (!string.IsNullOrWhiteSpace(manifest.MinimumVersion) &&
            ReleaseVersion.TryParse(manifest.MinimumVersion, out var minimum) && currentVersion < minimum)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.MinimumVersionRequired,
                $"新版要求至少 {manifest.MinimumVersion}，当前 {currentVersion} 过旧，请下载完整安装包",
                manifest, currentVersion, latest);
        }

        if (latest <= currentVersion)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, $"当前已是最新版本 {currentVersion}", manifest, currentVersion, latest);
        }

        return new UpdateCheckResult(
            UpdateCheckStatus.Available,
            $"发现新版本 {manifest.Version}（当前 {currentVersion}）",
            manifest, currentVersion, latest);
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateManifest manifest,
        string stagingDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri))
        {
            return new UpdateDownloadResult(false, null, "更新下载地址无效");
        }

        Directory.CreateDirectory(stagingDirectory);
        var finalPath = Path.Combine(stagingDirectory, manifest.FileName);
        var partialPath = finalPath + ".partial";

        try
        {
            using var response = await _httpClient().GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
            {
                return new UpdateDownloadResult(false, null, "更新包超过 250MB 上限");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var destination = new FileStream(
                partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes)
                    {
                        return new UpdateDownloadResult(false, null, "更新包超过 250MB 上限");
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (manifest.SizeBytes > 0)
                    {
                        progress?.Report(Math.Clamp((double)total / manifest.SizeBytes, 0, 1));
                    }
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (total != manifest.SizeBytes)
                {
                    return new UpdateDownloadResult(false, null, $"更新包大小不符：期望 {manifest.SizeBytes} 字节，实际 {total} 字节");
                }

                var actualHash = Convert.ToHexString(hash.GetHashAndReset());
                if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new UpdateDownloadResult(false, null, "更新包 SHA-256 校验失败，已拒绝安装");
                }
            }

            File.Move(partialPath, finalPath, overwrite: true);
            progress?.Report(1);
            return new UpdateDownloadResult(true, finalPath, "更新包已下载并通过签名与哈希校验", manifest.SizeBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateDownloadResult(false, null, $"下载更新失败：{ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(partialPath)) File.Delete(partialPath);
            }
            catch
            {
                // 临时文件清理失败不覆盖真实错误。
            }
        }
    }

    private static async Task<string> ReadLimitedTextAsync(
        Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new InvalidDataException("更新清单超过大小限制");
            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
    }
}
