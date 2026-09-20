# Batch 5 runtime verification (ascii-only source; PS 5.1 cannot parse UTF-8-without-BOM Chinese).
#
# Phase A: settings page renders the new "AI context" card and the "writing" profile
#          appears in the profiles list.
# Phase B: point the AI engine at a local stub (build\fixtures\llm-stub.py), drive the
#          quick window with two consecutive sentences + one "more formal" click, and
#          prove from the stub log that FR-050 (context) and FR-051 (style) really reach
#          the wire.
#
# Everything it touches (settings.json, history rows created by the test sentences) is
# restored in the finally block.
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [string]$OutDir = "$PSScriptRoot\..\artifacts\ui",
    [int]$Port = 8123,
    [int]$StyleX = 66,
    [int]$StyleY = -46
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

public static class B5Run {
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
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int n);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    public static int[] Cursor() { POINT p; GetCursorPos(out p); return new int[] { p.X, p.Y }; }
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
    public static void Key(ushort vk) {
        INPUT d = new INPUT(); d.type = 1; d.U.ki.wVk = vk; Send(d);
        INPUT u = new INPUT(); u.type = 1; u.U.ki.wVk = vk; u.U.ki.dwFlags = 0x0002; Send(u);
    }
    public static IntPtr FindWindow(int pid) {
        // First visible window of the process that owns a title. Matching on the title
        // itself is not done here: GetWindowText across processes can return partial text,
        // so instead the harness logs TitleCodes() and I confirm from the screenshot.
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
    public static string TitleCodes(IntPtr hWnd) {
        StringBuilder t = new StringBuilder(512); GetWindowTextW(hWnd, t, t.Capacity);
        StringBuilder codes = new StringBuilder();
        foreach (char c in t.ToString()) { if (codes.Length > 0) codes.Append(','); codes.Append((int)c); }
        return codes.ToString();
    }
    public static int[] GetRect(IntPtr hWnd) { RECT r; GetWindowRect(hWnd, out r); return new int[] { r.Left, r.Top, r.Right, r.Bottom }; }
    public static void Park(IntPtr hWnd) { SetWindowPos(hWnd, IntPtr.Zero, 80, 50, 1120, 860, 0x0004 | 0x0010); }
    public static void Focus(IntPtr hWnd) { SetForegroundWindow(hWnd); }
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
[void][B5Run]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$python = (Get-Command python.exe -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = "python" }

$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
$dbPath = Join-Path $env:APPDATA 'TranslationApp\history.db'
$guid = [Guid]::NewGuid().ToString('N')
$backup = Join-Path $env:TEMP ("translationapp-b5run-$guid.json")
$stubLog = Join-Path $env:TEMP ("translationapp-b5stub-$guid.jsonl")
$stubPy = Join-Path $PSScriptRoot 'fixtures\llm-stub.py'
$patchPy = Join-Path $PSScriptRoot 'fixtures\patch-b5-settings.py'
$marker1 = 'b5probe apple is red'
$marker2 = 'b5probe banana is yellow'
function Stop-App {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
}

$stub = $null
try {
    if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
    Stop-App

    # ---------- Phase A: settings page (user's own settings, nothing patched yet) ----------
    $pa = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
    Start-Sleep -Seconds 6
    $hw = [B5Run]::FindWindow($pa.Id)
    if ($hw -eq [IntPtr]::Zero) { Write-Output 'SETTINGS WINDOW NOT FOUND'; exit 1 }
    Write-Output ('SETTINGS TITLE CODES ' + [B5Run]::TitleCodes($hw))
    [B5Run]::Park($hw); Start-Sleep -Milliseconds 400
    [B5Run]::Park($hw); Start-Sleep -Milliseconds 300
    $rect = [B5Run]::GetRect($hw)
    $ox = $rect[0] - 14; $oy = $rect[1] - 14

    # nav order (38px pitch): general=134 hotkeys=172 translate=210 engines=248
    # ... history=286 vocab=324 glossary=362 advanced=396
    [B5Run]::ClickAt($ox + 95, $oy + 210); Start-Sleep -Milliseconds 900
    $f = Join-Path $OutDir 'batch5-translate-top-light.png'
    Write-Output "$f  $([B5Run]::Capture($hw, $f, 14))"
    [B5Run]::WheelDown($ox + 640, $oy + 430, 6); Start-Sleep -Milliseconds 500
    [B5Run]::BringToFront($hw); Start-Sleep -Milliseconds 200
    $f = Join-Path $OutDir 'batch5-translate-context-card-light.png'
    Write-Output "$f  $([B5Run]::Capture($hw, $f, 14))"

    [B5Run]::ClickAt($ox + 95, $oy + 134); Start-Sleep -Milliseconds 900
    [B5Run]::WheelDown($ox + 640, $oy + 430, 12); Start-Sleep -Milliseconds 500
    [B5Run]::BringToFront($hw); Start-Sleep -Milliseconds 200
    $f = Join-Path $OutDir 'batch5-general-profiles-light.png'
    Write-Output "$f  $([B5Run]::Capture($hw, $f, 14))"
    Stop-App

    # ---------- Phase B: AI engine -> local stub ----------
    $entropy = [Text.Encoding]::UTF8.GetBytes('TranslationApp.SecretStore.v1')
    $cipher = [Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes('sk-verify-dummy'), $entropy, 'CurrentUser')
    $env:B5_SETTINGS = $settingsPath
    $env:B5_KEY = 'enc:' + [Convert]::ToBase64String($cipher)
    $env:B5_PORT = "$Port"
    & $python $patchPy
    Write-Output "PATCH EXIT $LASTEXITCODE"

    $stub = Start-Process -FilePath $python -ArgumentList @($stubPy, "$Port", $stubLog) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2

    $pb = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 7
    $ws = New-Object -ComObject WScript.Shell

    # Alt+D toggles the window, so only send it when the window is actually hidden.
    function Show-Quick([int]$procId) {
        $h = [B5Run]::FindWindow($procId)
        if ($h -eq [IntPtr]::Zero) {
            $ws.SendKeys('%d'); Start-Sleep -Milliseconds 1500
            $h = [B5Run]::FindWindow($procId)
        }
        return $h
    }

    # Sentence 1: nothing in history for this pair yet -> no context expected.
    # Text goes in via the clipboard: raw SendKeys characters get composed by the active
    # CJK IME (observed: "b5probe apple is red" arrived as one mangled token).
    Set-Clipboard -Value $marker1
    $qw = Show-Quick $pb.Id
    if ($qw -eq [IntPtr]::Zero) { Write-Output 'QUICK WINDOW NOT FOUND'; exit 1 }
    [B5Run]::Focus($qw); Start-Sleep -Milliseconds 400
    $ws.SendKeys('^v'); Start-Sleep -Seconds 4
    $f = Join-Path $OutDir 'batch5-quick-first-light.png'
    Write-Output "$f  $([B5Run]::Capture($qw, $f, 10))"
    Write-Output ("QUICK RECT " + ([B5Run]::GetRect($qw) -join ','))
    # paste-translate may already have fired (empty box + Ctrl+V); Enter covers the plain
    # paste path. The stub log, not the timing, is what the assertions read.
    $ws.SendKeys('{ENTER}'); Start-Sleep -Seconds 4
    $f = Join-Path $OutDir 'batch5-quick-first-after-enter-light.png'
    Write-Output "$f  $([B5Run]::Capture($qw, $f, 10))"
    $qr = [B5Run]::GetRect($qw)
    Write-Output ("QUICK RECT2 " + ($qr -join ','))

    # second sentence: FR-050 should attach the first one as context
    Set-Clipboard -Value $marker2
    $qw2 = Show-Quick $pb.Id
    if ($qw2 -eq [IntPtr]::Zero) { Write-Output 'QUICK WINDOW 2 NOT FOUND'; exit 1 }
    [B5Run]::Focus($qw2); Start-Sleep -Milliseconds 400
    $ws.SendKeys('^a'); Start-Sleep -Milliseconds 200
    $ws.SendKeys('^v'); Start-Sleep -Milliseconds 300
    $ws.SendKeys('{ENTER}')
    Start-Sleep -Seconds 4
    $f = Join-Path $OutDir 'batch5-quick-second-context-light.png'
    Write-Output "$f  $([B5Run]::Capture($qw2, $f, 10))"
    $qr2 = [B5Run]::GetRect($qw2)
    Write-Output ("QUICK2 RECT " + ($qr2 -join ','))

    # FR-051: click "more formal" (offsets are params so they can be retuned without editing)
    [B5Run]::BringToFront($qw2); Start-Sleep -Milliseconds 200
    $cx = $qr2[0] + $StyleX; $cy = $qr2[3] + $StyleY
    Write-Output ("STYLE CLICK $cx,$cy")
    [B5Run]::ClickAt($cx, $cy)
    Start-Sleep -Seconds 4
    [B5Run]::BringToFront($qw2); Start-Sleep -Milliseconds 200
    $f = Join-Path $OutDir 'batch5-quick-style-formal-light.png'
    Write-Output "$f  $([B5Run]::Capture($qw2, $f, 10))"

    Write-Output '----- STUB LOG -----'
    if (Test-Path $stubLog) {
        # dump-stub-log.py prints ASCII-escaped prompts + the two booleans we care about:
        # a GBK console would mangle the Chinese and hide exactly the evidence we want.
        & $python (Join-Path $PSScriptRoot 'fixtures\dump-stub-log.py') $stubLog
    } else { Write-Output 'NO STUB LOG' }
}
finally {
    Stop-App
    if ($stub -and -not $stub.HasExited) { Stop-Process -Id $stub.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path $backup) { Copy-Item $backup $settingsPath -Force; Remove-Item $backup -Force }
    # drop the two probe rows the test sentences wrote
    if (Test-Path $dbPath) {
        & $python (Join-Path $PSScriptRoot 'fixtures\clear-probe-rows.py') $dbPath
    }
    Write-Output 'RESTORED'
}
