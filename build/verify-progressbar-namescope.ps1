# Batch 6c runtime verification (ascii-only source; PS 5.1 cannot parse UTF-8-without-BOM Chinese).
#
# Proves the ProgressBar glow animation no longer throws the WPF namescope exception
# ("ResolveTargetName ... ControlTemplate") when a window is resized.
#   1. remembers where the day log currently ends
#   2. starts the app and opens the quick window through its own global hotkey (Alt+D)
#      with charmap holding the foreground, so the show path is the real one
#   3. forces real HWND size changes (WPF re-broadcasts Loaded from HwndTarget.OnResize -
#      that is exactly the call stack that used to throw)
#   4. pastes text and resizes again while a translation is in flight, then Esc
#   5. counts NEW namescope exceptions and new ERR lines in the log
# User state: settings.json is backed up and restored in finally, charmap is killed,
# nothing is written to history beyond what a normal Alt+D translation does.
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [string]$LogDir = "$env:APPDATA\TranslationApp\logs"
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class NsRun {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder t, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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
    public static string Title(IntPtr h) {
        StringBuilder t = new StringBuilder(512); GetWindowTextW(h, t, t.Capacity); return t.ToString();
    }
    public static int[] Rect(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }
    public static bool Resize(IntPtr h, int w, int hh) {
        return SetWindowPos(h, IntPtr.Zero, 0, 0, w, hh, 0x0002 | 0x0004 | 0x0010);
    }
    public static void Focus(IntPtr h) { ShowWindow(h, 5); SetForegroundWindow(h); }
}
"@ -Language CSharp

if (-not (Test-Path $Exe)) { Write-Output "EXE not found: $Exe"; exit 1 }
$Exe = (Resolve-Path $Exe).Path
[void][NsRun]::SetProcessDPIAware()

$logPath = Join-Path $env:APPDATA ("TranslationApp\logs\app-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))
$before = 0
if (Test-Path $logPath) { $before = (Get-Content $logPath -Encoding UTF8).Count }
Write-Output "LOG $logPath lines=$before"

$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
$backup = Join-Path $env:TEMP ("translationapp-ns-" + [Guid]::NewGuid().ToString('N') + '.json')
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }

$charmap = $null
try {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900

    $app = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 5

    $charmap = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\charmap.exe') -PassThru
    Start-Sleep -Seconds 2
    $cw = [NsRun]::FindWindow($charmap.Id)
    if ($cw -ne [IntPtr]::Zero) { [void][NsRun]::Focus($cw) }
    Start-Sleep -Milliseconds 500

    $ws = New-Object -ComObject WScript.Shell
    $ws.SendKeys('%d')
    Start-Sleep -Milliseconds 1800

    $qw = [NsRun]::FindWindow($app.Id)
    if ($qw -eq [IntPtr]::Zero) { Write-Output 'FAIL: quick window not found'; exit 1 }
    $r = [NsRun]::Rect($qw)
    Write-Output ("QUICK opened hwnd-title-chars=" + [NsRun]::Title($qw).Length + " rect=" + ($r -join ','))

    for ($i = 1; $i -le 10; $i++) {
        [void][NsRun]::Resize($qw, ($r[2] + $i * 9), ($r[3] + $i * 6))
        Start-Sleep -Milliseconds 220
    }
    Write-Output 'PHASE1 resized x10 (idle)'

    [void][NsRun]::Focus($qw)
    Start-Sleep -Milliseconds 300
    Set-Clipboard 'the quick brown fox jumps over the lazy dog'
    $ws.SendKeys('^v')
    Start-Sleep -Milliseconds 200
    $ws.SendKeys('{ENTER}')
    for ($i = 1; $i -le 10; $i++) {
        [void][NsRun]::Resize($qw, ($r[2] + 60 + $i * 9), ($r[3] + 30 + $i * 6))
        Start-Sleep -Milliseconds 220
    }
    Start-Sleep -Seconds 3
    Write-Output 'PHASE2 resized x10 while translating'

    $ws.SendKeys('{ESC}')
    Start-Sleep -Milliseconds 800
}
finally {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($charmap -and -not $charmap.HasExited) { Stop-Process -Id $charmap.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 700
    if (Test-Path $backup) { Copy-Item $backup $settingsPath -Force; Remove-Item $backup -Force }

    $all = Get-Content $logPath -Encoding UTF8
    $new = @($all | Select-Object -Skip $before)
    $namescope = @($new | Where-Object { $_ -match 'ResolveTargetName' }).Count
    $errlines = @($new | Where-Object { $_ -match ' ERR\] ' })
    Write-Output '=== RESULT ==='
    Write-Output ("namescope-exceptions={0} errors={1}" -f $namescope, $errlines.Count)
    foreach ($e in $errlines) { Write-Output ('  ' + $e.Substring(0, [Math]::Min(110, $e.Length))) }
    Write-Output 'SETTINGS RESTORED'
}
