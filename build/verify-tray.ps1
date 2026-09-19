#requires -Version 5.1
<#
.SYNOPSIS
    Phase 0 acceptance check: run the published EXE, verify tray startup,
    single-instance rejection, memory footprint, and graceful exit.
    (ASCII-only output so it works under Windows PowerShell 5.1 without BOM.)
#>
[CmdletBinding()]
param(
    [string]$ExePath = ""
)

$ErrorActionPreference = "Continue"
if (-not $ExePath) {
    $ExePath = Join-Path (Split-Path -Parent $PSScriptRoot) "publish\TranslationApp.exe"
}
if (-not (Test-Path $ExePath)) { Write-Host "FAIL: exe not found: $ExePath"; exit 1 }

$logFile = Join-Path $env:APPDATA ("TranslationApp\logs\app-{0}.log" -f (Get-Date -Format "yyyyMMdd"))

# --- 1. First instance: tray resident, no main window ---
$p1 = Start-Process $ExePath -PassThru
Start-Sleep -Seconds 6
$p1.Refresh()
if ($p1.HasExited) {
    Write-Host ("FAIL: first instance exited early, code={0}" -f $p1.ExitCode)
    exit 1
}
Write-Host ("PASS-1: first instance running, PID={0}" -f $p1.Id)
Write-Host ("MEM: WorkingSet={0:N1}MB Private={1:N1}MB" -f ($p1.WorkingSet64 / 1MB), ($p1.PrivateMemorySize64 / 1MB))
Write-Host ("MAINWINDOW: {0} (empty string = tray only)" -f $p1.MainWindowTitle)

if (Test-Path $logFile) {
    Write-Host "PASS-1b: log file exists"
} else {
    Write-Host ("FAIL: log file missing: {0}" -f $logFile)
}

# --- 2. Duplicate instance must be rejected ---
$p2 = Start-Process $ExePath -PassThru
Start-Sleep -Seconds 4
$p2.Refresh()
if ($p2.HasExited) {
    Write-Host ("PASS-2: duplicate instance exited by itself, code={0}" -f $p2.ExitCode)
} else {
    # Expected: blocked on the "already running" MessageBox; log entry proves the mutex check fired.
    Write-Host ("NOTE-2: duplicate still running (blocked on prompt), PID={0}" -f $p2.Id)
    Stop-Process -Id $p2.Id -Force -ErrorAction SilentlyContinue
}

if (Test-Path $logFile) {
    $hit = @(Select-String -Path $logFile -Pattern ("PID " + $p2.Id)).Count
    if ($hit -gt 0) {
        Write-Host ("PASS-2b: duplicate-start log entry found for PID {0}" -f $p2.Id)
    } else {
        Write-Host ("FAIL: no duplicate-start log entry for PID {0}" -f $p2.Id)
    }
}

# --- 3. Graceful exit of first instance via WM_QUIT to its message thread(s) ---
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Win32Msg {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
}
"@

$p1.Refresh()
$exitOk = $false
if ($p1.Threads.Count -gt 0) {
    foreach ($t in $p1.Threads) {
        [void][Win32Msg]::PostThreadMessage([uint32]$t.Id, 0x0012, [IntPtr]::Zero, [IntPtr]::Zero)
    }
    Start-Sleep -Seconds 4
    $p1.Refresh()
    $exitOk = $p1.HasExited
}

if ($exitOk) {
    Write-Host ("PASS-3: first instance exited gracefully, code={0}" -f $p1.ExitCode)
} else {
    Write-Host "WARN-3: no graceful exit after WM_QUIT, force-killing"
    Stop-Process -Id $p1.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

Write-Host "DONE"
