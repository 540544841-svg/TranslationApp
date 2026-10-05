using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TranslationApp.Core.Api;
using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>FR-046：本地 HTTP API（真实 HttpListener 绑 127.0.0.1 随机端口，零外网）。</summary>
public sealed class LocalApiServerTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly LocalApiServer _server;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _base;

    public LocalApiServerTests()
    {
        // 高位随机端口避免与真实实例撞端口；绑定失败由 TryStart 断言兜底
        var port = 47000 + Random.Shared.Next(0, 2000);
        _server = new LocalApiServer(
            port, Token,
            translate: (text, source, target, _) =>
                Task.FromResult(new LocalApiTranslation($"[{source}->{target}]{text}", "bing", 1)),
            configuredEngineIds: () => ["bing", "google"]);
        Assert.True(_server.TryStart(), "本地监听启动失败（端口被占？）");
        _base = $"http://127.0.0.1:{port}";
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
    }

    [Fact]
    public void GenerateToken_Is32LowerHex_AndUnique()
    {
        var a = LocalApiServer.GenerateToken();
        var b = LocalApiServer.GenerateToken();
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Translate_WithValidToken_ReturnsJson()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/translate")
        {
            Content = new StringContent("""{"text":"hello","target":"zh-CN"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Auth", Token);
        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("[auto->zh-CN]hello", doc.RootElement.GetProperty("translated").GetString());
        Assert.Equal("bing", doc.RootElement.GetProperty("engine").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("glossaryHits").GetInt32());
    }

    [Fact]
    public async Task Translate_WithoutToken_401()
    {
        var response = await _http.PostAsync($"{_base}/api/translate",
            new StringContent("""{"text":"x"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Translate_EmptyText_400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/translate")
        {
            Content = new StringContent("""{"text":"  "}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Auth", Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Translate_MalformedJson_400()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/translate")
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Auth", Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Translate_RequestBodyOverLimit_413()
    {
        var oversized = new string('x', LocalApiServer.MaxRequestBodyBytes + 1);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/translate")
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Auth", Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Status_WithToken_ReportsConfiguredEngines()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/status");
        request.Headers.Add("X-Auth", Token);
        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("译印", doc.RootElement.GetProperty("app").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("engines").GetArrayLength());
    }

    [Fact]
    public async Task UnknownPath_404()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_base}/nope");
        request.Headers.Add("X-Auth", Token);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task OptionsPreflight_OkWithoutToken_AndCarriesCorsHeaders()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, $"{_base}/api/translate");
        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").First());
    }
}

/// <summary>FR-047：批量 OCR 编排（桩识别器，零文件系统依赖——只测编排与导出）。</summary>
public sealed class BatchOcrRunnerTests
{
    [Theory]
    [InlineData("a.png", true)]
    [InlineData("b.jpeg", true)]
    [InlineData("c.TIF", true)]
    [InlineData("d.pdf", false)]
    [InlineData("e.txt", false)]
    public void SupportedExtensions(string path, bool expected) =>
        Assert.Equal(expected, BatchOcrRunner.IsSupportedImage(path));

    [Fact]
    public async Task Run_MixedResults_KeepsGoingAndReportsProgress()
    {
        var done = new List<int>();
        var items = await BatchOcrRunner.RunAsync(
            ["a.png", "b.png", "c.png"],
            async path =>
            {
                await Task.Yield();
                return path.Contains('b') ? throw new InvalidOperationException("内部信息") : $"文本-{path}";
            },
            new Progress<(int Done, int Total)>(p => done.Add(p.Done)));

        Assert.Equal(3, items.Count);
        Assert.True(items[0].Ok);
        Assert.False(items[1].Ok);
        Assert.Equal(nameof(InvalidOperationException), items[1].Error); // 不外泄异常消息
        Assert.Equal("文本-c.png", items[2].Text);
        await Task.Delay(100); // Progress 异步投递
        Assert.Contains(3, done);
    }

    [Fact]
    public async Task Run_EmptyTextFromRecognizer_CountsAsFailure()
    {
        var items = await BatchOcrRunner.RunAsync(["a.png"], _ => Task.FromResult<string?>(null));
        Assert.False(Assert.Single(items).Ok);
    }

    [Fact]
    public void Markdown_UsesFileNameHeading()
    {
        var md = BatchOcrRunner.BuildMarkdown(
        [
            new BatchOcrItem(@"C:\x\第一张.png", true, "hello\nworld", null),
            new BatchOcrItem(@"C:\x\坏图.png", false, null, "Http 404"),
        ]);
        Assert.Contains("## 第一张.png", md);
        Assert.Contains("hello\nworld", md);
        Assert.Contains("## 坏图.png", md);
        Assert.Contains("> 识别失败：Http 404", md);
    }
}
