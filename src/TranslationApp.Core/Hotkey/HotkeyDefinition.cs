namespace TranslationApp.Core.Hotkey;

/// <summary>RegisterHotKey 修饰键位（值与 Win32 MOD_* 常量一致）。</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,     // MOD_ALT
    Ctrl = 0x0002,    // MOD_CONTROL
    Shift = 0x0004,   // MOD_SHIFT
    Win = 0x0008,     // MOD_WIN
}

/// <summary>
/// 热键定义（修饰键 + 虚拟键）。字符串形式如 "Alt+D"、"Ctrl+Shift+F1"，
/// TryParse 接受任意顺序并做标准化（ToString 固定 Ctrl/Alt/Shift/Win + 主键顺序）。
/// 支持主键范围：A-Z、0-9、F1-F12（FR-001）。
/// </summary>
public sealed record HotkeyDefinition(HotkeyModifiers Modifiers, int VirtualKey)
{
    private const int VkF1 = 0x70;
    private const int VkF12 = 0x7B;

    /// <summary>默认热键：输入翻译 Alt+D。</summary>
    public static readonly HotkeyDefinition DefaultInput = new(HotkeyModifiers.Alt, 'D');

    /// <summary>默认热键：划词翻译 Alt+S。</summary>
    public static readonly HotkeyDefinition DefaultSelect = new(HotkeyModifiers.Alt, 'S');

    /// <summary>默认热键：截图翻译 Alt+O（FR-021，13.7）。</summary>
    public static readonly HotkeyDefinition DefaultCapture = new(HotkeyModifiers.Alt, 'O');

    /// <summary>默认热键：场景档案循环切换 Alt+P（FR-037，P0 批 2）。</summary>
    public static readonly HotkeyDefinition DefaultProfile = new(HotkeyModifiers.Alt, 'P');

    /// <summary>默认热键：翻译并原位替换 Alt+R。</summary>
    public static readonly HotkeyDefinition DefaultReplace = new(HotkeyModifiers.Alt, 'R');

    /// <summary>解析失败时返回的默认值（避免设置串损坏导致启动失败）。</summary>
    public static HotkeyDefinition ParseOrDefault(string? text, HotkeyDefinition fallback) =>
        TryParse(text, out var def) ? def : fallback;

    public static bool TryParse(string? text, out HotkeyDefinition definition)
    {
        definition = DefaultInput;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        int? virtualKey = null;

        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                return false;
            }

            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= HotkeyModifiers.Ctrl;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN" or "WINDOWS":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    if (virtualKey.HasValue || !TryParseVirtualKey(part, out var vk))
                    {
                        return false; // 出现第二个主键，或主键不在支持范围
                    }
                    virtualKey = vk;
                    break;
            }
        }

        if (!virtualKey.HasValue)
        {
            return false; // 只有修饰键、没有主键
        }

        var parsed = new HotkeyDefinition(modifiers, virtualKey.Value);
        if (!parsed.IsValidKey)
        {
            return false; // 裸字母/数字会被注册成全局热键而劫持用户在任意程序里的打字
        }

        definition = parsed;
        return true;
    }

    private static bool TryParseVirtualKey(string name, out int virtualKey)
    {
        var upper = name.ToUpperInvariant();
        virtualKey = 0;

        // A-Z / 0-9
        if (upper.Length == 1)
        {
            var c = upper[0];
            if (c is >= 'A' and <= 'Z')
            {
                virtualKey = c;
                return true;
            }
            if (c is >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
            return false;
        }

        // F1-F12
        if (upper[0] == 'F' && int.TryParse(upper[1..], out var f) && f is >= 1 and <= 12)
        {
            virtualKey = VkF1 + f - 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 主键在支持范围内（A-Z、0-9、F1-F12），且不会劫持用户打字：
    /// 字母/数字必须搭配至少一个修饰键（否则按下该键会全局触发本程序）；
    /// F1-F12 本就不用于文本输入，允许单独使用（沿用既有约定，见 HotkeyDefinitionTests）。
    /// </summary>
    public bool IsValidKey =>
        (VirtualKey is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= VkF1 and <= VkF12)
        && (Modifiers != HotkeyModifiers.None || VirtualKey is >= VkF1 and <= VkF12);

    public override string ToString()
    {
        var name = VirtualKeyName(VirtualKey);
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(name);
        return string.Join("+", parts);
    }

    private static string VirtualKeyName(int virtualKey)
    {
        if (virtualKey is >= VkF1 and <= VkF12)
        {
            return "F" + (virtualKey - VkF1 + 1);
        }
        return ((char)virtualKey).ToString().ToUpperInvariant();
    }
}
