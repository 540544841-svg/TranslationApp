using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TranslationApp.Core.Api;

/// <summary>一次 API 翻译的结果（引擎 Id 与术语命中数随译文返回）。</summary>
public sealed record LocalApiTranslation(string Translated, string Engine, int GlossaryHits);

/// <summary>
/// 本地 HTTP API（FR-046 / spec §4）：只绑 <c>http://127.0.0.1:{port}/</c>，供脚本 / 浏览器扩展 / AHK 调用。
/// 安全边界：① 仅回环地址（构造即固定，不给非本机前缀的机会）；② 每个数据请求都要 <c>X-Auth</c> 头等于 token；
/// ③ 并发闸 <see cref="MaxConcurrency"/>；④ 单请求翻译超时 <see cref="RequestTimeoutMs"/>ms。
/// CORS 放开是给本机扩展用的——token 才是防线，文档与设置页都如实写明。
/// </summary>
public sealed class LocalApiServer : IDisposable
{
    /// <summary>默认端口（可配 1024~65535）。</summary>
    public const int DefaultPort = 46610;

    /// <summary>同时在途的翻译请求上限（超出排队，防止脚本风暴打满引擎配额）。</summary>
    public const int MaxConcurrency = 2;

    /// <summary>单次翻译超时。</summary>
    public const int RequestTimeoutMs = 15_000;

    /// <summary>请求体上限；本地 API 的单次文本远小于此，仅用于阻止任意大 body 占用内存。</summary>
    public const int MaxRequestBodyBytes = 64 * 1024;

    private readonly HttpListener _listener = new();
    private readonly string _token;
    private readonly Func<string, string, string, CancellationToken, Task<LocalApiTranslation>> _translate;
    private readonly Func<IReadOnlyList<string>> _configuredEngineIds;
    private readonly SemaphoreSlim _gate = new(1, MaxConcurrency);
    private readonly object _requestStateGate = new();
    private readonly ManualResetEventSlim _requestsDrained = new(initialState: true);
    private int _activeRequests;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public LocalApiServer(
        int port,
        string token,
        Func<string, string, string, CancellationToken, Task<LocalApiTranslation>> translate,
        Func<IReadOnlyList<string>> configuredEngineIds)
    {
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口需在 1024~65535");
        }

        _token = token;
        _translate = translate;
        _configuredEngineIds = configuredEngineIds;
        // 前缀写死回环地址：本类不存在监听非 127.0.0.1 的构造路径
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public bool IsRunning => _listener.IsListening;

    /// <summary>32 位小写 hex 随机 token（RandomNumberGenerator，非 Guid）。</summary>
    public static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>启动监听；端口被占等失败返回 false（不抛，调用方提示用户改端口）。</summary>
    public bool TryStart()
    {
        if (_disposed || IsRunning)
        {
            return IsRunning;
        }

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            return false;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
        return true;
    }

    /// <summary>停止监听（幂等；在途请求由超时兜底收尾）。</summary>
    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch (HttpListenerException)
        {
            // 已在关闭中
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // Stop()/Dispose() 打断等待属正常收尾
            }

            QueueRequest(context, ct);
        }
    }

    /// <summary>先登记再调度，确保 Dispose 不会在任务启动窗口内误判为已排空。</summary>
    private void QueueRequest(HttpListenerContext context, CancellationToken cancellationToken)
    {
        lock (_requestStateGate)
        {
            if (_disposed)
            {
                try
                {
                    context.Response.Abort();
                }
                catch
                {
                    // 客户端可能已断开。
                }

                return;
            }

            _activeRequests++;
            _requestsDrained.Reset();
        }

        _ = Task.Run(() => HandleAsync(context, cancellationToken), CancellationToken.None);
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var response = context.Response;
        try
        {
            response.AddHeader("Access-Control-Allow-Origin", "*");
            response.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Auth");
            response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");

            var request = context.Request;
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                return;
            }

            var path = request.Url?.AbsolutePath ?? "";
            var isDataEndpoint = path is "/api/translate" or "/api/status";
            if (isDataEndpoint && request.Headers["X-Auth"] != _token)
            {
                response.StatusCode = 401;
                return;
            }

            switch ((request.HttpMethod, path))
            {
                case ("GET", "/api/status"):
                    await WriteJsonAsync(response, 200, new
                    {
                        app = "速译",
                        api = 1,
                        engines = _configuredEngineIds(),
                    });
                    return;

                case ("POST", "/api/translate"):
                    await HandleTranslateAsync(context, cancellationToken);
                    return;

                default:
                    response.StatusCode = 404;
                    return;
            }
        }
        catch
        {
            // 单请求异常不外溢：监听循环必须活着
            try
            {
                response.StatusCode = 500;
            }
            catch
            {
                // 客户端已断开
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch
            {
                // 忽略
            }

            CompleteRequest();
        }
    }

    private void CompleteRequest()
    {
        lock (_requestStateGate)
        {
            _activeRequests--;
            if (_activeRequests == 0)
            {
                _requestsDrained.Set();
            }
        }
    }

    private async Task HandleTranslateAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var response = context.Response;
        var request = context.Request;
        if (request.ContentLength64 > MaxRequestBodyBytes)
        {
            await WriteJsonAsync(response, 413, new { error = "请求体过大" });
            return;
        }

        string text;
        string source;
        string target;
        try
        {
            var body = await ReadBodyAsync(request.InputStream, cancellationToken);
            if (body is null)
            {
                await WriteJsonAsync(response, 413, new { error = "请求体过大" });
                return;
            }

            using var doc = JsonDocument.Parse(body);
            text = GetString(doc.RootElement, "text");
            source = GetString(doc.RootElement, "source", "auto");
            target = GetString(doc.RootElement, "target");
        }
        catch (JsonException)
        {
            await WriteJsonAsync(response, 400, new { error = "body 需为合法 JSON" });
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (text.Trim().Length == 0 || target.Length == 0)
        {
            await WriteJsonAsync(response, 400, new { error = "text 与 target 必填" });
            return;
        }

        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken);
            entered = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeoutMs);
            var result = await _translate(text.Trim(), source, target, timeout.Token);
            await WriteJsonAsync(response, 200, new
            {
                translated = result.Translated,
                engine = result.Engine,
                glossaryHits = result.GlossaryHits,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服务正在停止：不抢写响应，连接由外层关闭。
        }
        catch (OperationCanceledException)
        {
            await WriteJsonAsync(response, 504, new { error = "翻译超时" });
        }
        catch
        {
            // 引擎错误只回类型级信息，不外泄内部异常文本
            await WriteJsonAsync(response, 502, new { error = "translate_failed" });
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    private static async Task<string?> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total > MaxRequestBodyBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string GetString(JsonElement root, string name, string fallback = "") =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        lock (_requestStateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        _listener.Close();
        // Stop 已取消在途请求；只有确认排空后才释放同步原语，避免在仍被使用时 Dispose。
        if (!_requestsDrained.Wait(TimeSpan.FromSeconds(2)))
        {
            return;
        }

        _gate.Dispose();
        _requestsDrained.Dispose();
        _cts?.Dispose();
    }
}
