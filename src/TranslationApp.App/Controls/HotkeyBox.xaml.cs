using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TranslationApp.Core.Hotkey;

namespace TranslationApp.Controls;

/// <summary>
/// 热键录制控件（FR-009）：点击进入录制态，按下组合键（修饰键 + 字母/数字/F1~F12）即捕获；
/// Esc 取消本次录制；IsInvalid=true 时进入错误态（冲突/非法）。
/// Text 为标准化热键字符串（如 "Alt+D"），通过双向绑定与 ViewModel 交互。
/// 视觉状态（录制中 / 错误 / 按键帽 / 空值）由 XAML 的 DataTrigger 与 ItemsControl 呈现。
/// </summary>
public partial class HotkeyBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    public static readonly DependencyProperty IsInvalidProperty = DependencyProperty.Register(
        nameof(IsInvalid), typeof(bool), typeof(HotkeyBox),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsRecordingProperty = DependencyProperty.Register(
        nameof(IsRecording), typeof(bool), typeof(HotkeyBox),
        new PropertyMetadata(false, OnIsRecordingChanged));

    public HotkeyBox()
    {
        InitializeComponent();
    }

    /// <summary>标准化热键字符串，如 "Alt+D"。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>是否为无效/冲突状态（描边转错误色）。</summary>
    public bool IsInvalid
    {
        get => (bool)GetValue(IsInvalidProperty);
        set => SetValue(IsInvalidProperty, value);
    }

    /// <summary>是否处于录制中（描边转主色并显示提示）。</summary>
    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HotkeyBox)d).RefreshDisplay();

    private static void OnIsRecordingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HotkeyBox)d).RefreshDisplay();

    /// <summary>按当前状态刷新三种呈现：录制提示 / 按键帽 / 空值。</summary>
    private void RefreshDisplay()
    {
        var recording = IsRecording;
        var hotkey = Text ?? "";

        RecordingHint.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        Keycaps.Visibility = !recording && !string.IsNullOrEmpty(hotkey) ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = !recording && string.IsNullOrEmpty(hotkey) ? Visibility.Visible : Visibility.Collapsed;

        if (recording)
        {
            return;
        }

        Keycaps.ItemsSource = string.IsNullOrEmpty(hotkey) ? null : SplitKeycaps(hotkey);
    }

    /// <summary>"Alt+Shift+D" → ["Alt","Shift","D"]，用于渲染成独立键帽。</summary>
    private static IReadOnlyList<string> SplitKeycaps(string hotkey) =>
        hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus(); // 点击任意位置即开始录制
        e.Handled = true;
    }

    private void OnBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        IsRecording = true;
        if (IsInvalid)
        {
            // 重新录制时清除错误态，给用户干净的起点
            SetCurrentValue(IsInvalidProperty, false);
        }
    }

    private void OnBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => IsRecording = false;

    private void OnBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            IsRecording = false;
            e.Handled = true;
            return;
        }

        // 仅按下修饰键：保持录制态等待主键，不提交
        if (e.Key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        // Alt 组合时 WPF 把主键放在 SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var definition = new HotkeyDefinition((HotkeyModifiers)Keyboard.Modifiers, virtualKey);

        if (!definition.IsValidKey)
        {
            e.Handled = true; // 不支持的主键（需至少一个修饰键 + 允许的主键）
            return;
        }

        SetCurrentValue(TextProperty, definition.ToString()); // 触发 VM 即时保存与重注册
        IsRecording = false;
        e.Handled = true;
    }
}
