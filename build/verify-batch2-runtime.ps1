# Batch 2 runtime smoke: prove the profile chain end to end through the REAL settings UI.
# Clicks 'qie dao yuedu' then 'qie dao yinsi' on the general page (coordinates derived from
# build\verify-batch2.ps1 captures at the same scroll position), asserting settings.json after each.
# Also probes Alt+P injection (informational only: SendInput hotkeys may not reach a tray app from
# an automation session; registration itself is shared with the three long-standing hotkeys).
# Backs up settings.json and restores it at the end. ASCII only (PS 5.1 misparses UTF-8 without BOM).
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe"
)

$ErrorActionPreference = 'Continue'

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class B2Auto {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

    public static IntPtr FindWindow(int pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p != pid || !IsWindowVisible(h)) return true;
            System.Text.StringBuilder t = new System.Text.StringBuilder(512); GetWindowTextW(h, t, t.Capacity);
            if (t.ToString().Length == 0) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    public static int[] GetRect(IntPtr hWnd) { RECT r; GetWindowRect(hWnd, out r); return new int[] { r.Left, r.Top, r.Right, r.Bottom }; }
    public static void Park(IntPtr hWnd) { SetWindowPos(hWnd, IntPtr.Zero, 80, 50, 1120, 860, 0x0004 | 0x0010); }
    public static void Focus(IntPtr hWnd) { SetForegroundWindow(hWnd); }

    private static void Send(INPUT i) { INPUT[] a = new INPUT[] { i }; SendInput(1, a, Marshal.SizeOf(typeof(INPUT))); }
    private static void MoveTo(int x, int y) {
        int sw = GetSystemMetrics(0); int sh = GetSystemMetrics(1);
        INPUT m = new INPUT(); m.type = 0;
        m.U.mi.dx = (int)Math.Round(x * 65535.0 / (sw - 1));
        m.U.mi.dy = (int)Math.Round(y * 65535.0 / (sh - 1));
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
            INPUT w = new INPUT(); w.type = 0; w.U.mi.mouseData = 4294967176; // -120 as uint
            w.U.mi.dwFlags = 0x0800; Send(w); System.Threading.Thread.Sleep(40);
        }
    }
    public static void AltP() {
        INPUT[] k = new INPUT[4];
        k[0] = Key(0x12, false); k[1] = Key(0x50, false); k[2] = Key(0x50, true); k[3] = Key(0x12, true);
        foreach (INPUT i in k) { Send(i); System.Threading.Thread.Sleep(25); }
    }
    private static INPUT Key(ushort vk, bool up) {
        INPUT i = new INPUT(); i.type = 1; i.U.ki.wVk = vk;
        if (up) i.U.ki.dwFlags = 0x0002;
        return i;
    }
}
"@

if (-not (Test-Path $Exe)) { Write-Output "EXE not found: $Exe"; exit 1 }

$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
$backupPath = "$settingsPath.batch2bak"
$failures = @()

function Stop-App {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
}

function Read-Settings { Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json }

function Assert([bool]$cond, [string]$label) {
    if (-not $cond) { $script:failures += $label; Write-Output "FAIL $label" } else { Write-Output "ok   $label" }
}

# Chinese literals built from code points (script stays ASCII): yuedu / yinsi
$profileReading = [string][char]0x9605 + [char]0x8BFB
$profilePrivacy = [string][char]0x9690 + [char]0x79C1

try {
    Stop-App
    if (Test-Path $settingsPath) { Copy-Item $settingsPath $backupPath -Force }
    if (Test-Path $settingsPath) { $json = Read-Settings }
    else {
        New-Item -ItemType Directory -Force -Path (Split-Path $settingsPath) | Out-Null
        $json = [pscustomobject]@{}
    }
    function Set-Json([string]$name, $value) {
        if ($json.PSObject.Properties.Name -contains $name) { $json.$name = $value }
        else { $json | Add-Member -NotePropertyName $name -NotePropertyValue $value }
    }
    Set-Json 'Engine' 'deepl'
    Set-Json 'CleanClipboardText' $false
    Set-Json 'PrivacyMode' $false
    Set-Json 'ActiveProfile' ''
    Set-Json 'ClipboardMonitorEnabled' $true
    Set-Json 'ShowStartBalloon' $false
    $json | ConvertTo-Json -Depth 6 | Set-Content $settingsPath -Encoding UTF8

    $p = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
    Start-Sleep -Seconds 5
    $hw = [B2Auto]::FindWindow($p.Id)
    if ($hw -eq [IntPtr]::Zero) { throw 'settings window not found' }
    [B2Auto]::Focus($hw) | Out-Null
    Start-Sleep -Milliseconds 600
    [B2Auto]::Park($hw); Start-Sleep -Milliseconds 500
    [B2Auto]::Park($hw); Start-Sleep -Milliseconds 400
    $rect = [B2Auto]::GetRect($hw)
    $ox = $rect[0] - 14
    $oy = $rect[1] - 14

    # general nav (first item), then wheel to the profile card (same geometry as verify-batch2)
    [B2Auto]::ClickAt($ox + 95, $oy + 134); Start-Sleep -Milliseconds 700
    [B2Auto]::WheelDown($ox + 640, $oy + 430, 22); Start-Sleep -Milliseconds 600

    # 'qie dao yuedu' button center measured from batch2-general-bottom-light.png at (297, 724)
    [B2Auto]::ClickAt($ox + 297, $oy + 724); Start-Sleep -Milliseconds 1500
    $j = Read-Settings
    Write-Output ("after click yuedu: Engine=" + $j.Engine + " Clean=" + $j.CleanClipboardText + " Profile=" + $j.ActiveProfile)
    Assert ($j.Engine -eq 'bing') 'yuedu engine=bing persisted'
    Assert ($j.CleanClipboardText -eq $true) 'yuedu cleaning on'
    Assert ($j.ActiveProfile -eq $profileReading) 'yuedu ActiveProfile persisted'
    Assert ($j.ClipboardMonitorEnabled -eq $true) 'yuedu leaves clipboard monitor untouched'

    # 'qie dao yinsi' button center at (436, 724)
    [B2Auto]::ClickAt($ox + 436, $oy + 724); Start-Sleep -Milliseconds 1500
    $j = Read-Settings
    Write-Output ("after click yinsi: Privacy=" + $j.PrivacyMode + " Monitor=" + $j.ClipboardMonitorEnabled + " Profile=" + $j.ActiveProfile)
    Assert ($j.PrivacyMode -eq $true) 'yinsi privacy on'
    Assert ($j.ClipboardMonitorEnabled -eq $false) 'yinsi clipboard monitor off'
    Assert ($j.ActiveProfile -eq $profilePrivacy) 'yinsi ActiveProfile persisted'

    # informational: Alt+P injection (tray hotkey; may not reach from an automation session)
    Stop-App
    $json = Read-Settings
    Set-Json 'ActiveProfile' ''
    $json | ConvertTo-Json -Depth 6 | Set-Content $settingsPath -Encoding UTF8
    $p2 = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 5
    [B2Auto]::AltP(); Start-Sleep -Milliseconds 1500
    $j = Read-Settings
    Write-Output ("INFO Alt+P injection result: Profile=" + $j.ActiveProfile + " Engine=" + $j.Engine)
    Stop-App
} catch {
    $failures += "exception: $($_.Exception.Message)"
    Write-Output "EXCEPTION $($_.Exception.Message)"
} finally {
    Stop-App
    if (Test-Path $backupPath) {
        Move-Item $backupPath $settingsPath -Force
        Write-Output 'settings.json restored from backup'
    }
}

if ($failures.Count -gt 0) {
    Write-Output ("RESULT FAIL: " + ($failures -join '; '))
    exit 1
}
Write-Output 'RESULT PASS: profile switches persisted through the real settings UI'
