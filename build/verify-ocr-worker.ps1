#requires -Version 7.0
<#
.SYNOPSIS
    「OCR 原生引擎进程隔离」的人工验收探针（不在 CI 里跑：需要真实 EXE 与本机 ONNX 运行时）。

.DESCRIPTION
    用真实的 TranslationApp.exe 走一遍隔离链路：
      1) 以 `--ocr-worker <管道名>` 自启动子进程（主进程启动方式的同一入口）；
      2) 命名管道握手（hello），确认「起得来、说得上话」；
      3) -Recognize 时再发一张 8x8 全白 BGRA 图，跑通像素载荷 + 推理 + 结果回传（会加载模型，约数秒）；
      4) shutdown 后确认子进程**自行退出**（管道断开即收尾，不需要 kill）。

.EXAMPLE
    pwsh -File build\verify-ocr-worker.ps1 -Exe publish\TranslationApp.exe -Recognize
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [switch]$Recognize
)

$ErrorActionPreference = "Stop"
$exePath = (Resolve-Path -LiteralPath $Exe).Path
$pipe = "TranslationApp.OcrWorker.verify" + [Guid]::NewGuid().ToString("N")

# 与 OcrWorkerLauncher.OpenAsync 同一套启动参数（UseShellExecute=false + ArgumentList + CreateNoWindow）
$startInfo = [System.Diagnostics.ProcessStartInfo]::new($exePath)
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.ArgumentList.Add('--ocr-worker')
$startInfo.ArgumentList.Add($pipe)
$proc = [System.Diagnostics.Process]::Start($startInfo)
Write-Host "已启动隔离进程：PID $($proc.Id)，管道 $pipe"

function Read-Exactly([System.IO.Stream]$stream, [int]$count) {
    $buffer = New-Object byte[] $count
    $read = 0
    while ($read -lt $count) {
        $got = $stream.Read($buffer, $read, $count - $read)
        if ($got -le 0) { throw "对端提前关闭（已读 $read/$count 字节）" }
        $read += $got
    }
    return $buffer
}

function Write-Frame([System.IO.Stream]$stream, [string]$json) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $stream.Write([BitConverter]::GetBytes([int]$bytes.Length), 0, 4)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}

function Read-Frame([System.IO.Stream]$stream) {
    $length = [BitConverter]::ToInt32((Read-Exactly $stream 4), 0)
    if ($length -le 0 -or $length -gt 1MB) { throw "帧长度非法：$length" }
    return [Text.Encoding]::UTF8.GetString((Read-Exactly $stream $length))
}

$client = $null
try {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $pipe, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    $cts = [System.Threading.CancellationTokenSource]::new(15000)
    $client.ConnectAsync($cts.Token).GetAwaiter().GetResult()
    Write-Host "已连接。" -ForegroundColor Green

    Write-Frame $client '{"kind":"hello"}'
    Write-Host "握手应答：$(Read-Frame $client)" -ForegroundColor Green

    if ($Recognize) {
        $w = 8; $h = 8
        $pixels = New-Object byte[] ($w * $h * 4)
        for ($i = 0; $i -lt $pixels.Length; $i++) { $pixels[$i] = 0xFF }
        Write-Frame $client "{`"kind`":`"recognize`",`"width`":$w,`"height`":$h,`"pixels`":$($pixels.Length),`"languageTag`":`"auto`"}"
        $client.Write($pixels, 0, $pixels.Length)
        $client.Flush()
        Write-Host "识别应答：$(Read-Frame $client)" -ForegroundColor Green
    }

    Write-Frame $client '{"kind":"shutdown"}'
    Write-Host "退出应答：$(Read-Frame $client)"
}
finally {
    if ($client) { $client.Dispose() }
    if (-not $proc.WaitForExit(10000)) {
        Write-Host "隔离进程未在 10 秒内自行退出，强制结束。" -ForegroundColor Yellow
        $proc.Kill()
        exit 1
    }
    Write-Host "隔离进程已自行退出（退出码 $($proc.ExitCode)）。" -ForegroundColor Green
    $proc.Dispose()
}
