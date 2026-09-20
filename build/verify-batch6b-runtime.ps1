# Batch 6b runtime verification (ascii-only source; PS 5.1 cannot parse UTF-8-without-BOM Chinese).
#
# Phase A: the first-run onboarding card (FR-059) - shown by the real startup path
#          (OnboardingShown forced to false), then dismissed with Esc.
# Phase B: per-app language pairs (FR-058) - Notepad is made the foreground app, a rule
#          "notepad: auto -> ru" is pre-seeded, and Alt+D must open the quick window
#          already set to Russian for THIS session only.
#
# settings.json is restored in the finally block; the scratch Notepad is killed there too.
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [string]$OutDir = "$PSScriptRoot\..\artifacts\ui",
    [int]$Port = 8124,
    [int]$CollapseX = 1000,
    [int]$CollapseY = 150
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Security

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class B6b {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder t, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

    private static void Send(INPUT i) { INPUT[] a = { i }; SendInput(1, a, Marshal.SizeOf(typeof(INPUT))); }
    private static void MoveTo(int x, int y) {
        INPUT m = new INPUT(); m.type = 0;
        m.U.mi.dx = (int)Math.Round(x * 65535.0 / (GetSystemMetrics(0) - 1));
        m.U.mi.dy = (int)Math.Round(y * 65535.0 / (GetSystemMetrics(1) - 1));
        m.U.mi.dwFlags = 0x0001 | 0x8000;
        Send(m);
    }
    public static void ClickAt(int x, int y) {
        MoveTo(x, y); System.Threading.Thread.Sleep(80);
        INPUT d = new INPUT(); d.type = 0; d.U.mi.dwFlags = 0x0002; Send(d);
        System.Threading.Thread.Sleep(50);
        INPUT u = new INPUT(); u.type = 0; u.U.mi.dwFlags = 0x0004; Send(u);
    }
    public static void WheelDown(int x, int y, int notches) {
        MoveTo(x, y); System.Threading.Thread.Sleep(100);
        for (int i = 0; i < notches; i++) {
            INPUT w = new INPUT(); w.type = 0; w.U.mi.mouseData = 4294967176; w.U.mi.dwFlags = 0x0800;
            Send(w); System.Threading.Thread.Sleep(40);
        }
    }
    public static IntPtr FindWindowByTitle(int pid, string needle) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p != pid || !IsWindowVisible(h)) return true;
            StringBuilder t = new StringBuilder(512); GetWindowTextW(h, t, t.Capacity);
            if (t.ToString().IndexOf(needle, StringComparison.Ordinal) < 0) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
    public static bool FocusByPid(int pid) {
        IntPtr found = FindWindow(pid);
        if (found == IntPtr.Zero) return false;
        ShowWindow(found, 5);
        SetForegroundWindow(found);
        return true;
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
    public static int[] GetRect(IntPtr hWnd) { RECT r; GetWindowRect(hWnd, out r); return new int[] { r.Left, r.Top, r.Right, r.Bottom }; }
    public static void Focus(IntPtr hWnd) { SetForegroundWindow(hWnd); }
    public static void MoveCursor(int x, int y) { MoveTo(x, y); }
    public static void Park(IntPtr hWnd) { SetWindowPos(hWnd, IntPtr.Zero, 80, 50, 1120, 860, 0x0004 | 0x0010); }
    public static void BringToFront(IntPtr hWnd) {
        SetWindowPos(hWnd, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
        SetWindowPos(hWnd, new IntPtr(-2), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
    }
    public static string Capture(IntPtr hWnd, string path, int margin) {
        RECT r;
        if (!GetWindowRect(hWnd, out r)) return "fail";
        int w = (r.Right - r.Left) + margin * 2, h = (r.Bottom - r.Top) + margin * 2;
        using (Bitmap bmp = new Bitmap(w, h)) {
            using (Graphics g = Graphics.FromImage(bmp)) { g.CopyFromScreen(Math.Max(0, r.Left - margin), Math.Max(0, r.Top - margin), 0, 0, new Size(w, h)); }
            bmp.Save(path, ImageFormat.Png);
        }
        return w + "x" + h;
    }
}
"@
if (-not (Test-Path $Exe)) { Write-Output "EXE not found: $Exe"; exit 1 }
[void][B6b]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$python = (Get-Command python.exe -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = "python" }

$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
$guid = [Guid]::NewGuid().ToString('N')
$backup = Join-Path $env:TEMP ("translationapp-b6brun-$guid.json")
$patchPy = Join-Path $PSScriptRoot 'fixtures\patch-b6-settings.py'

function Stop-App {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
}

function Stop-ProbeApp { Get-Process -Name charmap -ErrorAction SilentlyContinue | Stop-Process -Force }

$notepad = $null
try {
    if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
    Stop-App

    $env:B6_SETTINGS = $settingsPath
    & $python $patchPy
    Write-Output "PATCH EXIT $LASTEXITCODE"

    # ---- Phase A: first-run onboarding card (real startup path) ----
    $pa = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 5
    $ow = [B6b]::FindWindow($pa.Id)
    if ($ow -eq [IntPtr]::Zero) { Write-Output 'ONBOARDING WINDOW NOT FOUND'; exit 1 }
    [B6b]::BringToFront($ow); Start-Sleep -Milliseconds 300
    $f = Join-Path $OutDir 'batch6b-onboarding-card-light.png'
    Write-Output "$f  $([B6b]::Capture($ow, $f, 10))"
    Write-Output ("ONBOARD RECT " + ([B6b]::GetRect($ow) -join ','))
    [B6b]::Focus($ow); Start-Sleep -Milliseconds 200
    $ws = New-Object -ComObject WScript.Shell
    $ws.SendKeys('{ESC}'); Start-Sleep -Milliseconds 800

    # ---- Phase B: per-app language pair, applied to this session only ----
    Stop-App
    # charmap: a classic Win32 app whose window belongs to the pid we started
    # (Win11's notepad.exe is a launcher stub that hands off to another pid)
    $notepad = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\charmap.exe') -PassThru
    Start-Sleep -Seconds 2
    $pb = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 6
    # Notepad must own the foreground, otherwise the rule under test never gets a chance
    [B6b]::FocusByPid($notepad.Id); Start-Sleep -Milliseconds 600
    $ws.SendKeys('%d'); Start-Sleep -Milliseconds 1800
    $qw = [B6b]::FindWindow($pb.Id)
    if ($qw -eq [IntPtr]::Zero) { Write-Output 'QUICK WINDOW NOT FOUND'; exit 1 }
    [B6b]::BringToFront($qw); Start-Sleep -Milliseconds 300
    $f = Join-Path $OutDir 'batch6b-app-language-rule-light.png'
    Write-Output "$f  $([B6b]::Capture($qw, $f, 10))"
    Write-Output ("QUICK RECT " + ([B6b]::GetRect($qw) -join ','))
}
finally {
    Stop-App
    if ($notepad -and -not $notepad.HasExited) { Stop-Process -Id $notepad.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path $backup) { Copy-Item $backup $settingsPath -Force; Remove-Item $backup -Force }
    Write-Output 'RESTORED'
}
