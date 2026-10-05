using System.Media;
using TranslationApp.Core.Settings;

namespace TranslationApp.Services;

/// <summary>
/// 操作确认音效（默认关）。使用系统提示音而不是硬编码资源，失败时静默忽略，
/// 不让反馈音效本身影响主流程。
/// </summary>
public sealed class FeedbackSoundService
{
    private readonly AppSettings _settings;

    public FeedbackSoundService(AppSettings settings) => _settings = settings;

    public void PlaySuccess()
    {
        if (!_settings.FeedbackSoundEnabled)
        {
            return;
        }

        try
        {
            SystemSounds.Asterisk.Play();
        }
        catch
        {
            // 音效失败不打扰用户，也不影响翻译主流程。
        }
    }
}
