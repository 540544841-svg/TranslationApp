using System.Windows;
using System.Windows.Controls;

namespace TranslationApp.Controls;

/// <summary>
/// 密钥输入控件（13.1.6）：PasswordBox 遮显 + 右侧「显示/隐藏」切换（查看时切到 TextBox）。
/// <see cref="Password"/> 为双向绑定的明文字符串 —— 仅在内存中使用，落盘由 ViewModel 走
/// <c>SecretStore.Protect</c> 加密，配置文件中只有密文。
/// 两个输入控件之间同步时必须屏蔽回调，否则会互相触发（并导致明文框光标跳到行首）。
/// </summary>
public partial class SecretBox : UserControl
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password), typeof(string), typeof(SecretBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    /// <summary>同步两个输入控件 / DP 时的重入保护。</summary>
    private bool _synchronizing;

    public SecretBox()
    {
        InitializeComponent();
        RefreshRevealGlyph();
    }

    /// <summary>密钥明文（仅内存）。</summary>
    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    /// <summary>当前是否处于明文显示状态（供外部断言/自动化使用）。</summary>
    public bool IsRevealed => Plain.Visibility == Visibility.Visible;

    /// <summary>外部（ViewModel）赋值时刷新两个输入框。</summary>
    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SecretBox)d;
        if (box._synchronizing)
        {
            return;
        }

        box._synchronizing = true;
        try
        {
            var value = e.NewValue as string ?? "";
            box.Hidden.Password = value;
            box.Plain.Text = value;
        }
        finally
        {
            box._synchronizing = false;
        }
    }

    private void OnHiddenPasswordChanged(object sender, RoutedEventArgs e) => Push(Hidden.Password, fromHidden: true);

    private void OnPlainTextChanged(object sender, TextChangedEventArgs e) => Push(Plain.Text, fromHidden: false);

    /// <summary>把用户输入写回 DP，并同步另一个输入控件；写 DP 会触发 VM 保存（密文落盘）。</summary>
    private void Push(string value, bool fromHidden)
    {
        if (_synchronizing)
        {
            return;
        }

        _synchronizing = true;
        try
        {
            if (fromHidden)
            {
                Plain.Text = value; // 明文框不可见，无需保留光标位置
            }
            else
            {
                Hidden.Password = value;
            }

            SetCurrentValue(PasswordProperty, value);
        }
        finally
        {
            _synchronizing = false;
        }
    }

    /// <summary>切换明文/密文显示。</summary>
    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        var reveal = !IsRevealed;
        Plain.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        Hidden.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        RefreshRevealGlyph();

        if (reveal)
        {
            Plain.Focus();
            Plain.CaretIndex = Plain.Text.Length;
        }
        else
        {
            Hidden.Focus();
        }
    }

    private void RefreshRevealGlyph()
    {
        var revealed = IsRevealed;
        RevealGlyph.Text = (string)(revealed ? FindResource("Icon.Conceal") : FindResource("Icon.Reveal"));
        RevealButton.ToolTip = revealed ? "隐藏密钥" : "显示密钥";
    }
}
