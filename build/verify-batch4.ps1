# Batch 4 UI verification: capture the NEW cards (local API / batch OCR / dictionary on the
# advanced page). Same harness pattern as verify-batch3.ps1 (park, click nav, wheel, screenshot).
# ASCII-only source. Backs up and restores the user's settings.json.
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [string]$OutDir = "$PSScriptRoot\..\artifacts\ui"
)

$ErrorActionPreference = 'Continue'

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class B4Shot {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder t, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

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
    public static void Park(IntPtr hWnd) { SetWindowPos(hWnd, IntPtr.Zero, 80, 50, 1120, 860, 0x0004 | 0x0010); }
    public static void Focus(IntPtr hWnd) { SetForegroundWindow(hWnd); }
    public static void BringToFront(IntPtr hWnd) {
        SetWindowPos(hWnd, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
        SetWindowPos(hWnd, new IntPtr(-2), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
    }
    public static string CaptureWindow(IntPtr hWnd, string path, int margin) {
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

public static class B4Typer {
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    private static void Send(INPUT i) { INPUT[] a = { i }; SendInput(1, a, Marshal.SizeOf(typeof(INPUT))); }
    private static void MoveTo(int x, int y) {
        INPUT m = new INPUT(); m.type = 0;
        m.U.mi.dx = (int)Math.Round(x * 65535.0 / (GetSystemMetrics(0) - 1));
        m.U.mi.dy = (int)Math.Round(y * 65535.0 / (GetSystemMetrics(1) - 1));
        m.U.mi.dwFlags = 0x0001 | 0x8000;
        Send(m);
    }
    public static void ClickAt(int x, int y) {
        MoveTo(x, y); System.Threading.Thread.Sleep(60);
        INPUT d = new INPUT(); d.type = 0; d.U.mi.dwFlags = 0x0002; Send(d);
        System.Threading.Thread.Sleep(40);
        INPUT u = new INPUT(); u.type = 0; u.U.mi.dwFlags = 0x0004; Send(u);
    }
    public static void WheelDown(int x, int y, int notches) {
        MoveTo(x, y); System.Threading.Thread.Sleep(100);
        for (int i = 0; i < notches; i++) {
            INPUT w = new INPUT(); w.type = 0; w.U.mi.mouseData = 4294967176; w.U.mi.dwFlags = 0x0800;
            Send(w); System.Threading.Thread.Sleep(40);
        }
    }
}
"@

if (-not (Test-Path $Exe)) { Write-Output "EXE not found: $Exe"; exit 1 }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
$backup = Join-Path $env:TEMP ("translationapp-batch4-settings-" + [Guid]::NewGuid().ToString('N') + '.json')

function Stop-App { Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 900 }

function Capture-At([int]$navY, [int]$wheelTurns, [string]$name) {
    Stop-App
    $p = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
    Start-Sleep -Seconds 4
    $hw = [B4Shot]::FindWindow($p.Id)
    if ($hw -eq [IntPtr]::Zero) { Write-Output "${name}: NOT FOUND"; return }
    [B4Shot]::Focus($hw) | Out-Null; Start-Sleep -Milliseconds 600
    [B4Shot]::Park($hw); Start-Sleep -Milliseconds 500
    [B4Shot]::Park($hw); Start-Sleep -Milliseconds 300
    $rect = [B4Shot]::GetRect($hw)
    $ox = $rect[0] - 14; $oy = $rect[1] - 14
    [B4Shot]::BringToFront($hw); Start-Sleep -Milliseconds 200
    [B4Typer]::ClickAt($ox + 95, $oy + $navY); Start-Sleep -Milliseconds 700
    if ($wheelTurns -gt 0) {
        [B4Shot]::BringToFront($hw)
        [B4Typer]::WheelDown($ox + 640, $oy + 430, $wheelTurns); Start-Sleep -Milliseconds 500
    }
    [B4Shot]::BringToFront($hw); Start-Sleep -Milliseconds 200
    $file = Join-Path $OutDir "$name.png"
    Write-Output "$file  $([B4Shot]::CaptureWindow($hw, $file, 14))"
}

try {
    if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
    # nav rows read from batch4-nav-probe.png: general=134 ... advanced=396 (38px pitch)
    Capture-At -navY 396 -wheelTurns 0 -name 'batch4-advanced-top-light'
    Capture-At -navY 396 -wheelTurns 10 -name 'batch4-advanced-mid-light'
    Capture-At -navY 396 -wheelTurns 18 -name 'batch4-advanced-bottom-light'
} finally {
    Stop-App
    if ((Test-Path $backup) -and (Test-Path $settingsPath)) { Copy-Item $backup $settingsPath -Force; Remove-Item $backup -Force }
    Write-Output 'DONE'
}
