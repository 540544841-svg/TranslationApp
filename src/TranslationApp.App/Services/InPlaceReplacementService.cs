using System.Runtime.InteropServices;
using System.Windows;
using TranslationApp.Core.Capture;
using TranslationApp.Core.History;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;

namespace TranslationApp.Services;

public sealed record InPlaceTranslationResult(string Text, string EngineName, bool FromMemory);

/// <summary>
/// 原位替换专用翻译：复用当前引擎、术语表、TM 与历史入库，不触碰小窗 UI 会话。
/// </summary>
public sealed class InPlaceTranslationService(
    TranslatorCatalog catalog,
    AppSettings settings,
    IHistoryRepository history)
{
    public async Task<InPlaceTranslationResult> TranslateAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var text = sourceText.Trim();
        if (!settings.PrivacyMode && settings.TmReuseEnabled)
        {
            var candidates = history.TmCandidates(targetLanguage);
            var exact = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Source.Trim(), text, StringComparison.Ordinal));
            if (exact is not null && !string.IsNullOrWhiteSpace(exact.Translated))
            {
                return new InPlaceTranslationResult(exact.Translated, "翻译记忆", true);
            }

            var fuzzy = TmMatcher.Find(text, candidates);
            if (fuzzy is not null && !string.IsNullOrWhiteSpace(fuzzy.Entry.Translated))
            {
                return new InPlaceTranslationResult(fuzzy.Entry.Translated, "翻译记忆", true);
            }
        }

        var translator = catalog.Resolve(settings.Engine);
        var result = await translator.TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken)
            .ConfigureAwait(false);
        if (!settings.PrivacyMode && settings.ReplaceWritesHistory)
        {
            history.Add(text, result.TranslatedText, sourceLanguage, targetLanguage, translator.Name);
        }

        return new InPlaceTranslationResult(result.TranslatedText, translator.Name, false);
    }
}

public sealed record SelectionReplacementResult(bool Success, string Message);

/// <summary>
/// 把译文写回原应用的当前选区：保持原编辑控件焦点，发送一次 Ctrl+V，随后恢复原剪贴板。
/// 单次粘贴进入目标应用自己的撤销栈，因此用户可直接在原应用按 Ctrl+Z 恢复原文。
/// </summary>
public sealed class SelectionReplacementService(
    IClipboardSnapshotProvider snapshots,
    Action<TimeSpan> suppressClipboardMonitor)
{
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkMenu = 0x12;
    private const ushort VkLeftWindows = 0x5B;
    private const ushort VkRightWindows = 0x5C;
    private const ushort VkV = 0x56;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const int SwRestore = 9;

    private static readonly ushort[] Modifiers =
        [VkShift, VkControl, VkMenu, VkLeftWindows, VkRightWindows];

    public async Task<SelectionReplacementResult> ReplaceAsync(
        IntPtr targetWindow,
        string translatedText,
        CancellationToken cancellationToken = default)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow))
        {
            return new SelectionReplacementResult(false, "原应用窗口已关闭，无法原位替换");
        }

        if (string.IsNullOrEmpty(translatedText))
        {
            return new SelectionReplacementResult(false, "译文为空，未执行替换");
        }

        var snapshot = snapshots.Capture();
        var originalText = TryGetClipboardText();
        suppressClipboardMonitor(TimeSpan.FromMilliseconds(750));
        Clipboard.SetText(translatedText);
        var pastedSequence = GetClipboardSequenceNumber();

        try
        {
            await WaitForModifiersReleasedAsync(cancellationToken).ConfigureAwait(true);

            // 未失去前台时不要重新激活或 SetFocus：那会把焦点从原编辑子控件移走，选区随之失效。
            if (GetForegroundWindow() != targetWindow)
            {
                if (!ActivateTarget(targetWindow))
                {
                    return new SelectionReplacementResult(false, "无法把焦点归还原应用，未执行替换");
                }

                await Task.Delay(40, cancellationToken).ConfigureAwait(true);
                if (GetForegroundWindow() != targetWindow)
                {
                    return new SelectionReplacementResult(false, "原应用未恢复前台，已取消替换");
                }
            }

            SendPasteKeystroke();
            await Task.Delay(60, cancellationToken).ConfigureAwait(true);
            return new SelectionReplacementResult(true, "已原位替换，可用 Ctrl+Z 撤销");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SelectionReplacementResult(false, $"原位替换失败：{ex.Message}");
        }
        finally
        {
            RestoreClipboard(snapshot, originalText, pastedSequence);
        }
    }

    private void RestoreClipboard(IClipboardSnapshot? snapshot, string? originalText, uint pastedSequence)
    {
        try
        {
            if (GetClipboardSequenceNumber() != pastedSequence)
            {
                return; // 目标应用或用户已经改写剪贴板，不覆盖新内容
            }

            suppressClipboardMonitor(TimeSpan.FromSeconds(1));
            if (snapshot is not null && snapshots.TryRestore(snapshot))
            {
                return;
            }

            if (originalText is null)
            {
                Clipboard.Clear();
            }
            else
            {
                Clipboard.SetText(originalText);
            }
        }
        catch
        {
            // 剪贴板恢复失败不影响已经完成的粘贴。
        }
    }

    private static async Task WaitForModifiersReleasedAsync(CancellationToken cancellationToken)
    {
        for (var elapsed = 0; elapsed < 300; elapsed += 10)
        {
            if (Modifiers.All(key => (GetAsyncKeyState(key) & 0x8000) == 0))
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private static bool ActivateTarget(IntPtr targetWindow)
    {
        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(targetWindow, out _);
        var attached = targetThread != 0 && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            ShowWindow(targetWindow, SwRestore);
            BringWindowToTop(targetWindow);
            SetForegroundWindow(targetWindow);
            return GetForegroundWindow() == targetWindow;
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    private static void SendPasteKeystroke()
    {
        var inputs = new[]
        {
            KeyboardInput(VkControl, keyUp: false),
            KeyboardInput(VkV, keyUp: false),
            KeyboardInput(VkV, keyUp: true),
            KeyboardInput(VkControl, keyUp: true),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"SendInput 仅发送 {sent}/{inputs.Length} 个事件");
        }
    }

    private static INPUT KeyboardInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyEventKeyUp : 0,
            },
        },
    };

    private static string? TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInputStructure);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
