# Batch 6c streak probe (ascii-only source).
#
# The earlier amber scans were inconclusive because bing answers in ~300ms - the bar was only
# on screen for a fraction of the sampling window. This one pins the engine to google with its
# 8s timeout so the indeterminate bar is guaranteed visible for ~8s, then samples the streak
# position 6 times. settings.json is byte-restored from a backup in finally (the old
# probe-progress.ps1 rewrote it through ConvertTo-Json, which can flatten nested settings).
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [int]$Frames = 6
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Str {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder t, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
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
    public static int[] Rect(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }
    public static void Focus(IntPtr h) { ShowWindow(h, 5); SetForegroundWindow(h); }
}
"@ -Language CSharp

if (-not (Test-Path $Exe)) { Write-Output "EXE not found: $Exe"; exit 1 }
$Exe = (Resolve-Path $Exe).Path
[void][Str]::SetProcessDPIAware()

$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
$backup = Join-Path $env:TEMP ("translationapp-streak-" + [Guid]::NewGuid().ToString('N') + '.json')
$patchPy = Join-Path $env:TEMP ("translationapp-streak-" + [Guid]::NewGuid().ToString('N') + '.py')
Copy-Item $settingsPath $backup -Force

@'
import json, os, sys, io
p = os.path.join(os.environ['APPDATA'], 'TranslationApp', 'settings.json')
with io.open(p, encoding='utf-8') as f:
    s = json.load(f)
s['Engine'] = sys.argv[1]
with io.open(p, 'w', encoding='utf-8') as f:
    json.dump(s, f, ensure_ascii=False, indent=2)
print('engine=' + s['Engine'])
'@ | Set-Content -Path $patchPy -Encoding ASCII

$python = (Get-Command python.exe -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = 'python' }

$charmap = $null
try {
    $env:PROBE_SETTINGS = $settingsPath
    & $python $patchPy google | Out-Null

    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
    $app = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 5

    $charmap = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\charmap.exe') -PassThru
    Start-Sleep -Seconds 2
    $cw = [Str]::FindWindow($charmap.Id)
    if ($cw -ne [IntPtr]::Zero) { [void][Str]::Focus($cw) }
    Start-Sleep -Milliseconds 400

    $ws = New-Object -ComObject WScript.Shell
    $ws.SendKeys('%d')
    Start-Sleep -Milliseconds 1500
    $qw = [Str]::FindWindow($app.Id)
    if ($qw -eq [IntPtr]::Zero) { Write-Output 'FAIL: quick window not found'; exit 1 }

    [void][Str]::Focus($qw)
    Start-Sleep -Milliseconds 250
    Set-Clipboard 'good morning everyone'
    $ws.SendKeys('^v')
    Start-Sleep -Milliseconds 200
    $ws.SendKeys('{ENTER}')

    $r = [Str]::Rect($qw)
    for ($i = 0; $i -lt $Frames; $i++) {
        Start-Sleep -Milliseconds 700
        $bmp = [System.Drawing.Bitmap]::new([int]$r[2], [int]$r[3])
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r[0], $r[1], 0, 0, [System.Drawing.Size]::new([int]$r[2], [int]$r[3]))
        $g.Dispose()
        $found = @()
        for ($py = 0; $py -lt $bmp.Height; $py++) {
            $cnt = 0; $minx = 99999; $maxx = -1
            for ($px = 4; $px -lt $bmp.Width - 4; $px++) {
                $c = $bmp.GetPixel($px, $py)
                if ($c.R -gt 200 -and $c.G -gt 110 -and $c.G -lt 215 -and $c.B -lt 90) {
                    $cnt++; if ($px -lt $minx) { $minx = $px }; if ($px -gt $maxx) { $maxx = $px }
                }
            }
            # the streak is a 96px wide band; a full-width line is a focus border, not the streak
            if ($cnt -ge 12 -and ($maxx - $minx) -lt 200) { $found += ("y{0}:n{1}x{2}-{3}" -f $py, $cnt, $minx, $maxx) }
        }
        Write-Output ("FRAME {0} {1}" -f $i, (($found) -join '  '))
        $bmp.Save(("$PSScriptRoot\probe\streak-f{0}.png" -f $i), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }
    $ws.SendKeys('{ESC}')
}
finally {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($charmap -and -not $charmap.HasExited) { Stop-Process -Id $charmap.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 600
    Copy-Item $backup $settingsPath -Force
    Remove-Item $backup -Force -ErrorAction SilentlyContinue
    Remove-Item $patchPy -Force -ErrorAction SilentlyContinue
    Write-Output 'SETTINGS RESTORED'
}
