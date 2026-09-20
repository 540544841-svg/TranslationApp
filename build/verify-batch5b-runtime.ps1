# Batch 5b runtime verification (ascii-only source; PS 5.1 cannot parse UTF-8-without-BOM Chinese).
#
# Proves FR-052 (daily review line in the quick window) and FR-053 (shadow reading:
# per-sentence list + current-line highlight + stop) really render, using the same
# local OpenAI-compatible stub as verify-batch5-runtime.ps1 so translations are
# deterministic and offline.
#
# Click offsets are parameters: run once with -Probe only, read the screenshot, then
# re-run with the measured coordinates. settings.json, the stub endpoint and the probe
# vocabulary rows are all restored in the finally block.
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [string]$OutDir = "$PSScriptRoot\..\artifacts\ui",
    [int]$Port = 8124,
    [int]$NextX = -1,
    [int]$NextY = -1,
    [int]$ShadowX = -1,
    [int]$ShadowY = -1,
    [switch]$Probe,
    [switch]$SettingsOnly
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

public static class B5b {
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
[void][B5b]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$python = (Get-Command python.exe -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = "python" }

$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
$dbPath = Join-Path $env:APPDATA 'TranslationApp\history.db'
$guid = [Guid]::NewGuid().ToString('N')
$backup = Join-Path $env:TEMP ("translationapp-b5brun-$guid.json")
$stubLog = Join-Path $env:TEMP ("translationapp-b5bstub-$guid.jsonl")
$stubPy = Join-Path $PSScriptRoot 'fixtures\llm-stub.py'
$patchPy = Join-Path $PSScriptRoot 'fixtures\patch-b5-settings.py'
$clearPy = Join-Path $PSScriptRoot 'fixtures\clear-probe-rows.py'
$seedPy = Join-Path $PSScriptRoot 'fixtures\seed-probe-vocab.py'
$sentence = 'b5probe shadow reading sentence one. This is the second one! And a third?'

function Stop-App {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
}

$stub = $null
try {
    if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
    Stop-App

    # Settings-page smoke test: a bad StaticResource key inside one card silently kills the
    # whole tab, so visit the two pages this batch added cards to and shoot them.
    $ps = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
    Start-Sleep -Seconds 6
    $hw = [B5b]::FindWindow($ps.Id)
    if ($hw -eq [IntPtr]::Zero) { Write-Output 'SETTINGS WINDOW NOT FOUND'; exit 1 }
    # park it over empty desktop: the window opens under the IDE and CopyFromScreen would
    # otherwise shoot whatever is on top
    [B5b]::Park($hw); Start-Sleep -Milliseconds 500
    [B5b]::Park($hw); Start-Sleep -Milliseconds 300
    $rect = [B5b]::GetRect($hw)
    $ox = $rect[0] - 14; $oy = $rect[1] - 14
    [B5b]::Focus($hw); Start-Sleep -Milliseconds 400
    foreach ($nav in @( @(324, 'vocab'), @(396, 'advanced') )) {
        # raise first: the first click on an inactive window only activates it (WPF swallows it)
        [B5b]::BringToFront($hw); Start-Sleep -Milliseconds 250
        [B5b]::ClickAt($ox + 95, $oy + $nav[0]); Start-Sleep -Milliseconds 500
        [B5b]::ClickAt($ox + 95, $oy + $nav[0]); Start-Sleep -Milliseconds 900
        [B5b]::BringToFront($hw); Start-Sleep -Milliseconds 250
        $f = Join-Path $OutDir ('batch5b-settings-' + $nav[1] + '-top-light.png')
        Write-Output "$f  $([B5b]::Capture($hw, $f, 14))"
    }
    # scroll the advanced page down to the speech card (shadow reading row)
    [B5b]::BringToFront($hw); Start-Sleep -Milliseconds 200
    [B5b]::ClickAt($ox + 95, $oy + 396); Start-Sleep -Milliseconds 700
    $rect = [B5b]::GetRect($hw); $ox = $rect[0] - 14; $oy = $rect[1] - 14
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Wheel {
  [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
  [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; }
  [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] p, int cb);
  [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
  private static void MoveTo(int x, int y) {
    INPUT m = new INPUT(); m.type = 0;
    m.U.mi.dx = (int)Math.Round(x * 65535.0 / (GetSystemMetrics(0) - 1));
    m.U.mi.dy = (int)Math.Round(y * 65535.0 / (GetSystemMetrics(1) - 1));
    m.U.mi.dwFlags = 0x0001 | 0x8000; Send(m);
  }
  private static void Send(INPUT i) { INPUT[] a = { i }; SendInput(1, a, Marshal.SizeOf(typeof(INPUT))); }
  public static void Down(int x, int y, int notches) {
    MoveTo(x, y); System.Threading.Thread.Sleep(80);
    for (int i = 0; i < notches; i++) {
      INPUT w = new INPUT(); w.type = 0; w.U.mi.mouseData = 4294967176; w.U.mi.dwFlags = 0x0800;
      Send(w); System.Threading.Thread.Sleep(40);
    }
  }
}
"@
    [Wheel]::Down($ox + 640, $oy + 430, 14)
    Start-Sleep -Milliseconds 500
    [B5b]::BringToFront($hw); Start-Sleep -Milliseconds 250
    $f = Join-Path $OutDir 'batch5b-settings-advanced-speech-light.png'
    Write-Output "$f  $([B5b]::Capture($hw, $f, 14))"
    Stop-App

    if ($SettingsOnly) { Write-Output 'SETTINGS ONLY, done'; return }

    # vocabulary rows for the review line (probe-prefixed, removed in finally)
    & $python $seedPy $dbPath

    $entropy = [Text.Encoding]::UTF8.GetBytes('TranslationApp.SecretStore.v1')
    $cipher = [Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('sk-verify-dummy'), $entropy, 'CurrentUser')
    $env:B5_SETTINGS = $settingsPath
    $env:B5_KEY = 'enc:' + [Convert]::ToBase64String($cipher)
    $env:B5_PORT = "$Port"
    $env:B5_BATCH5B = '1'
    & $python $patchPy

    if ($Probe) { Write-Output 'PROBE MODE: settings patched, nothing launched'; return }

    $stub = Start-Process -FilePath $python -ArgumentList @($stubPy, "$Port", $stubLog) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2

    $pb = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 7
    $ws = New-Object -ComObject WScript.Shell

    function Show-Quick([int]$procId) {
        $h = [B5b]::FindWindow($procId)
        if ($h -eq [IntPtr]::Zero) {
            # Alt+D opens the window at the cursor: park the cursor over empty desktop on
            # the primary monitor first, or the window lands under another app's window.
            [B5b]::MoveCursor(700, 320); Start-Sleep -Milliseconds 200
            $ws.SendKeys('%d'); Start-Sleep -Milliseconds 1500
            $h = [B5b]::FindWindow($procId)
        }
        return $h
    }

    # ---- FR-052: the review line must be there on the very first show of the day ----
    $qw = Show-Quick $pb.Id
    if ($qw -eq [IntPtr]::Zero) { Write-Output 'QUICK WINDOW NOT FOUND'; exit 1 }
    [B5b]::BringToFront($qw)
    [B5b]::Focus($qw); Start-Sleep -Milliseconds 500
    $f = Join-Path $OutDir 'batch5b-review-line-light.png'
    Write-Output "$f  $([B5b]::Capture($qw, $f, 10))"
    Write-Output ("RECT1 " + ([B5b]::GetRect($qw) -join ','))

    if ($NextX -ge 0 -and $NextY -ge 0) {
        $r = [B5b]::GetRect($qw)
        [B5b]::ClickAt($r[0] + $NextX, $r[1] + $NextY)
        Start-Sleep -Milliseconds 800
        [B5b]::BringToFront($qw); Start-Sleep -Milliseconds 200
        $f = Join-Path $OutDir 'batch5b-review-next-light.png'
        Write-Output "$f  $([B5b]::Capture($qw, $f, 10))"
    }

    # ---- FR-053: translate a 3-sentence text, then start / stop shadow reading ----
    Set-Clipboard -Value $sentence
    # the review click above left keyboard focus on a button: click back into the input
    # box first, otherwise Ctrl+V / Enter go to the button instead of the text
    $rIn = [B5b]::GetRect($qw)
    [B5b]::ClickAt($rIn[0] + 200, $rIn[1] + 110)
    Start-Sleep -Milliseconds 400
    $ws.SendKeys('^a'); Start-Sleep -Milliseconds 200
    $ws.SendKeys('^v'); Start-Sleep -Milliseconds 300
    $ws.SendKeys('{ENTER}')
    Start-Sleep -Seconds 4
    [B5b]::BringToFront($qw); Start-Sleep -Milliseconds 200
    $f = Join-Path $OutDir 'batch5b-result-light.png'
    Write-Output "$f  $([B5b]::Capture($qw, $f, 10))"
    Write-Output ("RECT2 " + ([B5b]::GetRect($qw) -join ','))

    if ($ShadowX -ge 0 -and $ShadowY -ge 0) {
        $r = [B5b]::GetRect($qw)
        [B5b]::ClickAt($r[0] + $ShadowX, $r[1] + $ShadowY)
        Start-Sleep -Milliseconds 1200
        [B5b]::BringToFront($qw); Start-Sleep -Milliseconds 200
        $f = Join-Path $OutDir 'batch5b-shadow-running-light.png'
        Write-Output "$f  $([B5b]::Capture($qw, $f, 10))"
        Write-Output ("RECT3 " + ([B5b]::GetRect($qw) -join ','))

        # stop: same button now reads "stop" and must return the window to block view.
        # Two attempts with a capture each: the first click can be eaten by the resize
        # animation that the auto-height just kicked off.
        foreach ($attempt in 1, 2) {
            $r3 = [B5b]::GetRect($qw)
            [B5b]::BringToFront($qw); Start-Sleep -Milliseconds 150
            [B5b]::ClickAt($r3[0] + $ShadowX, $r3[1] + $ShadowY)
            Start-Sleep -Milliseconds 900
            $f = Join-Path $OutDir ("batch5b-shadow-stopped-" + $attempt + "-light.png")
            Write-Output "$f  $([B5b]::Capture($qw, $f, 10))"
        }
        $f = Join-Path $OutDir 'batch5b-shadow-stopped-light.png'
        # Esc hides the window, which runs the same cancel path as the stop button; the
        # re-shown window must come back in block view (list cleared) for that to be true.
        [B5b]::Focus($qw); Start-Sleep -Milliseconds 200
        $ws.SendKeys('{ESC}'); Start-Sleep -Milliseconds 900
        $qw4 = Show-Quick $pb.Id
        if ($qw4 -ne [IntPtr]::Zero) {
            Start-Sleep -Milliseconds 600
            $f2 = Join-Path $OutDir 'batch5b-after-reshow-light.png'
            Write-Output "$f2  $([B5b]::Capture($qw4, $f2, 10))"
        }
    }

    Write-Output '----- STUB LOG -----'
    if (Test-Path $stubLog) {
        & $python (Join-Path $PSScriptRoot 'fixtures\dump-stub-log.py') $stubLog
    } else { Write-Output 'NO STUB LOG' }
}
finally {
    Stop-App
    if ($stub -and -not $stub.HasExited) { Stop-Process -Id $stub.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path $backup) { Copy-Item $backup $settingsPath -Force; Remove-Item $backup -Force }
    if (Test-Path $dbPath) {
        & $python $clearPy $dbPath
        & $python (Join-Path $PSScriptRoot 'fixtures\clear-probe-vocab.py') $dbPath
    }
    Write-Output 'RESTORED'
}
