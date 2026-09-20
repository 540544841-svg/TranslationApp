# Batch 2 (FR-035/036/037) UI verification: captures the NEW settings cards that sit
# below the fold of build\verify-ui.ps1 pages. ASCII only (PS 5.1 misparses UTF-8 without BOM).
# Usage: powershell -ExecutionPolicy Bypass -File build\verify-batch2.ps1 [-Exe <path>] [-OutDir <dir>]
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

public static class B2Shot {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    public static IntPtr FindWindow(int pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            uint wpid; GetWindowThreadProcessId(hWnd, out wpid);
            if ((int)wpid != pid) return true;
            if (!IsWindowVisible(hWnd)) return true;
            StringBuilder t = new StringBuilder(512); GetWindowTextW(hWnd, t, t.Capacity);
            if (t.ToString().Length == 0) return true;
            found = hWnd; return false;
        }, IntPtr.Zero);
        return found;
    }

    public static int[] GetRect(IntPtr hWnd) {
        RECT r; GetWindowRect(hWnd, out r);
        return new int[] { r.Left, r.Top, r.Right, r.Bottom };
    }

    public static void BringToFront(IntPtr hWnd) {
        SetWindowPos(hWnd, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
        SetWindowPos(hWnd, new IntPtr(-2), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
    }

    public static void Focus(IntPtr hWnd) { SetForegroundWindow(hWnd); }

    public static void SetBounds(IntPtr hWnd, int x, int y, int w, int h) {
        SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, 0x0004 | 0x0010);
    }

    public static string CaptureWindow(IntPtr hWnd, string path, int margin) {
        RECT r;
        if (!GetWindowRect(hWnd, out r)) return "GetWindowRect failed";
        int w = (r.Right - r.Left) + margin * 2;
        int h = (r.Bottom - r.Top) + margin * 2;
        int sx = Math.Max(0, r.Left - margin);
        int sy = Math.Max(0, r.Top - margin);
        using (Bitmap bmp = new Bitmap(w, h)) {
            using (Graphics g = Graphics.FromImage(bmp)) { g.CopyFromScreen(sx, sy, 0, 0, new Size(w, h)); }
            bmp.Save(path, ImageFormat.Png);
        }
        return w + "x" + h;
    }
}

public static class B2Typer {
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    private static void Send(INPUT i) { INPUT[] a = new INPUT[] { i }; SendInput(1, a, Marshal.SizeOf(typeof(INPUT))); }
    private static void MoveTo(int x, int y) {
        int sw = GetSystemMetrics(0); int sh = GetSystemMetrics(1);
        INPUT move = new INPUT(); move.type = 0;
        move.U.mi.dx = (int)Math.Round(x * 65535.0 / (sw - 1));
        move.U.mi.dy = (int)Math.Round(y * 65535.0 / (sh - 1));
        move.U.mi.dwFlags = 0x0001 | 0x8000;
        Send(move);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    public static void ClickAt(int x, int y) {
        MoveTo(x, y);
        System.Threading.Thread.Sleep(60);
        INPUT down = new INPUT(); down.type = 0; down.U.mi.dwFlags = 0x0002; Send(down);
        System.Threading.Thread.Sleep(40);
        INPUT up = new INPUT(); up.type = 0; up.U.mi.dwFlags = 0x0004; Send(up);
    }

    public static void Wheel(int x, int y, int notches) {
        MoveTo(x, y);
        System.Threading.Thread.Sleep(100);
        for (int i = 0; i < Math.Abs(notches); i++) {
            INPUT w = new INPUT(); w.type = 0;
            w.U.mi.mouseData = (uint)(notches < 0 ? -120 : 120);
            w.U.mi.dwFlags = 0x0800;
            Send(w);
            System.Threading.Thread.Sleep(40);
        }
    }
}
"@

if (-not (Test-Path $Exe)) { Write-Output "EXE not found: $Exe"; exit 1 }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'

function Set-Setting([string]$name, $value) {
    if (Test-Path $settingsPath) {
        $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path $settingsPath) | Out-Null
        $json = [pscustomobject]@{}
    }
    if ($json.PSObject.Properties.Name -contains $name) { $json.$name = $value }
    else { $json | Add-Member -NotePropertyName $name -NotePropertyValue $value }
    $json | ConvertTo-Json -Depth 6 | Set-Content $settingsPath -Encoding UTF8
}

function Stop-App {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
}

# Nav rows: same geometry as verify-ui.ps1 (sidebar 176px wide, first item center y=134, step 38).
$navY = @{ general = 134; hotkeys = 172; translate = 210; engine = 248; history = 286; vocabulary = 324; glossary = 362; advanced = 400 }

function Capture-Page([string]$theme, [string]$page, [switch]$ScrollBottom, [int]$WheelTurns = 22) {
    Stop-App
    Set-Setting 'Theme' $theme
    $p = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
    Start-Sleep -Seconds 4
    $hw = [B2Shot]::FindWindow($p.Id)
    if ($hw -eq [IntPtr]::Zero) { Write-Output "${page}-${theme}: NOT FOUND"; return }
    [B2Shot]::Focus($hw) | Out-Null
    Start-Sleep -Milliseconds 600
    # Park the window at a known on-screen rect (it may restore at the right edge and get clipped).
    # Apply twice: the first call can race WPF's own restore-on-show sizing.
    [B2Shot]::SetBounds($hw, 80, 50, 1120, 860)
    Start-Sleep -Milliseconds 500
    [B2Shot]::SetBounds($hw, 80, 50, 1120, 860)
    Start-Sleep -Milliseconds 300
    $rect = [B2Shot]::GetRect($hw)
    $originX = $rect[0] - 14
    $originY = $rect[1] - 14

    [B2Shot]::BringToFront($hw); Start-Sleep -Milliseconds 200
    [B2Typer]::ClickAt([int]($originX + 95), [int]($originY + $navY[$page]))
    Start-Sleep -Milliseconds 700

    if ($ScrollBottom) {
        # wheel DOWN (negative notches) over the content column to reach the batch-2 cards at the
        # bottom; re-assert topmost first so SendInput delivers the wheel to our window
        [B2Shot]::BringToFront($hw)
        [B2Typer]::Wheel([int]($originX + 640), [int]($originY + 430), -$WheelTurns)
        Start-Sleep -Milliseconds 500
    }

    [B2Shot]::BringToFront($hw); Start-Sleep -Milliseconds 200
    $suffix = if ($ScrollBottom) { '-bottom' } else { '' }
    $file = Join-Path $OutDir ("batch2-$page$suffix-$theme.png")
    $size = [B2Shot]::CaptureWindow($hw, $file, 14)
    Write-Output "$file  $size"
}

Capture-Page -theme 'light' -page 'general' -ScrollBottom
Capture-Page -theme 'light' -page 'vocabulary'
Capture-Page -theme 'light' -page 'glossary'
Capture-Page -theme 'light' -page 'hotkeys' -ScrollBottom
Capture-Page -theme 'dark'  -page 'general' -ScrollBottom

Stop-App
Write-Output 'DONE'
