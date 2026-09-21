# Batch 6c hook probe (ascii-only source).
#
# The app logs that both low-level hooks installed, so the remaining question is whether any
# input event actually reaches them. This injects exactly the two gestures the user says are
# dead - a double-tap of left Alt and a mouse X1 button - while charmap holds the foreground
# (the app ignores its own foreground, per design), then reports what the log recorded and
# whether the quick window appeared.
#
# Requires a TranslationApp instance already running with the hooks enabled.
param(
    [int]$GapMs = 120,
    [int]$WaitSeconds = 6
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Hk {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr extra; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr h, StringBuilder t, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    private const uint MOUSE_ABS = 0x8000, MOUSE_MOVE = 0x0001, MOUSE_DOWN = 0x0002, MOUSE_UP = 0x0004;
    private const uint X_DOWN = 0x0020, X_UP = 0x0040;
    private const uint KEY_UP = 0x0002;

    private static void Send(INPUT[] items) { SendInput((uint)items.Length, items, Marshal.SizeOf(typeof(INPUT))); }

    public static void Key(ushort vk, bool up) {
        INPUT i = new INPUT(); i.type = 1;
        i.U.ki.wVk = vk;
        i.U.ki.dwFlags = up ? KEY_UP : 0u;
        Send(new INPUT[] { i });
    }
    private static void MoveTo(int x, int y) {
        INPUT m = new INPUT(); m.type = 0;
        m.U.mi.dx = (int)Math.Round(x * 65535.0 / (GetSystemMetrics(0) - 1));
        m.U.mi.dy = (int)Math.Round(y * 65535.0 / (GetSystemMetrics(1) - 1));
        m.U.mi.dwFlags = MOUSE_MOVE | MOUSE_ABS;
        Send(new INPUT[] { m });
    }
    /// button: 1 = X1, 2 = X2. Sends a real down/up pair with the correct mouseData high word.
    public static void SideButton(int button) {
        INPUT d = new INPUT(); d.type = 0;
        d.U.mi.mouseData = (uint)(button << 16); d.U.mi.dwFlags = X_DOWN;
        INPUT u = new INPUT(); u.type = 0;
        u.U.mi.mouseData = (uint)(button << 16); u.U.mi.dwFlags = X_UP;
        Send(new INPUT[] { d });
        System.Threading.Thread.Sleep(60);
        Send(new INPUT[] { u });
    }
    public static void LeftClick(int x, int y) {
        MoveTo(x, y); System.Threading.Thread.Sleep(80);
        INPUT d = new INPUT(); d.type = 0; d.U.mi.dwFlags = MOUSE_DOWN;
        INPUT u = new INPUT(); u.type = 0; u.U.mi.dwFlags = MOUSE_UP;
        Send(new INPUT[] { d }); System.Threading.Thread.Sleep(50);
        Send(new INPUT[] { u });
    }
    public static IntPtr FindWindow(int pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p != pid || !IsWindowVisible(h)) return true;
            StringBuilder t = new StringBuilder(512); GetWindowTextW(h, t, t.Capacity);
            if (t.ToString().Length == 0) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
    public static int CountWindows(int pid) {
        int n = 0;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p != pid || !IsWindowVisible(h)) return true;
            StringBuilder t = new StringBuilder(512); GetWindowTextW(h, t, t.Capacity);
            if (t.ToString().Length == 0) return true;
            n++; return true;
        }, IntPtr.Zero);
        return n;
    }
    public static void Focus(IntPtr h) { ShowWindow(h, 5); SetForegroundWindow(h); }
}
"@ -Language CSharp

[void][Hk]::SetProcessDPIAware()
$logPath = Join-Path $env:APPDATA ("TranslationApp\logs\app-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))
$before = (Get-Content $logPath -Encoding UTF8).Count

$app = Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { Write-Output 'FAIL: app not running'; exit 1 }
Write-Output ("APP PID " + $app.Id)

$charmap = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\charmap.exe') -PassThru
Start-Sleep -Seconds 2
$cw = [Hk]::FindWindow($charmap.Id)
if ($cw -ne [IntPtr]::Zero) { [Hk]::Focus($cw) }
Start-Sleep -Milliseconds 600

Write-Output '--- gesture 1: double-tap left ALT (vk 0x12) ---'
[Hk]::Key(0x12, $false)
Start-Sleep -Milliseconds 40
[Hk]::Key(0x12, $true)
Start-Sleep -Milliseconds $GapMs
[Hk]::Key(0x12, $false)
Start-Sleep -Milliseconds 40
[Hk]::Key(0x12, $true)
Start-Sleep -Seconds $WaitSeconds
Write-Output ("  app visible windows now = " + [Hk]::CountWindows($app.Id))

Write-Output '--- gesture 2: plain LEFT click (mouse hook sanity) ---'
[Hk]::LeftClick(700, 400)
Start-Sleep -Seconds 2

Write-Output '--- gesture 3: mouse X1 up ---'
[Hk]::SideButton(1)
Start-Sleep -Seconds $WaitSeconds
Write-Output ("  app visible windows now = " + [Hk]::CountWindows($app.Id))

$new = Get-Content $logPath -Encoding UTF8 | Select-Object -Skip $before
Write-Output '--- log lines produced by the gestures ---'
foreach ($line in $new) { Write-Output ('  ' + $line.Substring(0, [Math]::Min(120, $line.Length))) }

# leave nothing behind
$ws = New-Object -ComObject WScript.Shell
$ws.SendKeys('{ESC}')
Start-Sleep -Milliseconds 400
Stop-Process -Id $charmap.Id -Force -ErrorAction SilentlyContinue
Write-Output 'DONE'
