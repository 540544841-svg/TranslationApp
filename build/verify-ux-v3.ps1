# End-to-end UI verification for onboarding, Doctor, and in-place replacement.
# Usage: pwsh -File build/verify-ux-v3.ps1 [-Exe <path>] [-OutDir <path>]
param(
    [string]$Exe = "$PSScriptRoot/../publish/TranslationApp.exe",
    [string]$OutDir = "$PSScriptRoot/../artifacts/ux-v3"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Drawing.Common
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class WindowNative {
    [StructLayout(LayoutKind.Sequential)] private struct RECT {
        public int Left; public int Top; public int Right; public int Bottom;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, bool unknown);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void keybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    public static int[] GetBounds(IntPtr hWnd) {
        RECT rect;
        if (!GetWindowRect(hWnd, out rect)) return Array.Empty<int>();
        return new[] { rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top };
    }

    public static bool Print(IntPtr hWnd, IntPtr hdc) => PrintWindow(hWnd, hdc, 3);

    public static bool Focus(IntPtr hWnd) {
        ShowWindow(hWnd, 9);
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(hWnd, out _);
        var attached = foregroundThread != 0 && targetThread != 0 &&
            AttachThreadInput(foregroundThread, targetThread, true);
        try {
            BringWindowToTop(hWnd);
            SetFocus(hWnd);
            // A synthetic Alt up/down releases the foreground lock before requesting focus.
            keybdEvent(0x12, 0, 0, UIntPtr.Zero);
            keybdEvent(0x12, 0, 0x0002, UIntPtr.Zero);
            SwitchToThisWindow(hWnd, true);
            SetForegroundWindow(hWnd);
            return GetForegroundWindow() == hWnd;
        } finally {
            if (attached) AttachThreadInput(foregroundThread, targetThread, false);
        }
    }

    public static bool IsForeground(IntPtr hWnd) => GetForegroundWindow() == hWnd;
}
"@

if (-not (Test-Path -LiteralPath $Exe)) {
    throw "EXE not found: $Exe"
}

if (Get-Process -Name TranslationApp -ErrorAction SilentlyContinue) {
    throw 'TranslationApp is already running; close it before this isolated UI verification.'
}

$Exe = (Resolve-Path -LiteralPath $Exe).Path
if (-not [IO.Path]::IsPathRooted($OutDir)) {
    $OutDir = Join-Path (Get-Location).Path $OutDir
}
$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

$exeDirectory = Split-Path -Parent $Exe
$dataDirectory = Join-Path $exeDirectory 'data'
$settingsFile = Join-Path $dataDirectory 'settings.json'
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null

# Minimal JSON is enough: omitted fields deserialize to AppDefaults.
$settings = [ordered]@{
    OnboardingShown           = $false
    ReplaceSelectionEnabled   = $true
    ReplaceWritesHistory      = $true
    HotkeyReplaceTranslate    = 'Alt+J'
    PrivacyMode               = $false
    Engine                     = 'bing'
    SourceLanguage             = 'auto'
    TargetLanguage             = 'zh-CN'
    UpdateAutoCheck            = $false
}
$settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $settingsFile -Encoding utf8

function Wait-For {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSeconds = 15,
        [string]$Message = 'Timed out'
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $value = & $Condition
        if ($value) {
            return $value
        }
        Start-Sleep -Milliseconds 150
    }
    throw $Message
}

function Find-Descendant {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.AutomationProperty]$Property,
        [object]$Value
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new($Property, $Value)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Find-Window {
    param([int]$ProcessId, [string]$Title)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)
    foreach ($window in $windows) {
        if ($window.Current.Name -eq $Title -and $window.Current.IsOffscreen -eq $false) {
            return $window
        }
    }
    return $null
}

function Find-ProcessWindow {
    param([int]$ProcessId)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
    foreach ($window in $windows) {
        if ($window.Current.IsOffscreen -eq $false) {
            return $window
        }
    }
    return $null
}

function Find-Button {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Text
    )

    $byId = Find-Descendant $Root ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) $Text
    if ($byId) {
        return $byId
    }

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($button in $buttons) {
        if ($button.Current.Name -eq $Text) {
            return $button
        }

        $textCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Text)
        $text = $button.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
        if ($text) {
            return $button
        }
    }
    return $null
}

function Invoke-Button {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Text
    )

    $button = Wait-For -TimeoutSeconds 10 -Message "Button not found: $Text" -Condition {
        Find-Button $Root $Text
    }
    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Select-Tab {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Text
    )

    $tab = Wait-For -TimeoutSeconds 10 -Message "Tab not found: $Text" -Condition {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Text)
        $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    $pattern = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
}

function Save-ElementImage {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Path
    )

    $handle = [IntPtr]$Element.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) {
        throw "Element has no native window: $($Element.Current.Name)"
    }

    $bounds = [WindowNative]::GetBounds($handle)
    if ($bounds.Count -ne 4 -or $bounds[2] -le 0 -or $bounds[3] -le 0) {
        throw "Window has invalid bounds: $($Element.Current.Name)"
    }

    $bitmap = [Drawing.Bitmap]::new($bounds[2], $bounds[3])
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [WindowNative]::Print($handle, $hdc)) {
                throw "PrintWindow failed: $($Element.Current.Name)"
            }
        }
        finally {
            $graphics.ReleaseHdc($hdc)
        }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Get-AllText {
    param([System.Windows.Automation.AutomationElement]$Root)

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $elements = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    @($elements | ForEach-Object { $_.Current.Name })
}

function Get-WindowTextContent {
    param([System.Windows.Automation.AutomationElement]$Root)

    $parts = [Collections.Generic.List[string]]::new()
    $types = @(
        [System.Windows.Automation.ControlType]::Document,
        [System.Windows.Automation.ControlType]::Edit,
        [System.Windows.Automation.ControlType]::Text)

    foreach ($type in $types) {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $type)
        $elements = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        foreach ($element in $elements) {
            $pattern = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$pattern)) {
                $parts.Add($pattern.DocumentRange.GetText(-1))
            }

            $pattern = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
                $parts.Add($pattern.Value)
            }

            if ($element.Current.Name) {
                $parts.Add($element.Current.Name)
            }
        }
    }

    return ($parts -join "`n")
}

$app = $null
$notepad = $null
$previousPortable = $env:TRANSLATIONAPP_PORTABLE
$steps = [Collections.Generic.List[string]]::new()

try {
    $env:TRANSLATIONAPP_PORTABLE = '1'
    $app = Start-Process -FilePath $Exe -ArgumentList '--settings' -PassThru
    if (-not $app) {
        throw 'Failed to start TranslationApp.'
    }

    $settingsWindow = Wait-For -TimeoutSeconds 15 -Message 'Settings window did not open.' -Condition {
        Find-Window $app.Id '译印 INKSEAL · 设置'
    }
    $steps.Add('settings-window')

    $guide = Wait-For -TimeoutSeconds 15 -Message 'First-run guide did not open.' -Condition {
        Find-Window $app.Id '译印 · 首次使用引导'
    }
    Save-ElementImage $guide (Join-Path $OutDir '01-guide-engine.png')
    $steps.Add('guide-step-1')

    Invoke-Button $guide 'NextButton'
    Start-Sleep -Milliseconds 400
    $guide = Wait-For -TimeoutSeconds 5 -Message 'Guide step 2 did not open.' -Condition {
        Find-Window $app.Id '译印 · 首次使用引导'
    }
    Save-ElementImage $guide (Join-Path $OutDir '02-guide-hotkey.png')
    $steps.Add('guide-step-2')

    $guideInputHotkey = Wait-For -TimeoutSeconds 5 -Message 'Guide input hotkey editor was not found.' -Condition {
        Find-Descendant $guide ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) 'InputHotkeyBox'
    }
    $guideInputHotkey.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^%{F11}')
    Wait-For -TimeoutSeconds 5 -Message 'Guide hotkey recording did not capture Ctrl+Alt+F11.' -Condition {
        (($guideInputHotkey.Current.Name + "`n") + ((Get-AllText $guide) -join "`n")) -like '*F11*'
    }
    Save-ElementImage $guide (Join-Path $OutDir '02-guide-hotkey-recorded.png')
    $steps.Add('guide-hotkey-recorded')

    Invoke-Button $guide 'NextButton'
    $nextButton = Wait-For -TimeoutSeconds 8 -Message 'Guide self-check did not finish.' -Condition {
        $candidate = Find-Descendant $guide ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) 'NextButton'
        if (-not $candidate) { return $null }
        $name = $candidate.Current.Name
        if ($name -in @('完成', '重新检查')) { return $candidate }
        return $null
    }
    Save-ElementImage $guide (Join-Path $OutDir '03-guide-check.png')
    $steps.Add('guide-step-3')

    if ($nextButton.Current.Name -ne '完成') {
        throw "Guide self-check reported failures; next button was '$($nextButton.Current.Name)'."
    }

    $nextButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For -TimeoutSeconds 8 -Message 'First-run guide did not close.' -Condition {
        -not (Find-Window $app.Id '译印 · 首次使用引导')
    }
    $steps.Add('guide-completed')

    $settingsWindow = Wait-For -TimeoutSeconds 8 -Message 'Settings window became unavailable after onboarding.' -Condition {
        Find-Window $app.Id '译印 INKSEAL · 设置'
    }
    Select-Tab $settingsWindow '热键'
    Start-Sleep -Milliseconds 400
    $selectHotkey = Wait-For -TimeoutSeconds 5 -Message 'Settings select-hotkey editor was not found.' -Condition {
        Find-Descendant $settingsWindow ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) 'SelectHotkeyEditor'
    }
    $selectHotkey.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^%{F10}')
    Wait-For -TimeoutSeconds 5 -Message 'Settings hotkey recording did not capture Ctrl+Alt+F10.' -Condition {
        ((Get-AllText $settingsWindow) -join "`n") -like '*F10*'
    }
    Save-ElementImage $settingsWindow (Join-Path $OutDir '06-settings-hotkey-recorded.png')
    Invoke-Button $settingsWindow 'ResetSelectHotkeyButton'
    Wait-For -TimeoutSeconds 5 -Message 'Per-hotkey reset did not restore Alt+S.' -Condition {
        $text = (Get-AllText $settingsWindow) -join "`n"
        $text -like '*Alt*' -and $text -like '*S*' -and $text -notlike '*F10*'
    }
    $steps.Add('settings-hotkey-edited-and-reset')

    $settingsWindow = Wait-For -TimeoutSeconds 8 -Message 'Settings window became unavailable.' -Condition {
        Find-Window $app.Id '译印 INKSEAL · 设置'
    }
    Select-Tab $settingsWindow '诊断'
    Start-Sleep -Milliseconds 500
    Save-ElementImage $settingsWindow (Join-Path $OutDir '04-doctor-before.png')

    Invoke-Button $settingsWindow '开始诊断'
    $steps.Add('doctor-started')

    Wait-For -TimeoutSeconds 45 -Message 'Doctor did not finish.' -Condition {
        $texts = Get-AllText $settingsWindow
        ($texts | Where-Object { $_ -like '诊断完成于*' -or $_ -like '诊断未能完成*' }).Count -gt 0
    }
    Save-ElementImage $settingsWindow (Join-Path $OutDir '05-doctor-results.png')
    $steps.Add('doctor-completed')

    $targetScript = Join-Path $PSScriptRoot 'ux-replace-target.ps1'
    $notepad = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile', '-File', $targetScript) -PassThru
    $notepadWindow = Wait-For -TimeoutSeconds 15 -Message 'Replacement target window did not open.' -Condition {
        return Find-ProcessWindow $notepad.Id
    }
    $notepad = Get-Process -Id $notepadWindow.Current.ProcessId
    $notepadHandle = [IntPtr]$notepadWindow.Current.NativeWindowHandle
    Wait-For -TimeoutSeconds 8 -Message 'Replacement target could not take foreground focus.' -Condition {
        [void][WindowNative]::Focus($notepadHandle)
        return [WindowNative]::IsForeground($notepadHandle)
    }
    Start-Sleep -Milliseconds 300
    $targetContent = Get-WindowTextContent $notepadWindow
    if ($targetContent -notlike '*Hello world*') {
        throw "Replacement target content is unavailable; UIA content: '$targetContent'"
    }
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Save-ElementImage $notepadWindow (Join-Path $OutDir '06-target-selected.png')
    [System.Windows.Forms.SendKeys]::SendWait('%j')
    $steps.Add('replace-hotkey-sent')

    Wait-For -TimeoutSeconds 20 -Message 'Direct replacement did not overwrite the selected source text.' -Condition {
        $replacedContent = Get-WindowTextContent $notepadWindow
        if ([string]::IsNullOrWhiteSpace($replacedContent)) { return $false }
        return $replacedContent -notlike '*Hello world*'
    }

    if ((Find-Window $app.Id '确认原位替换') -or (Find-Window $app.Id '原位替换完成')) {
        throw 'Replacement unexpectedly opened a confirmation or result window.'
    }
    $steps.Add('replace-direct')
    Save-ElementImage $notepadWindow (Join-Path $OutDir '07-replace-direct.png')

    [System.Windows.Forms.SendKeys]::SendWait('^z')
    Wait-For -TimeoutSeconds 8 -Message 'Target app Ctrl+Z did not restore the original text.' -Condition {
        (Get-WindowTextContent $notepadWindow) -like '*Hello world*'
    }
    $steps.Add('replace-undone')
    Save-ElementImage $notepadWindow (Join-Path $OutDir '08-replace-undone.png')

    Write-Output "PASS: $($steps -join ' -> ')"
    Write-Output "Screenshots: $OutDir"
}
finally {
    if ($previousPortable -eq $null) {
        Remove-Item Env:TRANSLATIONAPP_PORTABLE -ErrorAction SilentlyContinue
    }
    else {
        $env:TRANSLATIONAPP_PORTABLE = $previousPortable
    }

    if ($notepad -and -not $notepad.HasExited) {
        Stop-Process -Id $notepad.Id -Force -ErrorAction SilentlyContinue
    }
    if ($app -and -not $app.HasExited) {
        Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    }
}
