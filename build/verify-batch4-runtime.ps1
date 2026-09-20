# FR-049 runtime harness (ascii-only): copy the synthetic mdx into
# %AppData%\TranslationApp\dicts, open settings -> advanced, screenshot the
# dictionary card, then run a test query. Restores settings.json at the end.
# fixtures\fake-zuci.mdx is a synthetic MDX v3-SQLite dictionary (3 entries) built with the
# same block-header encoding that MdxDictionaryReaderTests uses; regenerate it from those
# test helpers if it ever needs replacing.
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [string]$MdxSource = "$PSScriptRoot\fixtures\fake-zuci.mdx",
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

public static class B4Run {
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
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public static int[] Cursor() { POINT p; GetCursorPos(out p); return new int[] { p.X, p.Y }; }
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int n);
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
    public static int SM(int i) { return GetSystemMetrics(i); }
    public static string DpiProbe() { return "ok"; }
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
if (-not (Test-Path $MdxSource)) { Write-Output "MDX not found: $MdxSource"; exit 1 }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
$dictsDir = Join-Path $env:APPDATA 'TranslationApp\dicts'
$backup = Join-Path $env:TEMP ("translationapp-batch4run-" + [Guid]::NewGuid().ToString('N') + '.json')
$installed = Join-Path $dictsDir 'fake-zuci.mdx'

function Stop-App { Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 900 }

try {
    if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
    New-Item -ItemType Directory -Force -Path $dictsDir | Out-Null
    Copy-Item $MdxSource $installed -Force
    Stop-App

    $p = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
    Start-Sleep -Seconds 4
    $hw = [B4Run]::FindWindow($p.Id)
    if ($hw -eq [IntPtr]::Zero) { Write-Output 'WINDOW NOT FOUND'; exit 1 }
    [B4Run]::Focus($hw) | Out-Null; Start-Sleep -Milliseconds 600
    [B4Run]::Park($hw); Start-Sleep -Milliseconds 500
    [B4Run]::Park($hw); Start-Sleep -Milliseconds 300
    $rect = [B4Run]::GetRect($hw)
    $ox = $rect[0] - 14; $oy = $rect[1] - 14
    [B4Run]::BringToFront($hw); Start-Sleep -Milliseconds 200
    Write-Output ("RECT " + ($rect -join ',') + " CLICK " + ($ox + 95) + "," + ($oy + 396))
    foreach ($y in 358, 396) {
        [B4Run]::ClickAt($ox + 95, $oy + $y); Start-Sleep -Milliseconds 900
        $mid = Join-Path $OutDir ("batch4run-navcheck-" + $y + ".png")
        Write-Output "$mid  $([B4Run]::Capture($hw, $mid, 14))"
    }
    [B4Run]::BringToFront($hw)
    [B4Run]::WheelDown($ox + 640, $oy + 430, 4); Start-Sleep -Milliseconds 500
    [B4Run]::BringToFront($hw); Start-Sleep -Milliseconds 200
    $file = Join-Path $OutDir 'batch4run-dict-installed-light.png'
    Write-Output "$file  $([B4Run]::Capture($hw, $file, 14))"

    # test query: type apple into the dictionary card input, then Enter
    [B4Run]::ClickAt($ox + 866, $oy + 547); Start-Sleep -Milliseconds 400
    foreach ($ch in 'apple'.ToCharArray()) {
        [B4Run]::Key([uint16][int][char]([string]$ch).ToUpper())
        Start-Sleep -Milliseconds 80
    }
    Start-Sleep -Milliseconds 300
    [B4Run]::Key(0x0D)
    Start-Sleep -Milliseconds 2000
    [B4Run]::BringToFront($hw)
    $file2 = Join-Path $OutDir 'batch4run-dict-testquery-light.png'
    Write-Output "$file2  $([B4Run]::Capture($hw, $file2, 14))"
    Stop-App
} finally {
    Stop-App
    Remove-Item $installed -Force -ErrorAction SilentlyContinue
    if ((Test-Path $backup) -and (Test-Path $settingsPath)) { Copy-Item $backup $settingsPath -Force; Remove-Item $backup -Force }
    Write-Output 'DONE'
}
