using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TranslationApp.Theming;
using TranslationApp.ViewModels;

namespace TranslationApp.Windows;

/// <summary>设置主窗口（FR-009）：关闭按钮=隐藏，托盘常驻应用复用同一实例。</summary>
public partial class MainWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public MainWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // 代理密码框不是可绑定属性（WPF 出于安全不暴露 Password 为依赖属性），
        // 因此回填已保存的密码并在此把输入写回 ViewModel
        ProxyPasswordBox.Password = viewModel.ProxyPassword ?? "";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 原生标题栏需单独适配深色，否则深色主题下会出现白色标题栏
        ThemeManager.ApplyTitleBar(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OnProxyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            _viewModel.ProxyPassword = box.Password;
        }
    }
}
