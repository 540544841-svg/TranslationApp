# 一次性探针：验证进度条「斜切流光」在加载态真实渲染（Google 引擎 8s 超时 = 加载态确定持续）
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
public static class P {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);
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
    public static int[] Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return new int[] { r.Left, r.Top, r.Right, r.Bottom }; }
    public static void Shot(IntPtr h, string path) {
        RECT r; GetWindowRect(h, out r);
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        using (Bitmap b = new Bitmap(w, ht)) {
            using (Graphics g = Graphics.FromImage(b)) { g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, ht)); }
            b.Save(path, ImageFormat.Png);
        }
    }
}
"@

$exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe"
$out = "$PSScriptRoot\probe"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'

function Set-Setting([string]$name, $value) {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($json.PSObject.Properties.Name -contains $name) { $json.$name = $value }
    else { $json | Add-Member -NotePropertyName $name -NotePropertyValue $value }
    $json | ConvertTo-Json -Depth 6 | Set-Content $settingsPath -Encoding UTF8
}

Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900
Set-Setting 'Engine' 'google'

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5
$ws = New-Object -ComObject WScript.Shell
$ws.SendKeys('%d')
Start-Sleep -Milliseconds 1200
$ws.SendKeys('good morning{ENTER}')

for ($i = 0; $i -lt 6; $i++) {
    Start-Sleep -Milliseconds 900
    $hw = [P]::Find($p.Id)
    if ($hw -eq [IntPtr]::Zero) { Write-Output "frame $i : window gone"; continue }
    $f = Join-Path $out ("frame$ i.png".Replace(' ', ''))
    [P]::Shot($hw, $f)
    # 扫描输入框以下区域（y>200）的琥珀像素
    $bmp = New-Object System.Drawing.Bitmap($f)
    $amber = 0; $xs = @()
    for ($y = 200; $y -lt $bmp.Height; $y++) {
        for ($x = 5; $x -lt $bmp.Width - 5; $x++) {
            $px = $bmp.GetPixel($x, $y)
            if ($px.R -gt 200 -and $px.G -gt 110 -and $px.G -lt 215 -and $px.B -lt 90) { $amber++; if ($xs.Count -lt 3) { $xs += $x } }
        }
    }
    Write-Output ("frame {0}: amberBelowInput={1} xs={2}" -f $i, $amber, ($xs -join ','))
    $bmp.Dispose()
}

Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900
Set-Setting 'Engine' 'bing'
Write-Output "probe done"
