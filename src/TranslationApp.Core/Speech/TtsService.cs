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

    /// <summary>立即停止朗读。</summary>
    void Stop();

    /// <summary>已安装语音包数量（0 表示系统无语音，需在「设置 → 时间和语言 → 语音」安装）。</summary>
    int InstalledVoiceCount { get; }
}

/// <summary>System.Speech（SAPI）实现。</summary>
[SupportedOSPlatform("windows")]
public sealed class TtsService : ITtsService, IDisposable
{
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
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
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
