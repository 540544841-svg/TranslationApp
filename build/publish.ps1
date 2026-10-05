#requires -Version 5.1
<#
.SYNOPSIS
    译印一键发布：产出自包含单文件 EXE（需求文档 FR-013 / 阶段 0 发布链路）。

.DESCRIPTION
    执行 dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true，
    产出 publish\TranslationApp.exe，并校验体积是否满足 < 200MB 硬约束。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\publish.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "publish" }
$project = Join-Path $repoRoot "src\TranslationApp.App\TranslationApp.App.csproj"
$exe = Join-Path $OutputDir "TranslationApp.exe"

# 发布前先清场：正在跑的实例会锁住 publish\TranslationApp.exe，新包覆盖不了，用户双击也只能
# 唤出旧窗口（单实例守卫）。所以发布的第一步就是把实例停掉，保证落盘的是最新包。
$running = @(Get-Process -Name "TranslationApp" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ("停掉正在运行的实例：{0} 个（PID {1}）" -f $running.Count, ($running.Id -join ', ')) -ForegroundColor Yellow
    $running | Stop-Process -Force
    # 等文件句柄真正释放，否则紧接着的 dotnet publish -o 会因为目标被占用而失败。
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if (@(Get-Process -Name "TranslationApp" -ErrorAction SilentlyContinue).Count -eq 0) { break }
    }
}

Write-Host "== 译印发布：Configuration=$Configuration Runtime=$Runtime ==" -ForegroundColor Cyan
Write-Host "项目：$project"

dotnet publish $project -c $Configuration -r $Runtime --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutputDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
}

if (-not (Test-Path $exe)) {
    throw "发布产物未找到：$exe"
}

# 兼容旧发布目录：OnnxRuntime 包曾经把 .lib 导入库复制到输出；新目标已从源发布列表剔除。
# 把历史上残留的链接期文件清掉，否则单文件门禁会被旧文件误判。
Get-ChildItem -LiteralPath $OutputDir -File -Filter "onnxruntime*.lib" |
    Remove-Item -Force

$unexpected = Get-ChildItem -LiteralPath $OutputDir -File |
    Where-Object { $_.Name -notin @("TranslationApp.exe", "latest.json") }
if ($unexpected) {
    throw "单文件发布出现额外文件：$($unexpected.Name -join ', ')"
}

$sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host ("发布成功：{0}（{1} MB）" -f $exe, $sizeMB) -ForegroundColor Green

# 体积门禁：2026-09-14 用户决策由 90MB 放宽至 200MB（需求文档 14.3.12.7 / FR-030，第 6 章），
# 为 PaddleOCR 本地高精度 OCR 留预算（RapidOcrNet + ONNX Runtime + SkiaSharp + 嵌入模型 22.5MB）。
# 双口径：默认路径（windows 引擎，不加载 paddle 运行时）实测 ≈ 70.5MB（70,516,894 字节）；
# 启用 paddle 后预计 ≈ 102.6MB（C0 探针实测增量 +32.05MB，单文件压缩率 ≈ 65%）。
if ($sizeMB -gt 200) {
    Write-Warning ("单 EXE 体积 {0} MB 超过 200MB 硬约束，请检查压缩/裁剪选项。" -f $sizeMB)
    exit 2
}
