using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>术语表后置替换测试（P0 批 1 / spec §1.2）。</summary>
public class GlossaryReplacerTests
{
    private static GlossaryItem Item(string src, string dst, bool enabled = true) => new(src, dst, enabled);

    [Fact]
    public void Apply_BasicReplacement()
    {
        var r = GlossaryReplacer.Apply("这是一段 memory 文本", new[] { Item("memory", "内存") });

        Assert.Equal("这是一段 内存 文本", r.Text);
        Assert.Equal(1, r.Hits);
    }

    [Fact]
    public void Apply_AsciiWordBoundary_DoesNotMatchInsideWord()
    {
        var items = new[] { Item("AI", "人工智能") };

        Assert.Equal("OpenAI 与 Open AI", GlossaryReplacer.Apply("OpenAI 与 Open AI", items).Text.Replace("人工智能", "AI"));
        // 上一行仅验证词界：OpenAI 不被拆开（替换后还原比较），下面做直接断言
        var r = GlossaryReplacer.Apply("OpenAI rocks", items);
        Assert.Equal("OpenAI rocks", r.Text);
        Assert.Equal(0, r.Hits);

        var r2 = GlossaryReplacer.Apply("the AI works", items);
        Assert.Equal("the 人工智能 works", r2.Text);
    }

    [Fact]
    public void Apply_AsciiIsCaseInsensitive()
    {
        var r = GlossaryReplacer.Apply("The Model here", new[] { Item("the model", "该模型") });

        Assert.Equal("该模型 here", r.Text);
    }

    [Fact]
    public void Apply_LongestTermWins()
    {
        var items = new[] { Item("AI", "人工智能"), Item("AI Agent", "AI 代理") };

        var r = GlossaryReplacer.Apply("an AI Agent appeared", items);

        Assert.Equal("an AI 代理 appeared", r.Text);
    }

    [Fact]
    public void Apply_ReplacedTextIsNotRematched()
    {
        var items = new[] { Item("A", "B"), Item("B", "C") };

        var r = GlossaryReplacer.Apply("A and B", items);

        // A→B 的结果不再被 B→C 二次命中；两条规则各自独立：原文里的 B 也会→C
        Assert.Equal("B and C", r.Text);
    }

    [Fact]
    public void Apply_CjkUsesSubstringMatch()
    {
        var r = GlossaryReplacer.Apply("占用大量内存。", new[] { Item("内存", "memory") });

        Assert.Equal("占用大量memory。", r.Text);
    }

    [Fact]
    public void Apply_EmptyItems_ReturnsOriginal()
    {
        var r = GlossaryReplacer.Apply("hello", Array.Empty<GlossaryItem>());

        Assert.Equal("hello", r.Text);
        Assert.Equal(0, r.Hits);
    }

    [Fact]
    public void Apply_DisabledItemsSkipped()
    {
        var r = GlossaryReplacer.Apply("memory", new[] { Item("memory", "内存", enabled: false) });

        Assert.Equal("memory", r.Text);
    }

    [Fact]
    public void Apply_RegexSpecialChars_AreLiteral()
    {
        var r = GlossaryReplacer.Apply("write it in c++ today", new[] { Item("c++", "C加加") });

        Assert.Equal("write it in C加加 today", r.Text);
    }

    [Fact]
    public void Apply_CountsMultipleHits()
    {
        var r = GlossaryReplacer.Apply("memory and memory", new[] { Item("memory", "内存") });

        Assert.Equal(2, r.Hits);
        Assert.Single(r.Applied);
        Assert.Equal(2, r.Applied[0].Count);
    }

    [Fact]
    public void Apply_NullOrEmptyText_Safe()
    {
        Assert.Equal("", GlossaryReplacer.Apply("", new[] { Item("a", "b") }).Text);
    }

    [Fact]
    public void Parse_CorruptedJson_ReturnsEmptyWithFlag()
    {
        var items = GlossaryReplacer.Parse("{ not json [", out var corrupted);

        Assert.True(corrupted);
        Assert.Empty(items);
    }

    [Fact]
    public void Parse_FiltersBlankEntries()
    {
        var json = GlossaryReplacer.Serialize(new[]
        {
            Item("memory", "内存"), Item("  ", "空"), Item("ok", ""),
        });

        var items = GlossaryReplacer.Parse(json, out var corrupted);

        Assert.False(corrupted);
        Assert.Single(items);
        Assert.Equal("memory", items[0].Source);
    }

    [Fact]
    public void Parse_TruncatesToMaxItems()
    {
        var many = Enumerable.Range(0, GlossaryReplacer.MaxItems + 50)
            .Select(i => Item($"term{i}", $"译{i}")).ToList();

        var items = GlossaryReplacer.Parse(GlossaryReplacer.Serialize(many), out _);

        Assert.Equal(GlossaryReplacer.MaxItems, items.Count);
    }

    [Fact]
    public void Serialize_Parse_Roundtrip()
    {
        var src = new[] { Item("load balancing", "负载均衡"), Item("幂等", "idempotent", false) };

        var back = GlossaryReplacer.Parse(GlossaryReplacer.Serialize(src), out var corrupted);

        Assert.False(corrupted);
        Assert.Equal(src.Length, back.Count);
        Assert.Equal(src[0].Enabled, back[0].Enabled);
        Assert.Equal(src[1].Enabled, back[1].Enabled);
    }
}
