# 探针 v2：窄条扫描进度条区域（y 250..330），10 帧 x 400ms，输出每帧琥珀 x 区间
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
public static class P2 {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder t, int c);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    public static IntPtr Find(int pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if ((int)wpid != pid) return true;
            if (!IsWindowVisible(h)) return true;
            StringBuilder t = new StringBuilder(512); GetWindowTextW(h, t, 512);
            if (t.ToString().Length == 0) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
    public static void Shot(IntPtr h, string path) {
        RECT r; GetWindowRect(h, out r);
        using (Bitmap b = new Bitmap(r.Right - r.Left, r.Bottom - r.Top)) {
            using (Graphics g = Graphics.FromImage(b)) { g.CopyFromScreen(r.Left, r.Top, 0, 0, b.Size); }
            b.Save(path, ImageFormat.Png);
        }
    }
}
"@

$exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe"
$out = "$PSScriptRoot\probe2"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
function Set-Setting([string]$name, $value) {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($json.PSObject.Properties.Name -contains $name) { $json.$name = $value }
    else { $json | Add-Member -NotePropertyName $name -NotePropertyValue $value }
    $json | ConvertTo-Json -Depth 6 | Set-Content $settingsPath -Encoding UTF8
}
function Scan-Strip([string]$file) {
    $bmp = New-Object System.Drawing.Bitmap($file)
    $rect = New-Object System.Drawing.Rectangle(0, 250, $bmp.Width, [Math]::Min(80, $bmp.Height - 250))
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $bytes = New-Object byte[] ($stride * $rect.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $minX = -1; $maxX = -1; $count = 0
    for ($y = 0; $y -lt $rect.Height; $y++) {
        $row = $y * $stride
        for ($x = 0; $x -lt $rect.Width; $x++) {
            $i = $row + $x * 4
            $b = $bytes[$i]; $g = $bytes[$i + 1]; $r = $bytes[$i + 2]
            if ($r -gt 200 -and $g -gt 110 -and $g -lt 215 -and $b -lt 90) {
                $count++
                if ($minX -lt 0 -or $x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
            }
        }
    }
    $bmp.Dispose()
    return [pscustomobject]@{ Count = $count; MinX = $minX; MaxX = $maxX }
}

Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900
Set-Setting 'Engine' 'google'

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5
$ws = New-Object -ComObject WScript.Shell
$ws.SendKeys('%d')
Start-Sleep -Milliseconds 1200
$ws.SendKeys('good morning')
Start-Sleep -Milliseconds 300
$ws.SendKeys('{ENTER}')

for ($i = 0; $i -lt 10; $i++) {
    $hw = [P2]::Find($p.Id)
    if ($hw -eq [IntPtr]::Zero) { Write-Output "frame $i : no window"; Start-Sleep -Milliseconds 400; continue }
    $f = Join-Path $out ("f{0:d2}.png" -f $i)
    [P2]::Shot($hw, $f)
    $r = Scan-Strip $f
    Write-Output ("frame {0}: amber={1} x=[{2}..{3}]" -f $i, $r.Count, $r.MinX, $r.MaxX)
    Start-Sleep -Milliseconds 400
}

Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900
Set-Setting 'Engine' 'bing'
Write-Output "probe2 done"
