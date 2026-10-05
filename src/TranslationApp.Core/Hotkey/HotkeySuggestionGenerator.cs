namespace TranslationApp.Core.Hotkey;

/// <summary>
/// Generates short, low-risk alternatives for an unavailable global hotkey.
/// The caller supplies app-owned conflicts and a system availability probe,
/// so this stays deterministic and unit-testable.
/// </summary>
public static class HotkeySuggestionGenerator
{
    private static readonly HotkeyModifiers[] ModifierFamilies =
    [
        HotkeyModifiers.Ctrl | HotkeyModifiers.Alt,
        HotkeyModifiers.Ctrl | HotkeyModifiers.Shift,
        HotkeyModifiers.Alt | HotkeyModifiers.Shift,
        HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
        HotkeyModifiers.Ctrl,
        HotkeyModifiers.Alt,
        HotkeyModifiers.Shift,
        HotkeyModifiers.Win,
    ];

    private static readonly int[] FunctionKeys =
    [
        0x7B, // F12
        0x7A, // F11
        0x79, // F10
        0x78, // F9
        0x77, // F8
        0x76, // F7
        0x75, // F6
        0x74, // F5
        0x73, // F4
        0x72, // F3
        0x71, // F2
        0x70, // F1
    ];

    private static readonly int[] PreferredLetters =
    [
        'Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'J', 'K', 'L',
        'A', 'F', 'G', 'H', 'Z', 'X', 'C', 'V', 'B', 'N', 'M',
    ];

    /// <summary>
    /// Returns up to <paramref name="maxResults"/> valid alternatives.
    /// </summary>
    public static IReadOnlyList<HotkeyDefinition> Suggest(
        HotkeyDefinition requested,
        IReadOnlyCollection<HotkeyDefinition>? unavailable = null,
        Func<HotkeyDefinition, bool>? isAvailable = null,
        int maxResults = 3)
    {
        if (maxResults <= 0 || requested.Modifiers == HotkeyModifiers.None)
        {
            return [];
        }

        var blocked = unavailable ?? [];
        var results = new List<HotkeyDefinition>(maxResults);

        foreach (var candidate in EnumerateCandidates(requested))
        {
            if (candidate == requested
                || !candidate.IsValidKey
                || candidate.Modifiers == HotkeyModifiers.None
                || blocked.Contains(candidate)
                || results.Contains(candidate)
                || (isAvailable is not null && !isAvailable(candidate)))
            {
                continue;
            }

            results.Add(candidate);
            if (results.Count == maxResults)
            {
                break;
            }
        }

        return results;
    }

    private static IEnumerable<HotkeyDefinition> EnumerateCandidates(HotkeyDefinition requested)
    {
        // First try to keep the same primary key and move to a nearby modifier family.
        foreach (var modifiers in RelatedModifiers(requested.Modifiers))
        {
            yield return new HotkeyDefinition(modifiers, requested.VirtualKey);
        }

        // Function keys are already valid without a modifier, but keeping the
        // user's modifier family makes the suggestion familiar and less intrusive.
        foreach (var virtualKey in FunctionKeys)
        {
            if (virtualKey != requested.VirtualKey)
            {
                yield return new HotkeyDefinition(requested.Modifiers, virtualKey);
            }
        }

        foreach (var virtualKey in NearbyKeys(requested.VirtualKey))
        {
            yield return new HotkeyDefinition(requested.Modifiers, virtualKey);
        }

        foreach (var virtualKey in PreferredLetters)
        {
            if (virtualKey != requested.VirtualKey)
            {
                yield return new HotkeyDefinition(requested.Modifiers, virtualKey);
            }
        }

        foreach (var modifiers in RelatedModifiers(requested.Modifiers))
        {
            foreach (var virtualKey in FunctionKeys)
            {
                yield return new HotkeyDefinition(modifiers, virtualKey);
            }
        }
    }

    private static IEnumerable<HotkeyModifiers> RelatedModifiers(HotkeyModifiers requested)
    {
        foreach (var candidate in ModifierFamilies)
        {
            if (candidate != requested && (candidate & requested) != 0)
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<int> NearbyKeys(int virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z')
        {
            for (var distance = 1; distance <= 6; distance++)
            {
                var before = virtualKey - distance;
                if (before >= 'A')
                {
                    yield return before;
                }

                var after = virtualKey + distance;
                if (after <= 'Z')
                {
                    yield return after;
                }
            }
            yield break;
        }

        if (virtualKey is >= '0' and <= '9')
        {
            for (var distance = 1; distance <= 5; distance++)
            {
                var before = virtualKey - distance;
                if (before >= '0')
                {
                    yield return before;
                }

                var after = virtualKey + distance;
                if (after <= '9')
                {
                    yield return after;
                }
            }
        }
    }
}
