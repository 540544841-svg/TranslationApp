using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 共享分块工具测试（13.1.1）。断言语义与原 BingTranslator.SplitIntoChunks 的测试完全一致，
/// 保证提取到 TextChunker 后行为没有走样（旧测试仍留在 BingTranslatorTests 中作为等价性校验）。
/// </summary>
public class TextChunkerTests
{
    [Fact]
    public void Split_ShortText_ReturnsSingleChunk()
    {
        var chunks = TextChunker.Split("hello world", 900).ToArray();

        Assert.Single(chunks);
        Assert.Equal("hello world", chunks[0]);
    }

    [Fact]
    public void Split_ExactlyAtLimit_ReturnsSingleChunk()
    {
        var text = new string('a', 900);

        var chunks = TextChunker.Split(text, 900).ToArray();

        Assert.Single(chunks);
    }

    [Fact]
    public void Split_LongText_SplitsWithinLimitAndPreservesContent()
    {
        var text = string.Concat(Enumerable.Repeat("This is a sentence. ", 200)); // 4000 字符

        var chunks = TextChunker.Split(text, 900).ToArray();

        Assert.True(chunks.Length > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 900, $"分块超长：{c.Length}"));
        Assert.Equal(text, string.Concat(chunks)); // 不丢字符
    }

    [Fact]
    public void Split_LongText_CutsAtSentenceBoundary()
    {
        var text = string.Concat(Enumerable.Repeat("This is a sentence. ", 200));

        var chunks = TextChunker.Split(text, 900).ToArray();

        // 断点落在句末标点上，不会从词中间截断（下一块以句间空白开头，去掉空白仍是完整句子）
        Assert.EndsWith(".", chunks[0]);
        Assert.StartsWith("This is a sentence.", chunks[1].TrimStart());
    }

    [Fact]
    public void Split_BoundaryTooEarly_FallsBackToHardCut()
    {
        // 窗口内唯一的句末标点出现在开头（远小于 maxLength/4），按长度硬切而非切出超小块
        var text = "." + new string('a', 199);

        var chunks = TextChunker.Split(text, 100).ToArray();

        Assert.Equal(2, chunks.Length);
        Assert.Equal(100, chunks[0].Length);
        Assert.Equal(100, chunks[1].Length);
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void Split_EmptyText_ReturnsSingleEmptyChunk()
    {
        var chunks = TextChunker.Split("", 900).ToArray();

        Assert.Single(chunks);
        Assert.Equal("", chunks[0]);
    }

    [Fact]
    public void BingSplitIntoChunks_ForwardsToTextChunker()
    {
        // 转发方法必须与共享工具逐块一致，否则旧调用点会与新引擎走不同分块
        var text = string.Concat(Enumerable.Repeat("Hello world. ", 300));

        var forwarded = BingTranslator.SplitIntoChunks(text, 900).ToArray();
        var shared = TextChunker.Split(text, 900).ToArray();

        Assert.Equal(shared, forwarded);
        Assert.Equal(text, string.Concat(forwarded));
    }
}
