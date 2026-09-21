# Atomic UI driver for reproducing user-reported defects (ascii-only source).
#
# Why this exists: the previous attempt set the clipboard / raised the window between the
# click and the keystrokes, and Windows focus-stealing prevention meant the keys landed in
# whatever window the agent's own console had pulled forward - so "typing did nothing"
# could not be told apart from "the harness never typed". Here every action happens inside
# one process with real SendInput, and the clipboard is prepared before the click.
#
# Script DSL (semicolon separated):
#   click:X,Y            real left click at window-relative X,Y
#   dclick:X,Y           double click
#   keys:ctrl+v          SendInput key chord (ctrl|alt|shift prefixes, + separated)
#   text:ABC             SendInput UNICODE keystrokes (IME bypassed - deterministic)
#   shot:label           capture the window to <OutTag>-NN-label.png
#   wait:MS              sleep
#   nav:NAME             click a settings nav row by name (general|hotkeys|translate|
#                        engines|history|vocab|glossary|advanced)
#   addterm              click 「添加词条」 (glossary tab, top-right)
#   park:X,Y             re-park the window (keeps coordinates stable)
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [string]$ExtraArgs = '--settings',
    [string]$Script = '',
    [string]$ClipboardText = '',
    [string]$OutTag = 'ui',
    [string]$OutDir = "$PSScriptRoot\..\build\probe",
    [int]$OriginX = 240,
    [int]$OriginY = 120,
    [int]$Width = 720,
    [int]$Height = 520
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class Ui2 {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr extra; }

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

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSE_MOVE = 0x0001, MOUSE_ABS = 0x8000, MOUSE_DOWN = 0x0002, MOUSE_UP = 0x0004;
    private const uint KEY_UP = 0x0002, KEY_UNICODE = 0x0004;

    private static void Send(INPUT[] items) { SendInput((uint)items.Length, items, Marshal.SizeOf(typeof(INPUT))); }

    private static void MoveTo(int x, int y) {
        INPUT m = new INPUT(); m.type = INPUT_MOUSE;
        m.U.mi.dx = (int)Math.Round(x * 65535.0 / (GetSystemMetrics(0) - 1));
        m.U.mi.dy = (int)Math.Round(y * 65535.0 / (GetSystemMetrics(1) - 1));
        m.U.mi.dwFlags = MOUSE_MOVE | MOUSE_ABS;
        Send(new INPUT[] { m });
    }
    public static void Click(int x, int y, int count) {
        MoveTo(x, y); System.Threading.Thread.Sleep(90);
        for (int i = 0; i < count; i++) {
            INPUT d = new INPUT(); d.type = INPUT_MOUSE; d.U.mi.dwFlags = MOUSE_DOWN;
            INPUT u = new INPUT(); u.type = INPUT_MOUSE; u.U.mi.dwFlags = MOUSE_UP;
            Send(new INPUT[] { d }); System.Threading.Thread.Sleep(45);
            Send(new INPUT[] { u }); System.Threading.Thread.Sleep(90);
        }
    }
    public static void Type(string s) {
        foreach (char c in s) {
            INPUT d = new INPUT(); d.type = INPUT_KEYBOARD;
            d.U.ki.wScan = (ushort)c; d.U.ki.dwFlags = KEY_UNICODE;
            INPUT u = new INPUT(); u.type = INPUT_KEYBOARD;
            u.U.ki.wScan = (ushort)c; u.U.ki.dwFlags = KEY_UNICODE | KEY_UP;
            Send(new INPUT[] { d, u });
            System.Threading.Thread.Sleep(35);
        }
    }
    public static void Chord(string spec) {
        string[] parts = spec.Split('+');
        ushort vk = (ushort)parts[parts.Length - 1].ToUpper()[0];
        if (parts[parts.Length - 1].Length > 1) vk = (ushort)int.Parse(parts[parts.Length - 1], System.Globalization.NumberStyles.HexNumber);
        bool ctrl = spec.ToLower().Contains("ctrl"), alt = spec.ToLower().Contains("alt"), shift = spec.ToLower().Contains("shift");
        INPUT[] down = new INPUT[ctrl ? 2 : 1];
        int i = 0;
        if (ctrl) { INPUT c = new INPUT(); c.type = INPUT_KEYBOARD; c.U.ki.wVk = 0x11; down[i++] = c; }
        INPUT k = new INPUT(); k.type = INPUT_KEYBOARD; k.U.ki.wVk = vk; down[i] = k;
        Send(down);
        System.Threading.Thread.Sleep(45);
        INPUT[] up = new INPUT[ctrl ? 2 : 1];
        i = 0;
        INPUT ku = new INPUT(); ku.type = INPUT_KEYBOARD; ku.U.ki.wVk = vk; ku.U.ki.dwFlags = KEY_UP;
        if (ctrl) { INPUT cu = new INPUT(); cu.type = INPUT_KEYBOARD; cu.U.ki.wVk = 0x11; cu.U.ki.dwFlags = KEY_UP; up[i++] = cu; }
        up[i] = ku;
        Send(up);
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
    public static void Park(IntPtr h, int x, int y, int w, int hh) {
        // the window restores a different size between runs, so the size is pinned here to keep
        // measured coordinates valid
        SetWindowPos(h, IntPtr.Zero, x, y, w, hh, 0x0040 | 0x0004);
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
[void][Ui2]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$navY = @{ general = 113; hotkeys = 151; translate = 189; engines = 227; history = 265; vocab = 303; glossary = 341; advanced = 379 }

$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
$backup = Join-Path $env:TEMP ("translationapp-ui2-" + [Guid]::NewGuid().ToString('N') + '.json')
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }

try {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
    if ($ClipboardText) { Set-Clipboard $ClipboardText }
    if ($ExtraArgs) { $app = Start-Process -FilePath $Exe -ArgumentList $ExtraArgs -PassThru }
    else { $app = Start-Process -FilePath $Exe -PassThru }
    Start-Sleep -Seconds 7
    $hw = [Ui2]::FindWindow($app.Id)
    if ($hw -eq [IntPtr]::Zero) { Write-Output 'FAIL: no window'; exit 1 }
    [Ui2]::Park($hw, $OriginX, $OriginY, $Width, $Height)
    Start-Sleep -Milliseconds 400
    [Ui2]::Park($hw, $OriginX, $OriginY, $Width, $Height)
    Start-Sleep -Milliseconds 300
    $r = [Ui2]::Rect($hw)
    Write-Output ("WINDOW " + ($r -join ','))

    $idx = 0
    function Shoot([string]$label) {
        $script:idx++
        $f = Join-Path $OutDir ("{0}-{1:d2}-{2}.png" -f $OutTag, $script:idx, $label)
        Write-Output ("  SHOT " + $f)
        [void][Ui2]::Shot($hw, $f)
    }
    Shoot 'start'

    foreach ($raw in ($Script -split ';')) {
        $step = $raw.Trim()
        if (-not $step) { continue }
        $parts = $step -split ':', 2
        $verb = $parts[0].ToLower()
        $arg = if ($parts.Count -gt 1) { $parts[1] } else { '' }
        [Ui2]::Focus($hw)
        Start-Sleep -Milliseconds 120
        switch ($verb) {
            'click' {
                $c = $arg -split ','
                [Ui2]::Click(($r[0] + [int]$c[0]), ($r[1] + [int]$c[1]), 1)
                Write-Output ("  click " + $arg)
            }
            'dclick' {
                $c = $arg -split ','
                [Ui2]::Click(($r[0] + [int]$c[0]), ($r[1] + [int]$c[1]), 2)
                Write-Output ("  dclick " + $arg)
            }
            'keys' { [Ui2]::Chord($arg); Write-Output ("  keys " + $arg) }
            'text' { [Ui2]::Type($arg); Write-Output ("  text " + $arg) }
            'wait' { Start-Sleep -Milliseconds ([int]$arg) }
            'nav' {
                $y = $navY[$arg.ToLower()]
                [Ui2]::Click(($r[0] + 68), ($r[1] + $y), 1)
                Write-Output ("  nav " + $arg + " y=" + $y)
            }
            'addterm' { [Ui2]::Click(($r[0] + 638), ($r[1] + 68), 1); Write-Output '  addterm' }
            'shot' { Shoot $arg }
            'park' {
                $c = $arg -split ','
                [Ui2]::Park($hw, [int]$c[0], [int]$c[1], $Width, $Height)
                Start-Sleep -Milliseconds 300
                $r = [Ui2]::Rect($hw)
                Write-Output ("  reparked " + ($r -join ','))
            }
            default { Write-Output ("  UNKNOWN STEP " + $step) }
        }
        Start-Sleep -Milliseconds 350
    }
    Write-Output 'SCRIPT DONE'
}
finally {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if (Test-Path $backup) { Copy-Item $backup $settingsPath -Force; Remove-Item $backup -Force }
    Write-Output 'SETTINGS RESTORED'
}
