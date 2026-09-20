using System.Text;
using System.Text.Json;

namespace TranslationApp.Core.Anki;

/// <summary>
/// AnkiConnect 客户端（FR-035 / spec §1.1）：向本机桌面版 Anki 的 <c>http://127.0.0.1:29537</c> 发 JSON-RPC。
/// 两条铁律：① 只访问本机——生产 HttpClient 必须 <c>UseProxy=false</c>（系统代理转发 localhost 会假失败，
/// 这也是不复用 HttpClientProvider 的原因：那是引擎代理作用域的客户端）；
/// ② 失败摘要只留异常类型 / HTTP 状态，绝不回显响应正文（Anki 的报错会带字段内容 = 用户文本）。
/// </summary>
public sealed class AnkiConnectClient
{
    /// <summary>AnkiConnect 固定默认端口，仅本机回环地址。</summary>
    public const string BaseUrl = "http://127.0.0.1:29537";

    /// <summary>单请求超时：Anki 卡死/插件版本不符时不能挂住收藏路径。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    private readonly HttpClient _http;

    public AnkiConnectClient(HttpClient http) => _http = http;

    /// <summary>生产用：不经代理的本机客户端。</summary>
    public static HttpClient CreateLocalHostClient() =>
        new(new HttpClientHandler { UseProxy = false }) { Timeout = RequestTimeout };

    /// <summary>连通性探测（version 动作）：设置页「测试连接」与推送前预检共用。</summary>
    public async Task<AnkiProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = await SendAsync(AnkiRequestBuilder.BuildVersionRequest(), cancellationToken);
            if (!TryReadEnvelope(doc.RootElement, out var error, out var result))
            {
                return new AnkiProbeResult(false, null, "AnkiConnect 响应结构异常");
            }

            if (error is not null)
            {
                return new AnkiProbeResult(false, null, "AnkiConnect 返回错误");
            }

            if (result is { ValueKind: JsonValueKind.Number })
            {
                return new AnkiProbeResult(true, result.Value.GetInt32(), null);
            }

            return new AnkiProbeResult(false, null, "AnkiConnect 响应结构异常");
        }
        catch (Exception ex)
        {
            return new AnkiProbeResult(false, null, Describe(ex));
        }
    }

    /// <summary>批量推送（每批 ≤50，spec §1.3）；单批失败不中断其余批次。</summary>
    public async Task<AnkiPushResult> PushNotesAsync(
        IReadOnlyList<AnkiNoteRequest> notes, CancellationToken cancellationToken = default)
    {
        var added = 0;
        var skipped = 0;
        var failed = 0;
        string? reason = null;

        foreach (var batch in AnkiRequestBuilder.Batch(notes))
        {
            try
            {
                using var doc = await SendAsync(AnkiRequestBuilder.BuildAddNotesRequest(batch), cancellationToken);
                if (!TryReadEnvelope(doc.RootElement, out var error, out var result) || error is not null)
                {
                    failed += batch.Count;
                    reason ??= "AnkiConnect 返回错误";
                    continue;
                }

                if (result is not { ValueKind: JsonValueKind.Array })
                {
                    failed += batch.Count;
                    reason ??= "AnkiConnect 响应结构异常";
                    continue;
                }

                foreach (var item in result.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Null)
                    {
                        skipped++; // Anki 判重复/无效：计跳过，不算失败
                    }
                    else
                    {
                        added++;
                    }
                }
            }
            catch (Exception ex)
            {
                failed += batch.Count;
                reason ??= Describe(ex);
            }
        }

        return new AnkiPushResult(added, skipped, failed, reason);
    }

    private async Task<JsonDocument> SendAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(BaseUrl, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AnkiTransportException($"HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>读 AnkiConnect 信封 <c>{"result":…,"error":…}</c>；非对象响应判为结构异常。</summary>
    private static bool TryReadEnvelope(JsonElement root, out string? error, out JsonElement? result)
    {
        error = null;
        result = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
        {
            error = string.IsNullOrEmpty(e.GetString()) ? null : e.GetString();
        }

        if (root.TryGetProperty("result", out var r))
        {
            result = r;
        }

        return true;
    }

    /// <summary>
    /// 异常 → 安全摘要：传输异常只保留 HTTP 状态，其余只保留类型名。
    /// HttpClient 会把处理程序抛出的 HttpRequestException 再包一层（Inner 仍是 HttpRequestException），
    /// 因此取最内层类型名——摘要形态与直连异常保持一致。
    /// </summary>
    private static string Describe(Exception ex)
    {
        if (ex is AnkiTransportException)
        {
            return ex.Message;
        }

        var inner = ex;
        while (inner.InnerException is not null && inner is HttpRequestException)
        {
            inner = inner.InnerException;
        }

        return inner.GetType().Name;
    }

    private sealed class AnkiTransportException(string message) : Exception(message);
}
