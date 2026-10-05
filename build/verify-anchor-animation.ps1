# Verifies the two Win32/WPF facts the quick-window "top edge lock" relies on:
#   1) animating Window.Height (+Width) with a fixed Left/Top grows the window
#      downward/rightward and leaves the top-left pixel untouched;
#   2) re-placing the window with SetWindowPos at the pinned top right after the
#      animation ends does not move it (no second jolt at the end of the growth).
#
# The check window is shown with ShowActivated=$false, ShowInTaskbar=$false off the
# virtual desktop (right of every monitor), so it is invisible and takes neither
# focus nor the mouse. Nothing else on the machine is touched.
#
# Usage: powershell -ExecutionPolicy Bypass -File build\verify-anchor-animation.ps1
# ASCII only: PowerShell 5.1 misparses UTF-8 without BOM.
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName PresentationCore

if (-not ('AnWin' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class AnWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
}
"@
}

function Get-Rect([IntPtr]$hwnd) {
    $r = New-Object AnWin+RECT
    if (-not [AnWin]::GetWindowRect($hwnd, [ref]$r)) { throw 'GetWindowRect failed' }
    return $r
}

# Pump the dispatcher for N ms so animation clocks tick and layout settles.
function Pump([int]$ms) {
    $frame = New-Object System.Windows.Threading.DispatcherFrame
    $timer = New-Object System.Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds($ms)
    $timer.Add_Tick({ param($s, $e) $s.Stop(); $frame.Continue = $false })
    $timer.Start()
    [System.Windows.Threading.Dispatcher]::PushFrame($frame)
}

$failures = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) {
        Write-Host ("  [PASS] " + $name + " -- " + $detail)
    } else {
        Write-Host ("  [FAIL] " + $name + " -- " + $detail)
        $script:failures = $script:failures + 1
    }
}

# Park the check window right of every monitor: invisibly off the virtual desktop.
$vsRight = [AnWin]::GetSystemMetrics(76) + [AnWin]::GetSystemMetrics(78)
$offX = $vsRight + 600
$offY = 60

$win = New-Object System.Windows.Window
$win.WindowStyle = [System.Windows.WindowStyle]::None
$win.ResizeMode = [System.Windows.ResizeMode]::NoResize
$win.ShowInTaskbar = $false
$win.ShowActivated = $false
$win.Left = $offX
$win.Top = $offY
$win.Width = 300
$win.Height = 200
$win.Topmost = $false

$fgBefore = [AnWin]::GetForegroundWindow()
$win.Show()
Pump 120
$hwnd = (New-Object System.Windows.Interop.WindowInteropHelper($win)).Handle
if ($hwnd -eq [IntPtr]::Zero) { throw 'no HWND for the check window' }

# 1) Place it physically, exactly like QuickWindow does (physical pixels).
[void][AnWin]::SetWindowPos($hwnd, [IntPtr]::Zero, $offX, $offY, 300, 200, ([AnWin]::SWP_NOZORDER -bor [AnWin]::SWP_NOACTIVATE))
Pump 80
$r0 = Get-Rect $hwnd
Write-Host ("placed: ({0},{1}) {2}x{3}" -f $r0.Left, $r0.Top, ($r0.Right - $r0.Left), ($r0.Bottom - $r0.Top))

# 2) Grow width + height together with the shipped animation (260ms QuinticEase/EaseOut).
$easeH = New-Object System.Windows.Media.Animation.QuinticEase
$easeH.EasingMode = [System.Windows.Media.Animation.EasingMode]::EaseOut
$easeW = New-Object System.Windows.Media.Animation.QuinticEase
$easeW.EasingMode = [System.Windows.Media.Animation.EasingMode]::EaseOut
$animH = New-Object System.Windows.Media.Animation.DoubleAnimation(200, 560, [TimeSpan]::FromMilliseconds(260))
$animH.EasingFunction = $easeH
$animW = New-Object System.Windows.Media.Animation.DoubleAnimation(300, 420, [TimeSpan]::FromMilliseconds(260))
$animW.EasingFunction = $easeW
$win.BeginAnimation([System.Windows.FrameworkElement]::WidthProperty, $animW)
$win.BeginAnimation([System.Windows.FrameworkElement]::HeightProperty, $animH)
Pump 500
$r1 = Get-Rect $hwnd
$w1 = $r1.Right - $r1.Left
$h1 = $r1.Bottom - $r1.Top
Write-Host ("after animation: ({0},{1}) {2}x{3}" -f $r1.Left, $r1.Top, $w1, $h1)

Check 'grew (animation actually ticked)' (($w1 -gt 300) -and ($h1 -gt 200)) ("size is " + $w1 + "x" + $h1 + ", expected > 300x200")
Check 'top pixel unchanged while growing' ($r1.Top -eq $r0.Top) ("top " + $r0.Top + " -> " + $r1.Top)
Check 'left pixel unchanged while growing' ($r1.Left -eq $r0.Left) ("left " + $r0.Left + " -> " + $r1.Left)

# 3) Settle onto the intended physical rect (what the deferred Reposition does once
#    the animation completes): the pinned top must not move, the size must be exact.
$wantW = 420
$wantH = 560
[void][AnWin]::SetWindowPos($hwnd, [IntPtr]::Zero, $offX, $r0.Top, $wantW, $wantH, ([AnWin]::SWP_NOZORDER -bor [AnWin]::SWP_NOACTIVATE))
Pump 120
$r2 = Get-Rect $hwnd
$w2 = $r2.Right - $r2.Left
$h2 = $r2.Bottom - $r2.Top
Write-Host ("after settle: ({0},{1}) {2}x{3}" -f $r2.Left, $r2.Top, $w2, $h2)

Check 'top pixel unchanged by the settle placement' ($r2.Top -eq $r0.Top) ("top " + $r0.Top + " -> " + $r2.Top)
Check 'settle placement size exact' (($w2 -eq $wantW) -and ($h2 -eq $wantH)) ("size " + $w2 + "x" + $h2 + ", expected " + $wantW + "x" + $wantH)

# 4) Nothing was stolen: foreground window and mouse are untouched (this script never
#    calls SendInput; the check window is never activated).
$fgAfter = [AnWin]::GetForegroundWindow()
Check 'foreground window unchanged' ($fgAfter -eq $fgBefore) ("hWnd " + $fgBefore + " -> " + $fgAfter)

$win.Close()
Pump 60

if ($failures -gt 0) {
    Write-Host ("anchor animation check: FAILED (" + $failures + ")")
    exit 1
}
Write-Host 'anchor animation check: PASSED (top edge locked, no settle jolt, no focus stolen)'
exit 0
