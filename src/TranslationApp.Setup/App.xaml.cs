using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TranslationApp.Setup;

/// <summary>
/// 安装器入口。三种模式：
///   （无参数）        打开「立契」窗口，走一遍启封 → 落址 → 接引擎 → 盖印 → 契成。
///   --render-ui 目录  离屏出图（不显示任何窗口、不抢鼠标、不落盘任何系统改动），供设计核对。
///   --unseal          静默收印（供卸载登记调用，等价于 unseal.ps1 的无人值守分支）。
///
/// 与主程序一样：所有分支必须在窗口创建之前分流，避免任何一次「先开窗再判断」的闪烁。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (TryGetArgValue(e.Args, "--render-ui", out var renderDir))
        {
            RenderSetupScreens(renderDir);
            return;
        }

        if (e.Args.Contains("--unseal"))
        {
            Shutdown(Uninstaller.RunSilently() ? 0 : 1);
            return;
        }

        // 设计稿：配色随安装时刻（早六点到晚六点为纸，其余为墨）。
        SetupTheme.ApplyByInstallTime(DateTime.Now);

        var session = new SetupSession();
        var window = new SetupWindow(session);
        MainWindow = window;
        window.Show();
    }

    private static bool TryGetArgValue(string[] args, string name, out string value)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = i + 1 < args.Length ? args[i + 1] : "";
            return true;
        }

        value = "";
        return false;
    }

    // ============================================================
    // 离屏出图：把每一步的真实版面拍成 PNG，便于逐屏核对
    // ============================================================
    private void RenderSetupScreens(string outputDir)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            RenderOneTheme(true, "paper", outputDir);
            RenderOneTheme(false, "ink", outputDir);
        }
        finally
        {
            Shutdown(0);
        }
    }

    private static void RenderOneTheme(bool paper, string suffix, string outputDir)
    {
        SetupTheme.Apply(paper);
        var session = new SetupSession();
        var window = new SetupWindow(session);
        var root = (FrameworkElement)window.Content;

        for (var step = 0; step <= SetupWindow.LastStep; step++)
        {
            window.PrepareForRender(step);
            RenderElement(root, window.Width, window.Height,
                Path.Combine(outputDir, $"setup-{step:00}-{SetupWindow.StepKey(step)}-{suffix}.png"));
        }

        // 收印确认是独立一屏，也要进图
        window.PrepareForRender(SetupWindow.UnsealStep);
        RenderElement(root, window.Width, window.Height,
            Path.Combine(outputDir, $"setup-99-unseal-{suffix}.png"));
    }

    private static void RenderElement(FrameworkElement root, double width, double height, string path)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
