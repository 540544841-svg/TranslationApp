using System.Diagnostics;
using TranslationApp.Core.Settings;

namespace TranslationApp.Interop;

/// <summary>
/// 前台应用探测（FR-058）：只回答"这个窗口属于哪个进程名"。
/// 刻意不取窗口标题、路径、命令行或任何内容——按应用记语言对只需要进程名，
/// 多拿一点就从"个性化"变成"读取用户在看什么"。拿不到一律返回空串（调用方按"不知道"处理）。
/// </summary>
public static class ForegroundAppProbe
{
    public static string ProcessNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return "";
            }

            using var process = Process.GetProcessById((int)pid);
            return AppLanguageRules.Normalize(process.ProcessName);
        }
        catch (ArgumentException)
        {
            return "";   // 进程已退出
        }
        catch (Exception)
        {
            // 权限或系统异常都不该影响呼出小窗这件事
            return "";
        }
    }
}
