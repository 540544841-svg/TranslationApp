using TranslationApp.Core.Hotkey;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>热键定义解析/标准化单元测试（FR-001）。</summary>
public class HotkeyDefinitionTests
{
    [Fact]
    public void TryParse_SimpleAltKey_ParsesCorrectly()
    {
        var ok = HotkeyDefinition.TryParse("Alt+D", out var def);

        Assert.True(ok);
        Assert.Equal(HotkeyModifiers.Alt, def.Modifiers);
        Assert.Equal(0x44, def.VirtualKey); // 'D'
        Assert.Equal("Alt+D", def.ToString());
    }

    [Fact]
    public void TryParse_MultipleModifiers_NormalizesOrder()
    {
        var ok = HotkeyDefinition.TryParse("shift+ctrl+F5", out var def);

        Assert.True(ok);
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, def.Modifiers);
        Assert.Equal(0x74, def.VirtualKey); // F5
        Assert.Equal("Ctrl+Shift+F5", def.ToString()); // 顺序标准化
    }

    [Fact]
    public void TryParse_KeyBeforeModifier_StillParses()
    {
        var ok = HotkeyDefinition.TryParse("D+Alt", out var def);

        Assert.True(ok);
        Assert.Equal("Alt+D", def.ToString());
    }

    [Fact]
    public void TryParse_NoModifier_AllowsFunctionKey()
    {
        var ok = HotkeyDefinition.TryParse("F9", out var def);

        Assert.True(ok);
        Assert.Equal(HotkeyModifiers.None, def.Modifiers);
        Assert.Equal("F9", def.ToString());
    }

    [Fact]
    public void TryParse_WinPlusNumber_Works()
    {
        var ok = HotkeyDefinition.TryParse("Win+1", out var def);

        Assert.True(ok);
        Assert.Equal(HotkeyModifiers.Win, def.Modifiers);
        Assert.Equal(0x31, def.VirtualKey); // '1'
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Alt")]          // 只有修饰键
    [InlineData("Alt+Shift")]    // 只有修饰键
    [InlineData("Ctrl+D+Q")]     // 两个主键
    [InlineData("Alt+Space")]    // 主键不支持
    [InlineData("Alt+;")]        // 符号不支持
    [InlineData("Hyper+D")]      // 未知修饰键
    [InlineData("F13")]          // 超出 F12
    [InlineData("Alt++")]        // 空段
    public void TryParse_InvalidInput_ReturnsFalse(string? text)
    {
        Assert.False(HotkeyDefinition.TryParse(text, out _));
    }

    [Theory]
    [InlineData("D")]                    // 裸字母：会劫持用户在任意程序里的打字
    [InlineData("d")]
    [InlineData("1")]                    // 裸数字同理
    public void TryParse_BareMainKeyWithoutModifier_ReturnsFalse(string text) =>
        Assert.False(HotkeyDefinition.TryParse(text, out _));

    [Fact]
    public void IsValidKey_LetterOrDigitWithoutModifier_IsInvalid()
    {
        Assert.False(new HotkeyDefinition(HotkeyModifiers.None, 'D').IsValidKey);
        Assert.False(new HotkeyDefinition(HotkeyModifiers.None, '1').IsValidKey);
        Assert.True(new HotkeyDefinition(HotkeyModifiers.Alt, 'D').IsValidKey);
        // F1-F12 允许单独使用（既有约定，见 TryParse_NoModifier_AllowsFunctionKey）
        Assert.True(new HotkeyDefinition(HotkeyModifiers.None, 0x70).IsValidKey);
    }

    [Fact]
    public void Equality_SameKeyAndModifiers_AreEqual()
    {
        // 冲突检测依赖值相等语义
        Assert.Equal(HotkeyDefinition.DefaultInput, HotkeyDefinition.ParseOrDefault("Alt+D", HotkeyDefinition.DefaultSelect));
        Assert.NotEqual(HotkeyDefinition.DefaultInput, HotkeyDefinition.DefaultSelect);
    }

    [Fact]
    public void ParseOrDefault_InvalidText_ReturnsFallback()
    {
        var fallback = HotkeyDefinition.DefaultInput;

        var def = HotkeyDefinition.ParseOrDefault("不合法", fallback);

        Assert.Equal(fallback, def);
    }
}
