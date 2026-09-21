using System.Runtime.Versioning;
using System.Speech.Synthesis;

namespace TranslationApp.Core.Speech;

/// <summary>
/// 朗读服务（FR-016）：使用 Windows 系统语音（SAPI），无网络依赖。
/// 无对应语言语音包时 IsLanguageSupported 为 false，由 UI 禁用按钮并提示安装。
/// </summary>
[SupportedOSPlatform("windows")]
public interface ITtsService
{
    /// <summary>是否正在朗读。</summary>
    bool IsSpeaking { get; }

    /// <summary>当前系统是否安装了可用于该语言的语音包。</summary>
    bool IsLanguageSupported(string languageCode);

    /// <summary>朗读文本；若正在朗读则先停止（再次点击 = 停止）。</summary>
    void Speak(string text, string languageCode);

    /// <summary>
    /// 朗读一句并等它念完（FR-053 影子跟读：必须一句一念，才能在高亮上推进）。
    /// 取消时立即停止并抛 <see cref="OperationCanceledException"/>；无语音包/引擎异常按「已念完」返回，
    /// 跟读循环不该因为语音环境问题卡住。
    /// </summary>
    Task SpeakAndWaitAsync(string text, string languageCode, CancellationToken cancellationToken = default);

    /// <summary>立即停止朗读。</summary>
    void Stop();

    /// <summary>已安装语音包数量（0 表示系统无语音，需在「设置 → 时间和语言 → 语音」安装）。</summary>
    int InstalledVoiceCount { get; }
}

/// <summary>System.Speech（SAPI）实现。</summary>
[SupportedOSPlatform("windows")]
public sealed class TtsService : ITtsService, IDisposable
{
    /// <summary>跟读时轮询「还在念吗」的间隔毫秒（只影响句边界判定精度，不影响听感）。</summary>
    private const int SpeakPollMs = 60;

    /// <summary>等一句朗读「开始」的上限：语音冷启动/首次选语音包比后续慢，给 1.5s，超了就不再干等。</summary>
    private const int SpeakStartWaitMs = 1500;

    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly object _gate = new();

    public TtsService()
    {
        // 不阻塞调用线程：朗读在 SAPI 自己的线程上进行
        _synthesizer.SetOutputToDefaultAudioDevice();
    }

    public int InstalledVoiceCount
    {
        get
        {
            try
            {
                return _synthesizer.GetInstalledVoices().Count(v => v.Enabled);
            }
            catch
            {
                return 0;
            }
        }
    }

    public bool IsSpeaking
    {
        get
        {
            lock (_gate)
            {
                return _synthesizer.State == SynthesizerState.Speaking;
            }
        }
    }

    /// <summary>
    /// 语言码 → 语音包匹配。SAPI 语音按 Culture 匹配（如 zh-CN、en-US）；
    /// 找不到精确匹配时退一步按主语言匹配（zh-CN 可匹配 zh-TW 语音包）。
    /// </summary>
    public bool IsLanguageSupported(string languageCode)
    {
        try
        {
            var voices = _synthesizer.GetInstalledVoices().Where(v => v.Enabled).ToArray();
            if (voices.Length == 0)
            {
                return false;
            }

            var target = Normalize(languageCode);
            if (voices.Any(v => string.Equals(v.VoiceInfo.Culture.Name, target, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var primary = target.Split('-')[0];
            return voices.Any(v => string.Equals(v.VoiceInfo.Culture.TwoLetterISOLanguageName, primary,
                StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public void Speak(string text, string languageCode)
    {
        lock (_gate)
        {
            SpeakLocked(text, languageCode);
        }
    }

    /// <summary>
    /// 念完一句再返回（FR-053 影子跟读）。用轮询 <c>State</c> 而不是 SpeakCompleted 事件：
    /// 取消时 SAPI 的 Skip/Complete 事件会晚到，事件里再配 TaskCompletionSource 会把下一句误判成「已念完」。
    /// </summary>
    public async Task SpeakAndWaitAsync(string text, string languageCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            SpeakLocked(text, languageCode);
        }

        await WaitSpeechCycleAsync(
            () => { lock (_gate) { return _synthesizer.State == SynthesizerState.Speaking; } },
            ms => Task.Delay(ms, cancellationToken),
            () => cancellationToken.IsCancellationRequested,
            SpeakPollMs,
            SpeakStartWaitMs);
    }

    /// <summary>
    /// 一句朗读的等待节奏：先等它**真的开始念**，再等它停下。
    /// SpeakAsync 只是把文本入队，State 要等 SAPI 的异步 worker 取走任务才变成 Speaking；
    /// 只看「当前不在念」会在入队的那一瞬间就返回，跟读于是抢在语音前面一路跳句（实测症状）。
    /// 开始阶段有上限（<paramref name="startWaitMs"/>）：语音包缺失或被别的朗读抢占时绝不无限等。
    /// </summary>
    internal static async Task WaitSpeechCycleAsync(
        Func<bool> isSpeaking,
        Func<int, Task> delayAsync,
        Func<bool> isCancelled,
        int pollMs,
        int startWaitMs)
    {
        for (var waited = 0; ; waited += pollMs)
        {
            if (isSpeaking())
            {
                break;
            }

            if (isCancelled() || waited >= startWaitMs)
            {
                return;
            }

            await delayAsync(pollMs);
        }

        while (isSpeaking())
        {
            if (isCancelled())
            {
                return;
            }

            await delayAsync(pollMs);
        }
    }

    private void SpeakLocked(string text, string languageCode)
    {
        // 再次点击 = 停止当前朗读（FR-016）
        _synthesizer.SpeakAsyncCancelAll();

        var voice = FindVoice(languageCode);
        if (voice is not null)
        {
            _synthesizer.SelectVoice(voice);
        }

        _synthesizer.SpeakAsync(text);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _synthesizer.SpeakAsyncCancelAll();
        }
    }

    private string? FindVoice(string languageCode)
    {
        var voices = _synthesizer.GetInstalledVoices().Where(v => v.Enabled).ToArray();
        var target = Normalize(languageCode);

        var exact = voices.FirstOrDefault(v =>
            string.Equals(v.VoiceInfo.Culture.Name, target, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.VoiceInfo.Name;
        }

        var primary = target.Split('-')[0];
        return voices.FirstOrDefault(v =>
            string.Equals(v.VoiceInfo.Culture.TwoLetterISOLanguageName, primary,
                StringComparison.OrdinalIgnoreCase))?.VoiceInfo.Name;
    }

    /// <summary>把内部语言码归一化为 SAPI 可识别的 Culture 名。</summary>
    internal static string Normalize(string languageCode) => languageCode switch
    {
        "" or "auto" => "en-US",
        "zh-CN" => "zh-CN",
        "zh-TW" => "zh-TW",
        _ => languageCode,
    };

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _synthesizer.SpeakAsyncCancelAll();
            }
            catch
            {
                // 忽略
            }

            _synthesizer.Dispose();
        }
    }
}
