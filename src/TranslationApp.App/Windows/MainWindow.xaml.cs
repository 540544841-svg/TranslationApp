using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TranslationApp.Core.Hotkey;
using TranslationApp.Theming;
using TranslationApp.ViewModels;

namespace TranslationApp.Windows;

/// <summary>设置主窗口（FR-009）：关闭按钮=隐藏，托盘常驻应用复用同一实例。</summary>
public partial class MainWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly HotkeyManager _hotkeys;

    public MainWindow(SettingsViewModel viewModel, HotkeyManager hotkeys)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _hotkeys = hotkeys;
        _viewModel.SettingsNavigationRequested += (_, section) => NavigateToSection(section);
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

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 设置窗口录制热键时先释放全局注册，否则 Windows 会把现有组合吞成应用热键。
        _hotkeys.SuspendAll();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        _hotkeys.ResumeAll();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        _hotkeys.ResumeAll();
        Hide();
    }

    private void OnProxyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            _viewModel.ProxyPassword = box.Password;
        }
    }

    /// <summary>切到「引擎」页时刷新近 7 天成败看板（P0 批 1 / spec §4.3）。</summary>
    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, NavTabs)) return;
        if ((NavTabs.SelectedItem as TabItem)?.Header as string == "引擎")
        {
            _viewModel.RefreshEngineStats();
        }
    }

    /// <summary>按导航页签名切换设置分区；Doctor 的“打开设置”操作复用这里。</summary>
    public void NavigateToSection(string section)
    {
        var header = section switch
        {
            "hotkeys" => "热键",
            "advanced" => "高级",
            "updates" => "更新与数据",
            _ => section,
        };

        var tab = NavTabs.Items.OfType<TabItem>()
            .FirstOrDefault(item => string.Equals(item.Header as string, header, StringComparison.Ordinal));
        if (tab is null)
        {
            return;
        }

        NavTabs.SelectedItem = tab;
        Activate();
    }
}
