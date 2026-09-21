# Batch 6c: verify shadow reading paces with the voice (ascii-only source).
#
# Before the fix SpeakAndWaitAsync returned the instant the text was queued (SAPI has not
# flipped State to Speaking yet), so the loop cancelled each sentence and flew to the last one
# - the user saw the highlight "jumping" ahead of the audio. This opens the quick window,
# translates three sentences, starts 跟读 and samples the highlighted line 6 times ~1.1s apart.
# Expected after the fix: the accent bar advances roughly one line per sentence, not all at once.
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [string]$OutTag = 'shadow-fixed',
    [string]$ShadowClick = '318,145',
    [int]$Samples = 6,
    [int]$SampleGapMs = 1100,
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

public static class Sh {
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
[void][Sh]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$text = 'We were moving mountains long before we knew we could. There can be miracles when you believe. This third sentence is long enough to keep the voice busy for a while.'

$charmap = $null
try {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
    $app = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 6

    $charmap = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\charmap.exe') -PassThru
    Start-Sleep -Seconds 2
    $cw = [Sh]::FindWindow($charmap.Id)
    if ($cw -ne [IntPtr]::Zero) { [Sh]::Focus($cw) }
    Start-Sleep -Milliseconds 500

    $ws = New-Object -ComObject WScript.Shell
    $ws.SendKeys('%d')
    Start-Sleep -Milliseconds 1500
    $qw = [Sh]::FindWindow($app.Id)
    if ($qw -eq [IntPtr]::Zero) { Write-Output 'FAIL: quick window not found'; exit 1 }
    $r = [Sh]::Rect($qw)
    Write-Output ("QUICK " + ($r -join ','))

    [Sh]::Focus($qw)
    Start-Sleep -Milliseconds 250
    Set-Clipboard $text
    $ws.SendKeys('^v{ENTER}')
    Start-Sleep -Seconds 4

    $c = $ShadowClick -split ','
    [Sh]::Click(($r[0] + [int]$c[0]), ($r[1] + [int]$c[1]))
    Start-Sleep -Milliseconds 400

    for ($i = 0; $i -lt $Samples; $i++) {
        $f = Join-Path $OutDir ("{0}-{1}.png" -f $OutTag, $i)
        Write-Output "$f  $([Sh]::Shot($qw, $f))"
        Start-Sleep -Milliseconds $SampleGapMs
    }
    $ws.SendKeys('{ESC}')
}
finally {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($charmap -and -not $charmap.HasExited) { Stop-Process -Id $charmap.Id -Force -ErrorAction SilentlyContinue }
    Write-Output 'DONE'
}
