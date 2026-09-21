# Batch 6c: reproduce / verify the 「对照 + 对比」 overlapping-text defect (ascii-only source).
#
# Sequence: Alt+D opens the quick window (charmap holds the foreground so the real
# previous-foreground path is exercised) -> paste a 3-paragraph text -> Enter -> wait for the
# result -> click 「对照」(paragraph align overlay) -> click 「对比」(multi-engine compare overlay).
# Both overlays live in the same grid cell, so before the fix the last frame shows source text,
# translations and the column error banner printed on top of each other.
# Run it twice: -Exe pointing at the pre-fix publish build (expect overlap) and at the fixed
# Debug build (expect compare columns only).
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [string]$OutTag = 'align-overlap-fixed',
    [string]$AlignClick = '318,145',
    [string]$CompareClick = '392,145',
    [string]$OutDir = "$PSScriptRoot\..\build\probe"
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class Ov {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr extra; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr h, StringBuilder t, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    private static void Send(INPUT[] items) { SendInput((uint)items.Length, items, Marshal.SizeOf(typeof(INPUT))); }
    public static void Click(int x, int y) {
        INPUT m = new INPUT(); m.type = 0;
        m.U.mi.dx = (int)Math.Round(x * 65535.0 / (GetSystemMetrics(0) - 1));
        m.U.mi.dy = (int)Math.Round(y * 65535.0 / (GetSystemMetrics(1) - 1));
        m.U.mi.dwFlags = 0x0001 | 0x8000;
        Send(new INPUT[] { m });
        System.Threading.Thread.Sleep(90);
        INPUT d = new INPUT(); d.type = 0; d.U.mi.dwFlags = 0x0002;
        INPUT u = new INPUT(); u.type = 0; u.U.mi.dwFlags = 0x0004;
        Send(new INPUT[] { d }); System.Threading.Thread.Sleep(50);
        Send(new INPUT[] { u });
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
    public static int[] Rect(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }
    public static void Focus(IntPtr h) { ShowWindow(h, 5); SetForegroundWindow(h); }
    public static string Shot(IntPtr h, string path) {
        RECT r; GetWindowRect(h, out r);
        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
        using (Bitmap b = new Bitmap(w, hh)) {
            using (Graphics g = Graphics.FromImage(b)) { g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, hh)); }
            b.Save(path, ImageFormat.Png);
        }
        return w + "x" + hh;
    }
}
"@ -ReferencedAssemblies System.Drawing -Language CSharp

if (-not (Test-Path $Exe)) { Write-Output "EXE not found: $Exe"; exit 1 }
$Exe = (Resolve-Path $Exe).Path
[void][Ov]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$text = "We were moving mountains long before we knew we could.`r`nThere can be miracles when you believe.`r`nThis is a third paragraph with enough words to make the aligner produce a third pair."

$charmap = $null
try {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
    $app = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 6

    $charmap = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\charmap.exe') -PassThru
    Start-Sleep -Seconds 2
    $cw = [Ov]::FindWindow($charmap.Id)
    if ($cw -ne [IntPtr]::Zero) { [Ov]::Focus($cw) }
    Start-Sleep -Milliseconds 500

    $ws = New-Object -ComObject WScript.Shell
    $ws.SendKeys('%d')
    Start-Sleep -Milliseconds 1500
    $qw = [Ov]::FindWindow($app.Id)
    if ($qw -eq [IntPtr]::Zero) { Write-Output 'FAIL: quick window not found'; exit 1 }
    $r = [Ov]::Rect($qw)
    Write-Output ("QUICK " + ($r -join ','))

    [Ov]::Focus($qw)
    Start-Sleep -Milliseconds 250
    Set-Clipboard $text
    $ws.SendKeys('^v')
    Start-Sleep -Milliseconds 200
    $ws.SendKeys('{ENTER}')
    Start-Sleep -Seconds 4
    $f = Join-Path $OutDir "$OutTag-1-result.png"
    Write-Output "$f  $([Ov]::Shot($qw, $f))"

    $a = $AlignClick -split ','
    [Ov]::Click(($r[0] + [int]$a[0]), ($r[1] + [int]$a[1]))
    Start-Sleep -Milliseconds 900
    $f = Join-Path $OutDir "$OutTag-2-align.png"
    Write-Output "$f  $([Ov]::Shot($qw, $f))"

    $c = $CompareClick -split ','
    [Ov]::Click(($r[0] + [int]$c[0]), ($r[1] + [int]$c[1]))
    Start-Sleep -Seconds 6
    $r2 = [Ov]::Rect($qw)
    $f = Join-Path $OutDir "$OutTag-3-compare.png"
    Write-Output "$f  $([Ov]::Shot($qw, $f)) rect=$($r2 -join ',')"
}
finally {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($charmap -and -not $charmap.HasExited) { Stop-Process -Id $charmap.Id -Force -ErrorAction SilentlyContinue }
    Write-Output 'DONE'
}
