using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TranslationApp.Controls;
using TranslationApp.Core.Hotkey;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using TranslationApp.Services;

namespace TranslationApp.Windows;

/// <summary>
/// 首次使用三步向导：选择引擎 → 录入核心热键 → 运行能力自检。
/// 向导只读取/写入现有设置，不另建配置通道；失败项通过事件交回宿主打开 Doctor。
/// </summary>
public partial class FirstRunGuideWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;
    private readonly HotkeyManager _hotkeys;
    private readonly TranslatorCatalog _catalog;
    private readonly OcrService _ocr;
    private readonly InPlaceTranslationService _translations;
    private int _step = 1;
    private bool _checked;
    private int _checkFailures;
    private string? _practiceTranslation;

    public event EventHandler? OpenDoctorRequested;

    public FirstRunGuideWindow(
        AppSettings settings,
        ISettingsStore store,
        HotkeyManager hotkeys,
        TranslatorCatalog catalog,
        OcrService ocr,
        InPlaceTranslationService translations)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
        _hotkeys = hotkeys;
        _catalog = catalog;
        _ocr = ocr;
        _translations = translations;

        var choices = catalog.All
            .Where(engine => engine.IsConfigured)
            .Select(engine => new EngineChoice(engine.Id, engine.Name))
            .ToArray();
        EngineSelector.ItemsSource = choices;
        EngineSelector.SelectedValue = catalog.Resolve(settings.Engine).Id;
        InputHotkeyBox.Text = settings.HotkeyInputTranslate;
        SelectHotkeyBox.Text = settings.HotkeySelectTranslate;
        CaptureHotkeyBox.Text = settings.HotkeyCaptureTranslate;
        InputHotkeyBox.HotkeyCaptured += OnHotkeyCaptured;
        SelectHotkeyBox.HotkeyCaptured += OnHotkeyCaptured;
        CaptureHotkeyBox.HotkeyCaptured += OnHotkeyCaptured;
        PracticeSourceText.Text = "A reliable tool should reduce effort, not add another thing to remember.";
        PracticeTargetText.Text = PracticeSourceText.Text;
        EngineStatus.Text = choices.Length > 1
            ? "当前引擎可随时在“设置 → 翻译”中更换。"
            : "当前使用零配置引擎，后续可添加官方引擎或 AI 引擎。";
        ShowStep();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _hotkeys.SuspendAll();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        _hotkeys.ResumeAll();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotkeys.ResumeAll();
        base.OnClosed(e);
    }

    private sealed record EngineChoice(string Id, string Display);

    private void OnHotkeyCaptured(object? sender, HotkeyBox.HotkeyCapturedEventArgs e)
    {
        if (sender is not HotkeyBox box)
        {
            return;
        }

        var (name, label) = box == InputHotkeyBox
            ? ("input", "输入翻译")
            : box == SelectHotkeyBox
                ? ("select", "划词翻译")
                : ("capture", "截图翻译");
        ValidateGuideHotkey(box, name, label, e.Hotkey);
    }

    private void OnResetHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
        {
            return;
        }

        var (box, label, fallback) = name switch
        {
            "select" => (SelectHotkeyBox, "划词翻译", HotkeyDefinition.DefaultSelect),
            "capture" => (CaptureHotkeyBox, "截图翻译", HotkeyDefinition.DefaultCapture),
            _ => (InputHotkeyBox, "输入翻译", HotkeyDefinition.DefaultInput),
        };
        box.Text = fallback.ToString();
        ValidateGuideHotkey(box, name, label, box.Text);
    }

    private void ValidateGuideHotkey(HotkeyBox box, string name, string label, string hotkey)
    {
        if (!HotkeyDefinition.TryParse(hotkey, out var definition))
        {
            box.IsInvalid = true;
            box.Status = "格式不支持：需要 Ctrl / Alt / Shift / Win + 字母、数字或 F1~F12";
            box.Suggestions = Array.Empty<string>();
            return;
        }

        var core = new[]
        {
            ("input", "输入翻译", InputHotkeyBox.Text),
            ("select", "划词翻译", SelectHotkeyBox.Text),
            ("capture", "截图翻译", CaptureHotkeyBox.Text),
        };
        var duplicate = core.FirstOrDefault(item =>
            item.Item1 != name &&
            HotkeyDefinition.TryParse(item.Item3, out var other) &&
            other == definition);
        if (duplicate.Item1 is not null)
        {
            box.IsInvalid = true;
            box.Status = $"与“{duplicate.Item2}”重复，请换一个组合";
            box.Suggestions = BuildGuideSuggestions(name, definition);
            return;
        }

        var reserved = new[]
        {
            ("场景模式切换", _settings.HotkeySwitchProfile),
            ("翻译并替换", _settings.HotkeyReplaceTranslate),
        };
        var reservedMatch = reserved.FirstOrDefault(item =>
            HotkeyDefinition.TryParse(item.Item2, out var other) && other == definition);
        if (reservedMatch.Item1 is not null)
        {
            box.IsInvalid = true;
            box.Status = $"与“{reservedMatch.Item1}”重复，请换一个组合";
            box.Suggestions = BuildGuideSuggestions(name, definition);
            return;
        }

        if (!_hotkeys.CanRegister(name, definition))
        {
            box.IsInvalid = true;
            box.Status = $"{definition} 已被系统或其他程序占用";
            box.Suggestions = BuildGuideSuggestions(name, definition);
            return;
        }

        box.IsInvalid = false;
        box.Status = $"{definition} 可用";
        box.Suggestions = Array.Empty<string>();
    }

    private IReadOnlyList<string> BuildGuideSuggestions(string name, HotkeyDefinition requested)
    {
        var appOwned = new[]
        {
            InputHotkeyBox.Text,
            SelectHotkeyBox.Text,
            CaptureHotkeyBox.Text,
            _settings.HotkeySwitchProfile,
            _settings.HotkeyReplaceTranslate,
        }
            .Select(text => HotkeyDefinition.TryParse(text, out var definition)
                ? definition
                : (HotkeyDefinition?)null)
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .ToArray();

        return HotkeySuggestionGenerator.Suggest(
                requested,
                appOwned,
                candidate => _hotkeys.CanRegister(name, candidate))
            .Select(definition => definition.ToString())
            .ToArray();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step == 1)
        {
            if (EngineSelector.SelectedValue is string engineId)
            {
                _settings.Engine = engineId;
                _store.Save(_settings);
            }

            _step = 2;
            ShowStep();
            return;
        }

        if (_step == 2)
        {
            if (!TrySaveCoreHotkeys())
            {
                return;
            }

            _step = 3;
            ShowStep();
            return;
        }

        if (_step == 3)
        {
            _step = 4;
            ShowStep();
            RunChecks();
            return;
        }

        if (!_checked)
        {
            RunChecks();
            return;
        }


        if (_checkFailures > 0)
        {
            RunChecks();
            return;
        }

        Close();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_step > 1)
        {
            _step--;
            ShowStep();
        }
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        if (_step == 3)
        {
            _step = 4;
            ShowStep();
            RunChecks();
        }
        else if (_step == 4)
        {
            Close();
        }
    }

    private void OnOpenDoctorClick(object sender, RoutedEventArgs e)
    {
        OpenDoctorRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private async void OnPracticeTranslateClick(object sender, RoutedEventArgs e)
    {
        var source = PracticeSourceText.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(source))
        {
            PracticeMessage.Foreground = (Brush)FindResource("Brush.Error");
            PracticeMessage.Text = "先输入一段练习文字。";
            return;
        }

        PracticeTranslateButton.IsEnabled = false;
        PracticeMessage.Foreground = (Brush)FindResource("Brush.TextTertiary");
        PracticeMessage.Text = "正在调用当前引擎…";
        try
        {
            var result = await _translations.TranslateAsync(
                source, _settings.SourceLanguage, _settings.TargetLanguage);
            _practiceTranslation = result.Text;
            PracticeReplaceButton.IsEnabled = !string.IsNullOrWhiteSpace(_practiceTranslation);
            PracticeMessage.Foreground = (Brush)FindResource("Brush.Success");
            PracticeMessage.Text = result.FromMemory
                ? "已从翻译记忆取回结果；现在可以验证替换与 Ctrl+Z 撤销。"
                : $"已由 {result.EngineName} 完成；现在可以验证替换与 Ctrl+Z 撤销。";
        }
        catch (Exception ex)
        {
            _practiceTranslation = null;
            PracticeReplaceButton.IsEnabled = false;
            PracticeMessage.Foreground = (Brush)FindResource("Brush.Error");
            PracticeMessage.Text = $"翻译失败：{ex.Message}";
        }
        finally
        {
            PracticeTranslateButton.IsEnabled = true;
        }
    }

    private void OnPracticeReplaceClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_practiceTranslation))
        {
            return;
        }

        PracticeTargetText.Focus();
        PracticeTargetText.SelectAll();
        PracticeTargetText.SelectedText = _practiceTranslation;
        PracticeTargetText.CaretIndex = PracticeTargetText.Text.Length;
        PracticeMessage.Foreground = (Brush)FindResource("Brush.TextTertiary");
        PracticeMessage.Text = "已替换到练习区；现在按 Ctrl+Z 应恢复原文。";
    }

    private void OnPracticeResetClick(object sender, RoutedEventArgs e)
    {
        _practiceTranslation = null;
        PracticeTargetText.Text = PracticeSourceText.Text;
        PracticeReplaceButton.IsEnabled = false;
        PracticeMessage.Foreground = (Brush)FindResource("Brush.TextTertiary");
        PracticeMessage.Text = "练习区已重置。";
    }

    private bool TrySaveCoreHotkeys()
    {
        HotkeyError.Visibility = Visibility.Collapsed;
        var edits = new[]
        {
            new HotkeyEdit("input", "输入翻译", InputHotkeyBox.Text, _settings.HotkeyInputTranslate,
                HotkeyDefinition.DefaultInput, value => _settings.HotkeyInputTranslate = value),
            new HotkeyEdit("select", "划词翻译", SelectHotkeyBox.Text, _settings.HotkeySelectTranslate,
                HotkeyDefinition.DefaultSelect, value => _settings.HotkeySelectTranslate = value),
            new HotkeyEdit("capture", "截图翻译", CaptureHotkeyBox.Text, _settings.HotkeyCaptureTranslate,
                HotkeyDefinition.DefaultCapture, value => _settings.HotkeyCaptureTranslate = value),
        };

        var parsed = new List<(HotkeyEdit Edit, HotkeyDefinition Definition)>();
        foreach (var edit in edits)
        {
            if (!HotkeyDefinition.TryParse(edit.Value?.Trim(), out var definition))
            {
                ShowHotkeyError($"{edit.Label}热键格式不正确：需为修饰键 + 字母、数字或 F1~F12。");
                SetGuideHotkeyError(edit.Name, $"{edit.Label}热键格式不正确：需要 Ctrl / Alt / Shift / Win + 字母、数字或 F1~F12");
                return false;
            }
            parsed.Add((edit, definition));
        }

        var reserved = new[] { _settings.HotkeySwitchProfile, _settings.HotkeyReplaceTranslate };
        for (var i = 0; i < parsed.Count; i++)
        {
            for (var j = i + 1; j < parsed.Count; j++)
            {
                if (parsed[i].Definition == parsed[j].Definition)
                {
                    ShowHotkeyError("核心热键不能重复，请为每个功能选择不同组合。");
                    SetGuideHotkeyError(parsed[i].Edit.Name, $"{parsed[i].Edit.Label}与其他核心热键重复", parsed[i].Definition);
                    SetGuideHotkeyError(parsed[j].Edit.Name, $"{parsed[j].Edit.Label}与其他核心热键重复", parsed[j].Definition);
                    return false;
                }
            }

            if (reserved.Any(text => HotkeyDefinition.TryParse(text, out var other) && other == parsed[i].Definition))
            {
                ShowHotkeyError($"{parsed[i].Edit.Label}的组合已分配给其他功能，请换一个组合。");
                SetGuideHotkeyError(parsed[i].Edit.Name, $"{parsed[i].Edit.Label}的组合已分配给其他功能", parsed[i].Definition);
                return false;
            }
        }

        var applied = new List<(string Name, HotkeyDefinition Previous)>();
        foreach (var (edit, definition) in parsed)
        {
            var previous = HotkeyDefinition.ParseOrDefault(edit.Previous, edit.Fallback);
            if (!_hotkeys.TryRegister(edit.Name, definition))
            {
                foreach (var rollback in Enumerable.Reverse(applied))
                {
                    _hotkeys.TryRegister(rollback.Name, rollback.Previous);
                }
                RestoreCoreHotkeyBoxes();
                ShowHotkeyError($"{edit.Label}热键 {definition} 注册失败，可能已被其他程序占用；原热键已保留。");
                SetGuideHotkeyError(edit.Name, $"{definition} 已被系统或其他程序占用；原热键仍可用", definition);
                return false;
            }
            applied.Add((edit.Name, previous));
        }

        foreach (var (edit, definition) in parsed)
        {
            edit.Save(definition.ToString());
            SetGuideHotkeyError(edit.Name, "");
        }
        _store.Save(_settings);
        return true;
    }

    private void RestoreCoreHotkeyBoxes()
    {
        InputHotkeyBox.Text = _settings.HotkeyInputTranslate;
        SelectHotkeyBox.Text = _settings.HotkeySelectTranslate;
        CaptureHotkeyBox.Text = _settings.HotkeyCaptureTranslate;
    }

    private sealed record HotkeyEdit(
        string Name,
        string Label,
        string? Value,
        string Previous,
        HotkeyDefinition Fallback,
        Action<string> Save);

    private void ShowHotkeyError(string message)
    {
        HotkeyErrorText.Text = message;
        HotkeyError.Visibility = Visibility.Visible;
    }

    private void SetGuideHotkeyError(string name, string message, HotkeyDefinition? requested = null)
    {
        var box = name switch
        {
            "select" => SelectHotkeyBox,
            "capture" => CaptureHotkeyBox,
            _ => InputHotkeyBox,
        };
        box.Suggestions = requested is not null && !string.IsNullOrEmpty(message)
            ? BuildGuideSuggestions(name, requested)
            : Array.Empty<string>();
        box.IsInvalid = !string.IsNullOrEmpty(message);
        box.Status = message;
    }

    private void RunChecks()
    {
        _checked = true;
        var engine = _catalog.Resolve(_settings.Engine);
        var engineOk = engine.IsConfigured;
        var coreHotkeys = new[]
        {
            ("input", _settings.HotkeyInputTranslate),
            ("select", _settings.HotkeySelectTranslate),
            ("capture", _settings.HotkeyCaptureTranslate),
        };
        var hotkeyOk = coreHotkeys.All(pair =>
            HotkeyDefinition.TryParse(pair.Item2, out _) && _hotkeys.IsRegistered(pair.Item1));
        var ocrOk = _ocr.IsAvailable;

        SetCheck(EngineCheckGlyph, EngineCheckDetail, engineOk,
            engineOk ? $"{engine.Name} 已就绪" : "未找到可用引擎，请返回上一步");
        SetCheck(HotkeyCheckGlyph, HotkeyCheckDetail, hotkeyOk,
            hotkeyOk ? "输入 / 划词 / 截图热键均已注册" : "输入、划词或截图热键未全部注册");
        SetCheck(OcrCheckGlyph, OcrCheckDetail, ocrOk,
            ocrOk ? $"检测到 {_ocr.AvailableLanguages.Count} 个 OCR 语言包" : "未检测到 OCR 语言包");

        var failures = new[] { engineOk, hotkeyOk, ocrOk }.Count(ok => !ok);
        _checkFailures = failures;
        CheckSummary.Text = failures == 0
            ? "三项检查均通过，可以直接开始使用。"
            : $"{failures} 项需要处理；可打开 Doctor 查看详细原因和修复入口。";
        CheckSummary.Foreground = failures == 0
            ? (Brush)FindResource("Brush.Success")
            : (Brush)FindResource("Brush.Warning");
        OpenDoctorButton.Visibility = failures == 0 ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.Visibility = failures == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = failures == 0 ? "完成" : "重新检查";
    }

    private static void SetCheck(
        TextBlock glyph,
        TextBlock detail,
        bool passed,
        string message)
    {
        glyph.Text = passed ? "\uE73E" : "\uEA39";
        glyph.Foreground = (Brush)glyph.FindResource(passed ? "Brush.Success" : "Brush.Error");
        detail.Text = message;
    }

    private void ShowStep()
    {
        EnginePage.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        HotkeyPage.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        PracticePage.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        CheckPage.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        Step1Dot.Background = (Brush)FindResource(_step >= 1 ? "Brush.Primary" : "Brush.SurfaceMuted");
        Step2Dot.Background = (Brush)FindResource(_step >= 2 ? "Brush.Primary" : "Brush.SurfaceMuted");
        Step3Dot.Background = (Brush)FindResource(_step >= 3 ? "Brush.Primary" : "Brush.SurfaceMuted");
        Step4Dot.Background = (Brush)FindResource(_step >= 4 ? "Brush.Primary" : "Brush.SurfaceMuted");

        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.Content = _step == 3 ? "跳过练习" : "稍后测试";
        SecondaryButton.Visibility = _step >= 3 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step switch
        {
            1 => "下一步",
            2 => "下一步",
            3 => "开始自检",
            _ => "运行自检",
        };
        if (_step == 4)
        {
            _checked = false;
            _checkFailures = 0;
            OpenDoctorButton.Visibility = Visibility.Collapsed;
            CheckSummary.Text = "点击“运行自检”确认引擎、核心热键与 OCR 能力。";
            NextButton.Content = "运行自检";
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
