using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TranslationApp.Core.Capture;
using TranslationApp.Services;
// 别名：本类会生成同名属性 OcrOutputMode，直接用类型名会被属性遮蔽
using OcrMode = TranslationApp.Core.Capture.OcrOutputMode;
// 别名：同上，OcrPreprocess 属性会遮蔽 Core 的 OcrPreprocess 类型（FR-029-1）
using OcrPreprocessMode = TranslationApp.Core.Capture.OcrPreprocess;

namespace TranslationApp.ViewModels;

/// <summary>
/// 设置窗口「高级 → 截图翻译（OCR）」分区（FR-021 / 13.5.1 + FR-027 / 14.6 / 14.7）：
/// OCR 识别语言、识别后自动翻译、钉图默认显示译文、钉图缩放步进、工具条自动淡出、遮罩透明度，
/// 以及语言包缺失时的错误条。单独拆一个 partial 文件，与「引擎」页的组织方式一致。
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>遮罩透明度可调范围（默认 0.55；下限保证选区仍可辨认）。</summary>
    public const double MinScrimOpacity = 0.2;

    public const double MaxScrimOpacity = 0.9;

    /// <summary>OCR 识别语言下拉项（首项为「自动（中英识别择优）」，C-⑥）。</summary>
    public ObservableCollection<OcrLanguageOptionViewModel> OcrLanguageOptions { get; } = [];

    /// <summary>截图结果处理方式下拉项（FR-027 / 14.3.8；归属本卡片，不新开页面）。</summary>
    public ObservableCollection<OcrOutputModeOptionViewModel> OcrOutputModeOptions { get; } =
    [
        new(OcrMode.Pin, "钉在屏幕上（默认）"),
        new(OcrMode.Text, "识别文本填入翻译小窗（旧行为）"),
        new(OcrMode.Both, "两者都做（先钉图再开小窗）"),
    ];

    /// <summary>识别前预处理下拉项（FR-029-1 / 14.3.12.4；归属本卡片，不新开页面）。</summary>
    public ObservableCollection<OcrPreprocessOptionViewModel> OcrPreprocessOptions { get; } =
    [
        new(OcrPreprocessMode.ModeAuto, "自动（小字或疑似漏识时增强，默认）"),
        new(OcrPreprocessMode.ModeOn, "总是增强（多识别一遍，略慢）"),
        new(OcrPreprocessMode.ModeOff, "关闭（仅识别原图）"),
    ];

    /// <summary>本地 OCR 识别引擎下拉项（FR-030 / 14.9.3；归属本卡片，不新开页面）。</summary>
    public ObservableCollection<OcrLocalEngineOptionViewModel> OcrLocalEngineOptions { get; } =
    [
        new(OcrEngineNames.Windows, "系统引擎（Windows OCR，默认，快）"),
        new(OcrEngineNames.Paddle, "PaddleOCR 本地模型（更准，较慢）"),
    ];

    [ObservableProperty]
    private string _ocrOutputMode = OcrMode.Pin;

    [ObservableProperty]
    private string _ocrPreprocess = OcrPreprocessMode.ModeAuto;

    /// <summary>本地识别引擎（FR-030 / 14.9.3）：windows（默认）/ paddle。未知值按 windows 落盘。</summary>
    [ObservableProperty]
    private string _ocrLocalEngine = OcrEngineNames.Windows;

    /// <summary>paddle 模型会话常驻（FR-030 / 14.9.3）：默认关（空闲 5 分钟自动释放，重建约 0.5s）。</summary>
    [ObservableProperty]
    private bool _ocrPaddleResident;

    [ObservableProperty]
    private string _ocrLanguage = OcrLanguages.Auto;

    [ObservableProperty]
    private bool _ocrAutoTranslate = true;

    /// <summary>
    /// FR-027（14.6）：钉图默认显示译文。关闭时钉图的初始层是**原文**，用户点工具条「切换」或按空格才显示译文。
    /// 实现上译文是随钉图一起取好的（不额外发请求），只是不显示译文层——文案据此写清实际行为。
    /// </summary>
    [ObservableProperty]
    private bool _ocrInPlaceReplace = true;

    /// <summary>
    /// FR-027（14.6）：每格滚轮的等比缩放倍数。赋值即夹取到
    /// <see cref="PinLayout.MinZoomStep"/>~<see cref="PinLayout.MaxZoomStep"/> 并落盘。
    /// </summary>
    private double _pinZoomStep = PinLayout.DefaultZoomStep;

    [ObservableProperty]
    private bool _pinToolbarAutoFade = true;

    /// <summary>钉图缩放步进（等比倍数）。越界输入夹取后回显，绝不留下会让滚轮失效的值（≤1）。</summary>
    public double PinZoomStep
    {
        get => _pinZoomStep;
        set
        {
            var clamped = PinLayout.ClampZoomStep(value);
            var changed = Math.Abs(clamped - _pinZoomStep) > 0.0001;
            if (changed)
            {
                _pinZoomStep = clamped;
            }

            // 总是通知：越界输入（如 1.5 之外的 5）夹取后可能与当前值相同，仍需让输入框回显正确值
            OnPropertyChanged();
            if (changed)
            {
                Save(s => s.PinZoomStep = clamped);
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OcrScrimOpacityDisplay))]
    private double _ocrScrimOpacity = 0.55;

    /// <summary>系统未安装 OCR 语言包：整区显示错误条（13.2.5）。</summary>
    [ObservableProperty]
    private bool _ocrUnavailable;

    /// <summary>用户指定的 OCR 语言不在系统可用列表中：该项标红并列出可用语言。</summary>
    [ObservableProperty]
    private bool _ocrLanguageMissing;

    /// <summary>OCR 语言包状态提示（可用语言列表 / 回退说明 / 缺失指引）。</summary>
    [ObservableProperty]
    private string _ocrMessage = "";

    /// <summary>透明度百分比显示（滑块右侧）。</summary>
    public string OcrScrimOpacityDisplay => $"{(int)Math.Round(OcrScrimOpacity * 100)}%";

    /// <summary>构建 OCR 语言下拉项（首项固定为「自动」，其余来自系统可用语言包）。</summary>
    private void BuildOcrLanguageOptions()
    {
        OcrLanguageOptions.Add(new OcrLanguageOptionViewModel(
            OcrLanguages.Auto, "自动（中英识别择优）"));

        foreach (var language in _ocr.AvailableLanguages)
        {
            OcrLanguageOptions.Add(new OcrLanguageOptionViewModel(language.Tag, language.DisplayName));
        }

        RefreshCaptureMessage();
    }

    /// <summary>设置值不在可用列表中（或语言包缺失）时，下拉回落到 auto，避免显示空白项。</summary>
    private string NormalizeOcrLanguage(string? value) =>
        string.IsNullOrWhiteSpace(value) || OcrLanguageOptions.Any(o => o.Tag == value)
            ? value ?? OcrLanguages.Auto
            : OcrLanguages.Auto;

    partial void OnOcrLanguageChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        Save(s => s.OcrLanguage = value);
        RefreshCaptureMessage();
    }

    /// <summary>落盘前归一化：未知值一律按默认 pin 处理（与 OcrOutputMode.Parse 同一规则）。</summary>
    partial void OnOcrOutputModeChanged(string value) => Save(s => s.OcrOutputMode = OcrMode.Parse(value));

    /// <summary>FR-029-1（14.3.12.4）：识别前预处理三态（auto/on/off），未知值按 auto 落盘。</summary>
    partial void OnOcrPreprocessChanged(string value) =>
        Save(s => s.OcrPreprocess = OcrPreprocessMode.NormalizeMode(value));

    /// <summary>FR-030（14.9.3）：本地识别引擎两态（windows/paddle），未知值按 windows 落盘。</summary>
    partial void OnOcrLocalEngineChanged(string value) =>
        Save(s => s.OcrLocalEngine = OcrEngineNames.Normalize(value));

    /// <summary>FR-030（14.9.3）：paddle 模型会话常驻开关。</summary>
    partial void OnOcrPaddleResidentChanged(bool value) => Save(s => s.OcrPaddleResident = value);

    partial void OnOcrAutoTranslateChanged(bool value) => Save(s => s.OcrAutoTranslate = value);

    /// <summary>FR-027：钉图默认显示译文（14.6）。</summary>
    partial void OnOcrInPlaceReplaceChanged(bool value) => Save(s => s.OcrInPlaceReplace = value);

    /// <summary>FR-027：钉图工具条 2.5 s 后淡出到 35%（14.6）。</summary>
    partial void OnPinToolbarAutoFadeChanged(bool value) => Save(s => s.PinToolbarAutoFade = value);

    partial void OnOcrScrimOpacityChanged(double value) =>
        Save(s => s.OcrScrimOpacity = Math.Round(value, 2));

    /// <summary>
    /// 刷新语言包状态（13.2.5）：缺失 / 回退 / 正常三种文案。
    /// 判定用落盘值而非下拉当前值，这样「指定语言已不可用」也能被发现并标红。
    /// </summary>
    private void RefreshCaptureMessage()
    {
        var status = _ocr.ResolveStatus(_settings.OcrLanguage);

        OcrUnavailable = !status.IsAvailable;
        OcrLanguageMissing = status.IsFallback;

        if (!status.IsAvailable)
        {
            OcrMessage = OcrLanguages.MissingPackMessage;
            return;
        }

        if (status.IsFallback)
        {
            OcrMessage = OcrLanguages.DescribeFallback(status.Available);
            return;
        }

        OcrMessage = $"系统可用识别语言：{string.Join("、", status.Available.Select(tag => tag.DisplayName))}；"
                      + "识别语言为「自动」时按实际识别语言决定翻译源语言（中英各识别一次择优），"
                      + "显式指定时以其作为翻译源语言。";
    }
}

/// <summary>OCR 识别语言下拉项：Tag 为 Windows OCR 语言标签（auto = 跟随系统偏好）。</summary>
public sealed class OcrLanguageOptionViewModel
{
    public OcrLanguageOptionViewModel(string tag, string display)
    {
        Tag = tag;
        Display = display;
    }

    public string Tag { get; }

    public string Display { get; }
}

/// <summary>截图结果处理方式下拉项：Tag 为落盘值（pin / text / both，OcrOutputMode）。</summary>
public sealed class OcrOutputModeOptionViewModel
{
    public OcrOutputModeOptionViewModel(string tag, string display)
    {
        Tag = tag;
        Display = display;
    }

    public string Tag { get; }

    public string Display { get; }
}

/// <summary>识别前预处理下拉项：Tag 为落盘值（auto / on / off，OcrPreprocess）。</summary>
public sealed class OcrPreprocessOptionViewModel
{
    public OcrPreprocessOptionViewModel(string tag, string display)
    {
        Tag = tag;
        Display = display;
    }

    public string Tag { get; }

    public string Display { get; }
}

/// <summary>本地 OCR 识别引擎下拉项：Tag 为落盘值（windows / paddle，OcrLocalEngine，FR-030）。</summary>
public sealed class OcrLocalEngineOptionViewModel
{
    public OcrLocalEngineOptionViewModel(string tag, string display)
    {
        Tag = tag;
        Display = display;
    }

    public string Tag { get; }

    public string Display { get; }
}
