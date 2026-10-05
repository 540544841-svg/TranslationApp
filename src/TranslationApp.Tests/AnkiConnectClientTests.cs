using System.Net;
using System.Text;
using TranslationApp.Core.Anki;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// FR-035：AnkiConnectClient 行为（桩 handler，零网络）。spec §1.1/§5——
/// 重点锁两条红线：只访问 127.0.0.1；失败摘要绝不回显响应正文（Anki 报错会带字段内容 = 用户文本）。
/// </summary>
public class AnkiConnectClientTests
{
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<Uri> Uris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Uris.Add(request.RequestUri!);
            return respond(Bodies.Count - 1);
        }
    }


    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (AnkiConnectClient Client, StubHandler Handler) ClientResponding(Func<int, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new AnkiConnectClient(new HttpClient(handler)), handler);
    }

    private static AnkiNoteRequest Note(string front = "apple") => new(
        "生词本", "基本", "正面", front, "背面", "苹果", ["译印"]);

    [Fact]
    public async Task Probe_Ok_ReturnsVersion()
    {
        var (client, handler) = ClientResponding(_ => Json("""{"result":6,"error":null}"""));
        var probe = await client.ProbeAsync();
        Assert.True(probe.Ok);
        Assert.Equal(6, probe.Version);
        Assert.Null(probe.Reason);
        Assert.Contains("\"version\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task Probe_OnlyEverTargetsLocalhost()
    {
        var (client, handler) = ClientResponding(_ => Json("""{"result":6,"error":null}"""));
        await client.ProbeAsync();
        await client.PushNotesAsync([Note()]);
        Assert.All(handler.Uris, uri =>
        {
            Assert.Equal("http", uri.Scheme);
            Assert.Equal("127.0.0.1", uri.Host);
            Assert.Equal(29537, uri.Port);
        });
    }

    [Fact]
    public async Task Probe_ErrorField_Fails()
    {
        var (client, _) = ClientResponding(_ => Json("""{"result":null,"error":"AnkiConnect is busy"}"""));
        var probe = await client.ProbeAsync();
        Assert.False(probe.Ok);
        Assert.Null(probe.Version);
    }

    [Fact]
    public async Task Probe_TransportFailure_ReasonIsExceptionTypeNameOnly()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("用户文本泄漏点 apple"));
        var client = new AnkiConnectClient(new HttpClient(handler));
        var probe = await client.ProbeAsync();
        Assert.False(probe.Ok);
        Assert.Equal(nameof(HttpRequestException), probe.Reason);
    }

    [Fact]
    public async Task Push_CountsAddedAndSkippedPerItem()
    {
        var (client, _) = ClientResponding(_ => Json("""{"result":[111,null,null],"error":null}"""));
        var res = await client.PushNotesAsync([Note("a"), Note("b"), Note("c")]);
        Assert.Equal(1, res.Added);
        Assert.Equal(2, res.Skipped);
        Assert.Equal(0, res.Failed);
    }

    [Fact]
    public async Task Push_ServerError_CountsWholeBatchFailed_AndSanitizesReason()
    {
        // error 正文含字段回显（用户文本），失败摘要只给固定短语
        var (client, _) = ClientResponding(_ => Json(
            """{"result":null,"error":"note is duplicate of apple 苹果"}"""));
        var res = await client.PushNotesAsync([Note(), Note("b")]);
        Assert.Equal(0, res.Added);
        Assert.Equal(2, res.Failed);
        Assert.NotNull(res.Reason);
        Assert.DoesNotContain("apple", res.Reason);
        Assert.DoesNotContain("苹果", res.Reason);
    }

    [Fact]
    public async Task Push_Http500_FailedWithHttpStatus()
    {
        var (client, _) = ClientResponding(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var res = await client.PushNotesAsync([Note()]);
        Assert.Equal(1, res.Failed);
        Assert.Contains("500", res.Reason);
    }

    [Fact]
    public async Task Push_BatchesOneRequestPerFifty()
    {
        var (client, handler) = ClientResponding(_ => Json(
            """{"result":[1,1,1,1,1,1,1,1,1,1],"error":null}"""));
        var notes = Enumerable.Range(0, 120).Select(i => Note(i.ToString())).ToArray();
        await client.PushNotesAsync(notes);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains("\"addNotes\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task Push_TransportFailure_KeepsGoingAndCountsPerBatch()
    {
        // 第一批网络失败、第二批成功：不因单批失败中断其余批次
        var handler = new StubHandler(index =>
            index == 0
                ? throw new HttpRequestException("refused")
                : Json("""{"result":[1,1],"error":null}"""));
        var client = new AnkiConnectClient(new HttpClient(handler));
        var notes = Enumerable.Range(0, 60).Select(i => Note(i.ToString())).ToArray();
        var res = await client.PushNotesAsync(notes);
        Assert.Equal(2, res.Added); // 桩返回逐项结果数组：计数以数组为准
        Assert.Equal(50, res.Failed);
        Assert.Equal(nameof(HttpRequestException), res.Reason);
    }
}
