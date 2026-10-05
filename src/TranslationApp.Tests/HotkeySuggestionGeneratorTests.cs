using TranslationApp.Core.Hotkey;
using Xunit;

namespace TranslationApp.Tests;

public sealed class HotkeySuggestionGeneratorTests
{
    [Fact]
    public void Suggest_PrefersRelatedModifierForSameKey()
    {
        var requested = new HotkeyDefinition(HotkeyModifiers.Alt, 'D');

        var suggestions = HotkeySuggestionGenerator.Suggest(requested, maxResults: 3);

        Assert.Equal(3, suggestions.Count);
        Assert.Contains(suggestions, item =>
            item.VirtualKey == 'D' && item.Modifiers.HasFlag(HotkeyModifiers.Alt));
        Assert.DoesNotContain(requested, suggestions);
        Assert.All(suggestions, item => Assert.True(item.IsValidKey));
    }

    [Fact]
    public void Suggest_ExcludesAppOwnedAndSystemOccupiedCombinations()
    {
        var requested = new HotkeyDefinition(HotkeyModifiers.Alt, 'D');
        var appOwned = new HotkeyDefinition(
            HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'D');
        var systemOwned = new HotkeyDefinition(HotkeyModifiers.Alt, 0x7B);

        var suggestions = HotkeySuggestionGenerator.Suggest(
            requested,
            [appOwned, systemOwned],
            candidate => candidate != new HotkeyDefinition(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'D'),
            maxResults: 5);

        Assert.DoesNotContain(appOwned, suggestions);
        Assert.DoesNotContain(systemOwned, suggestions);
        Assert.DoesNotContain(
            new HotkeyDefinition(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'D'),
            suggestions);
        Assert.Equal(suggestions.Count, suggestions.Distinct().Count());
    }

    [Fact]
    public void Suggest_NeverReturnsBareLetterOrNumber()
    {
        var requested = new HotkeyDefinition(HotkeyModifiers.Win, 'K');

        var suggestions = HotkeySuggestionGenerator.Suggest(requested, maxResults: 20);

        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, item => Assert.NotEqual(HotkeyModifiers.None, item.Modifiers));
    }
}
