# Batch 6c: verify the quick window no longer jumps when its content resizes.
#
# Repro of the user report: summon the window, park the mouse over the 「对照」 button, click it
# (content height changes -> Reposition() runs). Before the fix Reposition re-read the live
# cursor position, so the window re-anchored under the mouse and visibly slid. Now the anchor is
# captured once per summon, so the rect must be unchanged.
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [string]$Click = '318,145',
    [string]$OutDir = "$PSScriptRoot\..\build\probe"
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class St {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr extra; }
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

    private static void Send(INPUT[] items) { SendInput((uint)items.Length, items, Marshal.SizeOf(typeof(INPUT))); }
    public static void MoveTo(int x, int y) {
        INPUT m = new INPUT(); m.type = 0;
        m.U.mi.dx = (int)Math.Round(x * 65535.0 / (GetSystemMetrics(0) - 1));
        m.U.mi.dy = (int)Math.Round(y * 65535.0 / (GetSystemMetrics(1) - 1));
        m.U.mi.dwFlags = 0x0001 | 0x8000;
        Send(new INPUT[] { m });
    }
    /// <summary>把指针停在 (x,y) 并按下抬起一次——窗口若跟着光标重锚定，这里就能测出来。</summary>
    public static void HoverClick(int x, int y) {
        MoveTo(x, y); System.Threading.Thread.Sleep(150);
        INPUT d = new INPUT(); d.type = 0; d.U.mi.dwFlags = 0x0002;
        INPUT u = new INPUT(); u.type = 0; u.U.mi.dwFlags = 0x0004;
        Send(new INPUT[] { d }); System.Threading.Thread.Sleep(60);
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
    public static int[] Rect(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }
    public static void Focus(IntPtr h) { ShowWindow(h, 5); SetForegroundWindow(h); }
}
"@ -Language CSharp

if (-not (Test-Path $Exe)) { Write-Output "EXE not found: $Exe"; exit 1 }
$Exe = (Resolve-Path $Exe).Path
[void][St]::SetProcessDPIAware()

$text = 'We were moving mountains long before we knew we could. There can be miracles when you believe. This third sentence is long enough to make the window grow.'

$charmap = $null
try {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
    $app = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 6

    $charmap = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\charmap.exe') -PassThru
    Start-Sleep -Seconds 2
    $cw = [St]::FindWindow($charmap.Id)
    if ($cw -ne [IntPtr]::Zero) { [St]::Focus($cw) }
    Start-Sleep -Milliseconds 500

    $ws = New-Object -ComObject WScript.Shell
    $ws.SendKeys('%d')
    Start-Sleep -Milliseconds 1500
    $qw = [St]::FindWindow($app.Id)
    if ($qw -eq [IntPtr]::Zero) { Write-Output 'FAIL: quick window not found'; exit 1 }

    [St]::Focus($qw)
    Start-Sleep -Milliseconds 250
    Set-Clipboard $text
    $ws.SendKeys('^v{ENTER}')
    Start-Sleep -Seconds 4

    $before = [St]::Rect($qw)
    Write-Output ("BEFORE " + ($before -join ','))

    # hover exactly over the button and click: this is the case that used to drag the window
    $c = $Click -split ','
    [St]::HoverClick(($before[0] + [int]$c[0]), ($before[1] + [int]$c[1]))
    Start-Sleep -Seconds 2
    $after = [St]::Rect($qw)
    Write-Output ("AFTER  " + ($after -join ','))

    $dx = [Math]::Abs($after[0] - $before[0]); $dy = [Math]::Abs($after[1] - $before[1])
    Write-Output ("MOVED dx={0} dy={1} => {2}" -f $dx, $dy, $(if ($dx -le 1 -and $dy -le 1) { 'STABLE' } else { 'JUMPED' }))

    # and a second interaction further down the same session
    [St]::HoverClick(($after[0] + 392), ($after[1] + 145))
    Start-Sleep -Seconds 2
    $third = [St]::Rect($qw)
    Write-Output ("THIRD  " + ($third -join ','))
    $dx2 = [Math]::Abs($third[0] - $before[0]); $dy2 = [Math]::Abs($third[1] - $before[1])
    Write-Output ("MOVED2 dx={0} dy={1} => {2}" -f $dx2, $dy2, $(if ($dx2 -le 1 -and $dy2 -le 1) { 'STABLE' } else { 'JUMPED' }))
}
finally {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($charmap -and -not $charmap.HasExited) { Stop-Process -Id $charmap.Id -Force -ErrorAction SilentlyContinue }
    Write-Output 'DONE'
}
