# UI review harness: captures every surface and state in both themes for visual review.
# Usage: powershell -ExecutionPolicy Bypass -File build\verify-ui.ps1 [-Exe <path>] [-OutDir <dir>] [-ZoomIcons]
# ASCII only: PowerShell 5.1 misparses UTF-8 without BOM.
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
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

public static class UiShot {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    public static IntPtr FindWindow(int pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            uint wpid; GetWindowThreadProcessId(hWnd, out wpid);
            if ((int)wpid != pid) return true;
            if (!IsWindowVisible(hWnd)) return true;
            StringBuilder t = new StringBuilder(512);
            GetWindowTextW(hWnd, t, t.Capacity);
            if (t.ToString().Length == 0) return true;
            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static string CaptureWindow(IntPtr hWnd, string path, int margin) {
        RECT r;
        if (!GetWindowRect(hWnd, out r)) return "GetWindowRect failed";
        int w = (r.Right - r.Left) + margin * 2;
        int h = (r.Bottom - r.Top) + margin * 2;
        int sx = Math.Max(0, r.Left - margin);
        int sy = Math.Max(0, r.Top - margin);
        using (Bitmap bmp = new Bitmap(w, h)) {
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.CopyFromScreen(sx, sy, 0, 0, new Size(w, h));
            }
            bmp.Save(path, ImageFormat.Png);
        }
        return w + "x" + h;
    }

    /// <summary>Crop a region of an existing PNG and scale it up, for close icon inspection.</summary>
    public static string ZoomRegion(string src, string dst, int x, int y, int w, int h, int scale) {
        using (Bitmap src0 = new Bitmap(src)) {
            using (Bitmap out0 = new Bitmap(w * scale, h * scale)) {
                using (Graphics g = Graphics.FromImage(out0)) {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(src0, new Rectangle(0, 0, w * scale, h * scale), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
                }
                out0.Save(dst, ImageFormat.Png);
            }
        }
        return (w * scale) + "x" + (h * scale);
    }

    public static void Focus(IntPtr hWnd) { SetForegroundWindow(hWnd); }

    /// <summary>Move + resize a window (used to capture the tall engine page in slices).</summary>
    public static void SetBounds(IntPtr hWnd, int x, int y, int w, int h) {
        SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Capture a raw screen region (used to slice a window taller than the screen).</summary>
    public static string CaptureRegion(string path, int x, int y, int w, int h) {
        using (Bitmap bmp = new Bitmap(w, h)) {
            using (Graphics g = Graphics.FromImage(bmp)) { g.CopyFromScreen(x, y, 0, 0, new Size(w, h)); }
            bmp.Save(path, ImageFormat.Png);
        }
        return w + "x" + h;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;

    /// <summary>
    /// Raise a window above all others. SetForegroundWindow often fails for a script process,
    /// which would leave another window on top of the capture region.
    /// </summary>
    public static void BringToFront(IntPtr hWnd) {
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Returns [left, top, right, bottom] of a window in screen pixels.</summary>
    public static int[] GetRect(IntPtr hWnd) {
        RECT r;
        GetWindowRect(hWnd, out r);
        return new int[] { r.Left, r.Top, r.Right, r.Bottom };
    }
}

public static class Typer {
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
    private const uint INPUT_KEYBOARD = 1;
    private const uint INPUT_MOUSE = 0;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_TAB = 0x09;

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private static void Send(INPUT i) { INPUT[] a = new INPUT[] { i }; SendInput(1, a, Marshal.SizeOf(typeof(INPUT))); }
    private static void MoveTo(int x, int y) {
        int sw = GetSystemMetrics(0);
        int sh = GetSystemMetrics(1);
        INPUT move = new INPUT(); move.type = INPUT_MOUSE;
        move.U.mi.dx = (int)Math.Round(x * 65535.0 / (sw - 1));
        move.U.mi.dy = (int)Math.Round(y * 65535.0 / (sh - 1));
        move.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        Send(move);
    }
    private static INPUT Key(ushort vk, bool up) {
        INPUT i = new INPUT(); i.type = INPUT_KEYBOARD; i.U.ki.wVk = vk;
        if (up) i.U.ki.dwFlags = KEYEVENTF_KEYUP;
        return i;
    }
    public static void TypeText(string t) {
        foreach (char c in t) {
            INPUT d = new INPUT(); d.type = INPUT_KEYBOARD; d.U.ki.wScan = c; d.U.ki.dwFlags = KEYEVENTF_UNICODE; Send(d);
            INPUT u = new INPUT(); u.type = INPUT_KEYBOARD; u.U.ki.wScan = c; u.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP; Send(u);
        }
    }
    public static void Enter() { Send(Key(VK_RETURN, false)); Send(Key(VK_RETURN, true)); }

    /// <summary>Press Tab (focus walking).</summary>
    public static void Tab() { Send(Key(VK_TAB, false)); Send(Key(VK_TAB, true)); }

    /// <summary>
    /// Scroll the wheel over a point. The target window must already be active/focused,
    /// otherwise Windows delivers the wheel to the focused window instead of the one under the cursor.
    /// </summary>
    public static void Wheel(int x, int y, int notches) {
        MoveTo(x, y);
        System.Threading.Thread.Sleep(100);
        for (int i = 0; i < Math.Abs(notches); i++) {
            INPUT w = new INPUT(); w.type = INPUT_MOUSE;
            w.U.mi.mouseData = (uint)(notches < 0 ? -120 : 120);
            w.U.mi.dwFlags = MOUSEEVENTF_WHEEL;
            Send(w);
            System.Threading.Thread.Sleep(40);
        }
    }

    /// <summary>Absolute mouse click in physical screen pixels.</summary>
    public static void ClickAt(int x, int y) {
        int sw = GetSystemMetrics(0);
        int sh = GetSystemMetrics(1);
        INPUT move = new INPUT(); move.type = INPUT_MOUSE;
        move.U.mi.dx = (int)Math.Round(x * 65535.0 / (sw - 1));
        move.U.mi.dy = (int)Math.Round(y * 65535.0 / (sh - 1));
        move.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        Send(move);
        System.Threading.Thread.Sleep(60);
        INPUT down = new INPUT(); down.type = INPUT_MOUSE; down.U.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
        Send(down);
        System.Threading.Thread.Sleep(40);
        INPUT up = new INPUT(); up.type = INPUT_MOUSE; up.U.mi.dwFlags = MOUSEEVENTF_LEFTUP;
        Send(up);
    }
}
"@

if (-not (Test-Path $Exe)) { Write-Output "EXE not found: $Exe"; exit 1 }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$settingsPath = Join-Path $env:APPDATA 'TranslationApp\settings.json'
Write-Output "exe=$Exe"
Write-Output "out=$OutDir"

$ws = New-Object -ComObject WScript.Shell

function Set-Setting([string]$name, $value) {
    if (Test-Path $settingsPath) {
        $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path $settingsPath) | Out-Null
        $json = [pscustomobject]@{}
    }
    if ($json.PSObject.Properties.Name -contains $name) { $json.$name = $value }
    else { $json | Add-Member -NotePropertyName $name -NotePropertyValue $value }
    $json | ConvertTo-Json -Depth 6 | Set-Content $settingsPath -Encoding UTF8
}

function Set-Theme([string]$theme) { Set-Setting 'Theme' $theme }

function Stop-App {
    Get-Process -Name TranslationApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
}

# Sidebar nav geometry (shared by the capture helpers).
# Order per 13.5: general -> hotkeys -> translate -> engine -> history -> vocabulary -> advanced.
$navClickY = @(134, 172, 210, 248, 286, 324, 362)
$navNames = @('general', 'hotkeys', 'translate', 'engine', 'history', 'vocabulary', 'advanced')
$engineNavIndex = 3

function Capture-Settings([string]$suffix) {
    Stop-App
    $p = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
    Start-Sleep -Seconds 4
    $hw = [UiShot]::FindWindow($p.Id)
    if ($hw -eq [IntPtr]::Zero) { Write-Output "settings${suffix}: NOT FOUND"; return }
    [UiShot]::Focus($hw) | Out-Null
    Start-Sleep -Milliseconds 600

    # Click each sidebar nav item and capture.
    # Coordinates derive from the layout (sidebar width 176, title block ~46px, item height 34 + 2px margins),
    # converted from image-relative to screen coordinates. UI Automation cannot be used here:
    # PowerShell resolves type literals at parse time, so a late assembly load does not help.
    $rect = [UiShot]::GetRect($hw)
    $originX = $rect[0] - 14   # captured image includes a 14px margin
    $originY = $rect[1] - 14

    for ($i = 0; $i -lt $navClickY.Count; $i++) {
        [UiShot]::BringToFront($hw)   # keep our window above any other app in the capture region
        Start-Sleep -Milliseconds 200
        [Typer]::ClickAt([int]($originX + 95), [int]($originY + $navClickY[$i]))
        Start-Sleep -Milliseconds 700
        [UiShot]::BringToFront($hw)
        Start-Sleep -Milliseconds 200
        $file = Join-Path $OutDir ("settings-$($i+1)-$($navNames[$i])$suffix.png")
        $size = [UiShot]::CaptureWindow($hw, $file, 14)
        Write-Output ("settings-$($i+1)-$($navNames[$i])$suffix.png: " + $size)

        # Translate page: open the engine dropdown to show the "（未配置�? suffix on engines without a key.
        # Coordinates measured from the capture: the engine ComboBox sits at image (~485..690, ~108..138).
        # No BringToFront here: raising the main window would cover its own popup.
        if ($navNames[$i] -eq 'translate') {
            [Typer]::ClickAt([int]($originX + 600), [int]($originY + 123))
            Start-Sleep -Milliseconds 900
            $file = Join-Path $OutDir ("settings-3-translate-engines$suffix.png")
            Write-Output ("settings-3-translate-engines$suffix.png: " + [UiShot]::CaptureWindow($hw, $file, 14))
            $ws.SendKeys('{ESC}')
            Start-Sleep -Milliseconds 500
        }
    }

    # The engine page holds 4 cards and is taller than the screen. Widen the window (fewer wrapped
    # hints) and capture it in two slices: the page top with no scrolling, the page bottom after
    # a wheel scroll. The settings window is resizable, so this is a legitimate window state.
    [UiShot]::BringToFront($hw)
    Start-Sleep -Milliseconds 200
    [UiShot]::SetBounds($hw, 0, 0, 1010, 1030)
    Start-Sleep -Milliseconds 900
    # The window moved/resized: recompute the image->screen offset before clicking anything.
    $rect = [UiShot]::GetRect($hw)
    $originX = $rect[0] - 14
    $originY = $rect[1] - 14
    [Typer]::ClickAt([int]($originX + 95), [int]($originY + $navClickY[$engineNavIndex]))
    Start-Sleep -Milliseconds 700
    [UiShot]::BringToFront($hw)
    Start-Sleep -Milliseconds 300
    $file = Join-Path $OutDir ("settings-4-engine$suffix-part1.png")
    Write-Output ("settings-4-engine$suffix-part1.png: " + [UiShot]::CaptureRegion($file, 0, 0, 1010, 1030))

    # Bottom slice: click a focusable field (clicking a plain TextBlock does not activate the window,
    # and the wheel is then delivered elsewhere) and scroll the page down with the wheel.
    [Typer]::ClickAt(815, 219)
    Start-Sleep -Milliseconds 400
    [Typer]::Wheel(500, 600, -8)
    Start-Sleep -Milliseconds 800
    [UiShot]::BringToFront($hw)
    Start-Sleep -Milliseconds 300
    $file = Join-Path $OutDir ("settings-4-engine$suffix-part2.png")
    Write-Output ("settings-4-engine$suffix-part2.png: " + [UiShot]::CaptureRegion($file, 0, 0, 1010, 1030))
}

function Capture-Quick([string]$suffix, [string]$text, [int]$waitSec = 5) {
    Stop-App
    $p = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 5
    $ws.SendKeys('%d')
    Start-Sleep -Milliseconds 1500
    $hw = [UiShot]::FindWindow($p.Id)
    if ($hw -eq [IntPtr]::Zero) { Write-Output "quick${suffix}: NOT FOUND"; return }
    Write-Output ("quick$suffix.png: " + [UiShot]::CaptureWindow($hw, (Join-Path $OutDir "quick$suffix.png"), 22))
    if ($text) {
        [Typer]::TypeText($text)
        Start-Sleep -Milliseconds 400
        [Typer]::Enter()
        if ($waitSec -le 6) {
            Start-Sleep -Seconds 2
            Write-Output ("quick$suffix-loading.png: " + [UiShot]::CaptureWindow($hw, (Join-Path $OutDir "quick$suffix-loading.png"), 22))
            Start-Sleep -Seconds ($waitSec - 2)
        } else {
            # Slow-failure engine (Google: 8s timeout + fallback retry): the busy state is
            # guaranteed for seconds, so grab the loading state (amber streak progress bar)
            # deterministically at two moments -- the x offset between the two frames also
            # proves the indeterminate animation is actually moving.
            Start-Sleep -Milliseconds 1200
            Write-Output ("quick$suffix-loading.png: " + [UiShot]::CaptureWindow($hw, (Join-Path $OutDir "quick$suffix-loading.png"), 22))
            Start-Sleep -Milliseconds 2500
            Write-Output ("quick$suffix-loading2.png: " + [UiShot]::CaptureWindow($hw, (Join-Path $OutDir "quick$suffix-loading2.png"), 22))
            Start-Sleep -Seconds ($waitSec - 4)
        }
        Write-Output ("quick$suffix-result.png: " + [UiShot]::CaptureWindow($hw, (Join-Path $OutDir "quick$suffix-result.png"), 22))
    }
}

foreach ($theme in @('light','dark')) {
    Set-Theme $theme
    Capture-Quick "-$theme" 'good morning'
    Capture-Settings "-$theme"
}

# Error state: point the engine at Google, which is unreachable in this network,
# so the translation fails deterministically and the error banner renders.
Set-Theme 'light'
Set-Setting 'Engine' 'google'
Capture-Quick '-error' 'good morning' 26   # Google times out (8s) then retries once
Set-Setting 'Engine' 'bing'

Set-Theme 'system'
Stop-App

# Endpoint-unreachable state (13.9): point the "foreign engines" proxy at a black-hole address so the
# engine page's reachability probe fails deterministically and the warning banner renders.
Set-Theme 'light'
Set-Setting 'ProxyEnabled' $true
Set-Setting 'ProxyMode' 'googleOnly'
Set-Setting 'ProxyHost' '10.255.255.1'
Set-Setting 'ProxyPort' 9
Stop-App
$p = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
Start-Sleep -Seconds 10   # probe timeout is 6s
$hw = [UiShot]::FindWindow($p.Id)
if ($hw -eq [IntPtr]::Zero) {
    Write-Output "engine-unreachable-light.png: NOT FOUND"
} else {
    [UiShot]::Focus($hw) | Out-Null
    Start-Sleep -Milliseconds 500
    [UiShot]::BringToFront($hw)
    [UiShot]::SetBounds($hw, 0, 0, 1010, 1030)
    Start-Sleep -Milliseconds 900
    # The window moved/resized: recompute the image->screen offset before clicking anything.
    $rect = [UiShot]::GetRect($hw)
    $originX = $rect[0] - 14
    $originY = $rect[1] - 14
    [Typer]::ClickAt([int]($originX + 95), [int]($originY + $navClickY[$engineNavIndex]))
    Start-Sleep -Milliseconds 800
    [UiShot]::BringToFront($hw)
    Start-Sleep -Milliseconds 300
    Write-Output ("engine-unreachable-light.png: " + [UiShot]::CaptureRegion((Join-Path $OutDir 'engine-unreachable-light.png'), 0, 0, 1010, 1030))
    # Bottom slice: focus a field, then scroll the page down with the wheel.
    [Typer]::ClickAt(815, 219)
    Start-Sleep -Milliseconds 400
    [Typer]::Wheel(500, 600, -8)
    Start-Sleep -Milliseconds 800
    [UiShot]::BringToFront($hw)
    Start-Sleep -Milliseconds 300
    Write-Output ("engine-unreachable-light-part2.png: " + [UiShot]::CaptureRegion((Join-Path $OutDir 'engine-unreachable-light-part2.png'), 0, 0, 1010, 1030))
}
Set-Setting 'ProxyEnabled' $false
Set-Setting 'ProxyHost' ''
Set-Setting 'ProxyPort' 7890
Set-Theme 'system'
Stop-App

# Icon close-up from the light quick window (top-right control row)
$lightQuick = Join-Path $OutDir 'quick-light.png'
if (Test-Path $lightQuick) {
    Write-Output ("icons-zoom.png: " + [UiShot]::ZoomRegion($lightQuick, (Join-Path $OutDir 'icons-zoom.png'), 150, 44, 270, 34, 4))
}
Write-Output "done"
