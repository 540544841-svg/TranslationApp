# Offscreen UI render harness: renders every settings section plus the quick window
# in both themes (paper/ink) straight to PNG via RenderTargetBitmap.
#
# No window is ever shown, so this steals neither focus nor the mouse - safe to run
# while another instance is in use, and safe for unattended/CI checks.
#
# Usage: powershell -ExecutionPolicy Bypass -File build\render-ui.ps1 [-Exe <path>] [-OutDir <dir>]
# ASCII only: PowerShell 5.1 misparses UTF-8 without BOM.
param(
    [string]$Exe = "$PSScriptRoot\..\src\TranslationApp.App\bin\Debug\net10.0-windows10.0.19041.0\TranslationApp.exe",
    [string]$OutDir = "$PSScriptRoot\..\out\render"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Exe)) { throw "exe not found: $Exe (run: dotnet build src\TranslationApp.App -c Debug)" }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if (-not ('FgWin' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class FgWin {
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public static IntPtr Handle() { return GetForegroundWindow(); }
}
"@
}

$foregroundBefore = [FgWin]::Handle()
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $Exe -ArgumentList @('--render-ui', $OutDir) -WindowStyle Hidden -PassThru
$stamp = Get-Date
$drift = 0
while (-not $proc.HasExited) {
    Start-Sleep -Milliseconds 120
    if ([FgWin]::Handle() -ne $foregroundBefore) { $drift++ }
}
$proc.WaitForExit()
$watch.Stop()

# 只算本次真正写出的图：渲染失败时旧的 png 还在，按总数报会误导
$png = @(Get-ChildItem -LiteralPath $OutDir -Filter *.png -File | Where-Object { $_.LastWriteTime -gt $stamp })
"rendered {0} png in {1:N2}s (exit {2})" -f $png.Count, $watch.Elapsed.TotalSeconds, $proc.ExitCode
"foreground window unchanged: {0} (drift samples: {1})" -f ($drift -eq 0), $drift
"out: $OutDir"
if ($proc.ExitCode -ne 0) { exit $proc.ExitCode }
if ($png.Count -eq 0) { throw "no png written - render aborted (check $env:APPDATA\TranslationApp\logs for the XamlParseException)" }
