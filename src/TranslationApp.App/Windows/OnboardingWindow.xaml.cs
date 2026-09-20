using System.Windows;
using System.Windows.Input;
using TranslationApp.Core.Settings;

namespace TranslationApp.Windows;

/// <summary>
/// 首次运行上手卡（FR-059 / 批 6 spec §4）：一屏看完三个入口，点「知道了」或按 Esc 关闭。
/// 不做多页向导、不做联网检查、不收集任何数据——它的唯一目标是"第一次打开就知道按什么"。
/// </summary>
public partial class OnboardingWindow : Window
{
    /// <summary>一行：键帽 + 名称 + 一句说明。</summary>
    public sealed record Row(string Hotkey, string Title, string Hint);

    public IReadOnlyList<Row> Rows { get; }

    public OnboardingWindow(AppSettings settings)
    {
        InitializeComponent();
        // 键帽取设置里的**真实热键**：用户改过热键后再看这张卡，不能显示出厂默认值
        Rows =
        [
            new(settings.HotkeyInputTranslate, "输入翻译", "打开输入窗，粘贴或打字，回车出译文"),
            new(settings.HotkeySelectTranslate, "划词翻译", "选中任意文字后按它，取词→清洗→翻译一步到位"),
            new(settings.HotkeyCaptureTranslate, "截图翻译", "框选屏幕区域，识别后可原位覆盖译文或钉图"),
        ];
        DataContext = this;
    }

    private void OnDismissClick(object sender, RoutedEventArgs e) => Close();

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
