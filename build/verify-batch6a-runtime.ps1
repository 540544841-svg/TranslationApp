# Batch 6a runtime verification (ascii-only source; PS 5.1 cannot parse UTF-8-without-BOM Chinese).
#
# Phase A: settings pages - history shown as session groups (FR-057, with a collapse click)
#          and the dictionary card's new "AI dictionary" row.
# Phase B: the quick window's dictionary card filled by the local OpenAI-compatible stub
#          (FR-056), with the local mdx layer switched off so the card can only be AI's.
#
# settings.json, the stub endpoint and the probe history rows are restored in the finally block.
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

public static class B6Run {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
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
[void][B6Run]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$python = (Get-Command python.exe -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = "python" }

$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
$dbPath = Join-Path $env:APPDATA 'TranslationApp\history.db'
$guid = [Guid]::NewGuid().ToString('N')
$backup = Join-Path $env:TEMP ("translationapp-b6run-$guid.json")
$stubLog = Join-Path $env:TEMP ("translationapp-b6stub-$guid.jsonl")
$stubPy = Join-Path $PSScriptRoot 'fixtures\llm-stub.py'
$patchPy = Join-Path $PSScriptRoot 'fixtures\patch-b5-settings.py'
$clearPy = Join-Path $PSScriptRoot 'fixtures\clear-probe-rows.py'
# marker prefix shared with clear-probe-rows.py: the cleanup deletes History rows LIKE 'b5probe%'
$word = 'b5probe apple'

function Stop-App {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
}

$stub = $null
try {
    if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
    Stop-App

    # ---- Phase A: settings pages (grouped history + the dictionary card's new row) ----
    $pa = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
    Start-Sleep -Seconds 6
    $hw = [B6Run]::FindWindow($pa.Id)
    if ($hw -eq [IntPtr]::Zero) { Write-Output 'SETTINGS WINDOW NOT FOUND'; exit 1 }
    [B6Run]::Park($hw); Start-Sleep -Milliseconds 400
    [B6Run]::Park($hw); Start-Sleep -Milliseconds 300
    $rect = [B6Run]::GetRect($hw)
    $ox = $rect[0] - 14; $oy = $rect[1] - 14
    [B6Run]::BringToFront($hw); Start-Sleep -Milliseconds 200

    # nav rows, 38px pitch: general=134 hotkeys=172 translate=210 engines=248
    # history=286 vocab=324 glossary=362 advanced=400
    [B6Run]::ClickAt($ox + 95, $oy + 286); Start-Sleep -Milliseconds 600
    [B6Run]::ClickAt($ox + 95, $oy + 286); Start-Sleep -Milliseconds 900
    [B6Run]::BringToFront($hw); Start-Sleep -Milliseconds 250
    $f = Join-Path $OutDir 'batch6a-history-grouped-light.png'
    Write-Output "$f  $([B6Run]::Capture($hw, $f, 14))"

    # collapse the first session group to prove the toggle works
    [B6Run]::ClickAt($ox + $CollapseX, $oy + $CollapseY); Start-Sleep -Milliseconds 700
    [B6Run]::BringToFront($hw); Start-Sleep -Milliseconds 250
    $f = Join-Path $OutDir 'batch6a-history-collapsed-light.png'
    Write-Output "$f  $([B6Run]::Capture($hw, $f, 14))"

    [B6Run]::ClickAt($ox + 95, $oy + 400); Start-Sleep -Milliseconds 700
    [B6Run]::BringToFront($hw)
    [B6Run]::WheelDown($ox + 640, $oy + 430, 16); Start-Sleep -Milliseconds 500
    [B6Run]::BringToFront($hw); Start-Sleep -Milliseconds 250
    $f = Join-Path $OutDir 'batch6a-settings-dictionary-card-light.png'
    Write-Output "$f  $([B6Run]::Capture($hw, $f, 14))"
    Stop-App

    # ---- Phase B: AI dictionary card, answered by the local stub ----
    $entropy = [Text.Encoding]::UTF8.GetBytes('TranslationApp.SecretStore.v1')
    $cipher = [Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('sk-verify-dummy'), $entropy, 'CurrentUser')
    $env:B5_SETTINGS = $settingsPath
    $env:B5_KEY = 'enc:' + [Convert]::ToBase64String($cipher)
    $env:B5_PORT = "$Port"
    $env:B6_AI_DICT = '1'
    & $python $patchPy
    Write-Output "PATCH EXIT $LASTEXITCODE"

    $stub = Start-Process -FilePath $python -ArgumentList @($stubPy, "$Port", $stubLog) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2

    $pb = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 7
    $ws = New-Object -ComObject WScript.Shell

    [B6Run]::MoveCursor(700, 320); Start-Sleep -Milliseconds 200
    $ws.SendKeys('%d'); Start-Sleep -Milliseconds 1600
    $qw = [B6Run]::FindWindow($pb.Id)
    if ($qw -eq [IntPtr]::Zero) { Write-Output 'QUICK WINDOW NOT FOUND'; exit 1 }
    [B6Run]::Focus($qw); Start-Sleep -Milliseconds 400
    # click into the input box first: keyboard focus is not guaranteed just because the
    # window came up, and a paste that goes nowhere silently does nothing
    $r = [B6Run]::GetRect($qw)
    [B6Run]::ClickAt($r[0] + 200, $r[1] + 110); Start-Sleep -Milliseconds 400
    # clipboard, not raw keystrokes: the active CJK IME would compose them
    Set-Clipboard -Value $word
    $ws.SendKeys('^a'); Start-Sleep -Milliseconds 200
    $ws.SendKeys('^v'); Start-Sleep -Milliseconds 400
    # Enter as well: paste-translate may or may not have fired depending on whether the
    # box was empty, and Enter on an already-translated box is a harmless re-run
    $ws.SendKeys('{ENTER}'); Start-Sleep -Seconds 5
    [B6Run]::BringToFront($qw); Start-Sleep -Milliseconds 200
    $f = Join-Path $OutDir 'batch6a-ai-dictionary-card-light.png'
    Write-Output "$f  $([B6Run]::Capture($qw, $f, 10))"
    Write-Output ("RECT " + ([B6Run]::GetRect($qw) -join ','))

    Write-Output '----- STUB LOG -----'
    if (Test-Path $stubLog) {
        & $python (Join-Path $PSScriptRoot 'fixtures\dump-stub-log.py') $stubLog
    } else { Write-Output 'NO STUB LOG' }
}
finally {
    Stop-App
    if ($stub -and -not $stub.HasExited) { Stop-Process -Id $stub.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path $backup) { Copy-Item $backup $settingsPath -Force; Remove-Item $backup -Force }
    if (Test-Path $dbPath) { & $python $clearPy $dbPath }
    Write-Output 'RESTORED'
}
