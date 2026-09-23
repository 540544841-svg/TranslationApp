using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace TranslationApp.Core.Capture;

/// <summary>
/// 划词取词器（FR-005）：取得当前选中文本，失败返回 null（调用方降级为手动输入）。
/// </summary>
[SupportedOSPlatform("windows")]
public interface ITextCapturer
{
    /// <summary>
    /// 尝试取得当前前台应用中被选中的文本。
    /// 无法取词（无选中、应用不支持复制、前台是本程序自身）时返回 null，不抛异常。
    /// </summary>
    Task<string?> CaptureSelectedTextAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 剪贴板取词器（FR-005 MVP 唯一方案）：
/// 1. 备份剪贴板文本；2. SendInput 模拟 Ctrl+C；3. 轮询剪贴板序号变化（上限 300ms / 间隔 15ms）；
/// 4. 读取 Unicode 文本；5. 还原剪贴板。
/// 全流程异常保护：还原失败只记日志，绝不上抛导致程序崩溃。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClipboardCapturer : ITextCapturer
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int ClipboardOpenRetries = 5;
    private const int ClipboardRetryDelayMs = 20;
    private const int PollTimeoutMs = 300;   // 轮询上限（FR-005）
    private const int PollIntervalMs = 15;   // 轮询间隔（FR-005）
    private const int CaptureAttempts = 3;   // 首次未复制成功时重试（个别应用首次按键会被自身状态吞掉）
    private const int AttemptIntervalMs = 60;
    private const int ModifierReleaseTimeoutMs = 300;
    private const int ModifierPollIntervalMs = 10;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkLeftWindows = 0x5B;
    private const ushort VkRightWindows = 0x5C;
    private const ushort VkC = 0x43;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    private static readonly ushort[] ModifierVirtualKeys =
        [VkShift, VkControl, VkMenu, VkLeftWindows, VkRightWindows];

    private readonly Action<string>? _log;
    private readonly Action<TimeSpan>? _suppressFor;
    private readonly IClipboardSnapshotProvider? _snapshotProvider;

    /// <param name="log">可选的日志回调（Core 层不依赖具体日志框架）。</param>
    /// <param name="suppressFor">
    /// 抑制剪贴板监听的回调（FR-017）：本程序写剪贴板（还原取词内容）时必须让监听器忽略这次变化，
    /// 否则「监听剪贴板 → 自动翻译」会与本程序的还原动作互相触发形成循环。
    /// </param>
    public ClipboardCapturer(
        Action<string>? log = null,
        Action<TimeSpan>? suppressFor = null,
        IClipboardSnapshotProvider? snapshotProvider = null)
    {
        _log = log;
        _suppressFor = suppressFor;
        _snapshotProvider = snapshotProvider;
    }

    /// <summary>
    /// 全流程放到线程池执行：首次读剪贴板与后续轮询都可能同步重试（剪贴板被占用时 5×20ms），
    /// 而本方法由 UI 线程调用（热键触发），同步阻塞会卡住界面（FR-005 响应性）。
    /// 语义顺序不变：先读原文本与剪贴板序号，再模拟 Ctrl+C。
    /// </summary>
    public Task<string?> CaptureSelectedTextAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => CaptureSelectedTextCoreAsync(cancellationToken), cancellationToken);

    private async Task<string?> CaptureSelectedTextCoreAsync(CancellationToken cancellationToken)
    {
        if (IsOwnProcessForeground())
        {
            _log?.Invoke("前台是本程序自身，跳过取词");
            return null; // 前台是本程序自身（例如小窗输入框），模拟复制会取到自己的内容
        }

        var originalText = TryGetClipboardText();
        var snapshot = _snapshotProvider?.Capture();
        var sequenceBefore = GetClipboardSequenceNumber();
        _log?.Invoke(
            $"取词开始：目标窗口={DescribeForegroundWindow()}，剪贴板原文本 {originalText?.Length ?? -1} 字符，序号={sequenceBefore}");

        string? captured = null;
        uint? copiedSequence = null;
        var copySent = false;
        try
        {
            for (var attempt = 1; attempt <= CaptureAttempts && captured is null; attempt++)
            {
                if (attempt > 1)
                {
                    await Task.Delay(AttemptIntervalMs, cancellationToken);
                }

                // 关键：热键在按键按下的瞬间触发，此时用户往往还按着 Alt/Ctrl，
                // 若不等待松开，模拟的 Ctrl+C 会变成 Ctrl+Alt+C 而被目标应用忽略（取词失败的根因）。
                var waited = await WaitForModifiersReleasedAsync(cancellationToken);
                if (waited > 0)
                {
                    _log?.Invoke($"等待修饰键松开 {waited}ms 后再模拟复制（第 {attempt} 次）");
                }

                try
                {
                    SendCopyKeystroke();
                    copySent = true;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"模拟复制按键失败：{ex.Message}");
                    break;
                }

                captured = await WaitForClipboardTextAsync(
                    sequenceBefore, cancellationToken, sequence => copiedSequence = sequence);
                var sequenceAfter = GetClipboardSequenceNumber();
                if (sequenceAfter != sequenceBefore)
                {
                    copiedSequence = sequenceAfter;
                }

                if (captured is null && sequenceAfter != sequenceBefore)
                {
                    break; // 剪贴板已变化但内容为空：说明确实没有可取的文本，无需重试
                }
            }

            return captured;
        }
        finally
        {
            if (copySent && copiedSequence is null && cancellationToken.IsCancellationRequested)
            {
                // Ctrl+C 可能已在取消信号到达前发出，目标应用稍后才写入剪贴板。
                // 取词任务已不再等待结果，但仍要给这段尾部窗口一次有界的还原机会。
                copiedSequence = await WaitForClipboardChangeAsync(sequenceBefore);
            }

            _log?.Invoke($"取词结束：序号={GetClipboardSequenceNumber()}，取得 {captured?.Length ?? -1} 字符");
            RestoreClipboard(snapshot, originalText, copiedSequence);
        }
    }

    /// <summary>
    /// 等待 Shift/Ctrl/Alt/Win 全部松开（返回等待毫秒数）。
    /// 用户按住 Alt 时模拟 Ctrl+C 会变成 Ctrl+Alt+C，目标应用不会复制，导致取词失败。
    /// </summary>
    private static async Task<int> WaitForModifiersReleasedAsync(CancellationToken cancellationToken)
    {
        var elapsed = 0;
        while (elapsed < ModifierReleaseTimeoutMs && IsAnyModifierDown())
        {
            await Task.Delay(ModifierPollIntervalMs, cancellationToken);
            elapsed += ModifierPollIntervalMs;
        }

        return elapsed;
    }

    private static bool IsAnyModifierDown()
    {
        foreach (var virtualKey in ModifierVirtualKeys)
        {
            if ((GetAsyncKeyState(virtualKey) & 0x8000) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>轮询等待剪贴板更新并读取文本（FR-005：上限 300ms、间隔 15ms）。</summary>
    private static async Task<string?> WaitForClipboardTextAsync(
        uint sequenceBefore, CancellationToken cancellationToken, Action<uint> onChanged)
    {
        var elapsed = 0;
        while (elapsed < PollTimeoutMs)
        {
            try
            {
                await Task.Delay(PollIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 取消可能发生在 Ctrl+C 已生效、下一次轮询尚未执行时；先记录序号再退出，
                // 外层 finally 才能据此决定是否还原剪贴板。
                RecordClipboardChange(sequenceBefore, onChanged);
                throw;
            }
            elapsed += PollIntervalMs;

            var sequenceAfter = GetClipboardSequenceNumber();
            if (sequenceAfter == sequenceBefore)
            {
                continue; // 剪贴板未变化 = 还没复制成功，继续等待
            }

            onChanged(sequenceAfter);

            var text = TryGetClipboardText();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        RecordClipboardChange(sequenceBefore, onChanged);
        return null; // 超时：无选中或目标应用不支持复制，调用方降级为手动输入
    }

    /// <summary>有界等待剪贴板序号变化，不再受已取消令牌影响（仅用于取消后的尾部还原）。</summary>
    private static async Task<uint?> WaitForClipboardChangeAsync(uint sequenceBefore)
    {
        for (var elapsed = 0; elapsed < PollTimeoutMs; elapsed += PollIntervalMs)
        {
            var current = GetClipboardSequenceNumber();
            if (current != sequenceBefore)
            {
                return current;
            }

            await Task.Delay(PollIntervalMs);
        }

        var final = GetClipboardSequenceNumber();
        return final == sequenceBefore ? null : final;
    }

    private static void RecordClipboardChange(uint sequenceBefore, Action<uint> onChanged)
    {
        var current = GetClipboardSequenceNumber();
        if (current != sequenceBefore)
        {
            onChanged(current);
        }
    }

    /// <summary>还原剪贴板；仅在剪贴板已被我们改变时执行，失败只记日志。</summary>
    private void RestoreClipboard(
        IClipboardSnapshot? snapshot, string? originalText, uint? copiedSequence)
    {
        try
        {
            if (copiedSequence is null || GetClipboardSequenceNumber() != copiedSequence.Value)
            {
                return; // 未复制成功，或用户随后又改了剪贴板：不覆盖用户的新内容
            }

            // 还原前先抑制剪贴板监听，避免本程序的写入被当成「用户复制」而触发翻译
            _suppressFor?.Invoke(TimeSpan.FromSeconds(1));

            if (snapshot is not null && _snapshotProvider?.TryRestore(snapshot) == true)
            {
                _log?.Invoke("剪贴板已按原格式完整还原");
                return;
            }

            if (originalText is null)
            {
                _log?.Invoke("原剪贴板无文本内容（可能为图片等格式），快照恢复失败后无法降级还原");
                return;
            }

            SetClipboardText(originalText);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"剪贴板还原失败（不影响翻译结果）：{ex.Message}");
        }
    }

    private static bool IsOwnProcessForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>描述当前前台窗口（诊断取词失败时到底把 Ctrl+C 发给了谁）。</summary>
    private static string DescribeForegroundWindow()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return "(无)";
        }

        GetWindowThreadProcessId(foreground, out var processId);
        var className = new StringBuilder(256);
        GetClassNameW(foreground, className, className.Capacity);
        var title = new StringBuilder(256);
        GetWindowTextW(foreground, title, title.Capacity);
        var titleText = title.ToString();
        // 窗口标题可能是文档名/聊天对象名，日志只留元数据：截断到 20 字符仅用于定位「Ctrl+C 发给了谁」
        if (titleText.Length > 20)
        {
            titleText = titleText[..20] + "…";
        }

        return $"pid={processId}, class={className}, title={titleText}";
    }

    private static void SendCopyKeystroke()
    {
        var inputs = new[]
        {
            KeyboardInput(VkControl, keyUp: false),
            KeyboardInput(VkC, keyUp: false),
            KeyboardInput(VkC, keyUp: true),
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

    /// <summary>读取剪贴板文本；剪贴板被占用时重试，失败返回 null。</summary>
    private static string? TryGetClipboardText()
    {
        for (var attempt = 0; attempt < ClipboardOpenRetries; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(ClipboardRetryDelayMs);
                continue;
            }

            try
            {
                if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
                {
                    return null;
                }

                var handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        return null;
    }

    /// <summary>写入剪贴板文本（GMEM_MOVEABLE + 归属系统，成功后不得再释放句柄）。</summary>
    private static void SetClipboardText(string text)
    {
        var bytes = (text.Length + 1) * sizeof(char);
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("GlobalAlloc 失败");
        }

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException("GlobalLock 失败");
        }

        try
        {
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0); // 结尾 NUL
        }
        finally
        {
            GlobalUnlock(handle);
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            GlobalFree(handle);
            throw new InvalidOperationException("OpenClipboard 失败");
        }

        try
        {
            EmptyClipboard();
            if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                throw new InvalidOperationException("SetClipboardData 失败");
            }
        }
        finally
        {
            CloseClipboard();
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>查询按键实时状态（最高位为 1 表示当前按下）。</summary>
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
