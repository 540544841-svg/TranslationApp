#requires -Version 5.1
<#
.SYNOPSIS
    译印一键出安装包：主程序单 EXE 作载荷 + 立契安装器 → publish\setup\译印-Setup-<版本>.exe

.DESCRIPTION
    四步，顺序不能换：

    1. 停掉正在运行的译印实例。正在跑的实例会锁住 publish\TranslationApp.exe，新包写不进去；
       更要命的是单实例守卫会让用户双击新 exe 时只唤出旧窗口——看着像「更新了」，其实还是老界面。
    2. 走 build\publish.ps1 出主程序单 EXE（它自己也会先清场，这里是双保险）。
    3. 算主程序的 SHA-256 / 字节数 / 版本，作为 MSBuild 属性传给安装器工程，写进程序集元数据。
       契书上的版本号因此来自真实产物，不是手写常量；安装时重算 SHA-256 比对，对不上就拒绝落印。
    4. 把安装器发布成单文件，重命名成「译印-Setup-<版本>.exe」。

    安装器本身就是一个 EXE：主程序被嵌进去当资源，所以安装包只有一个文件、断网也能装。

.PARAMETER Runtime
    目标运行时，默认 win-x64。

.PARAMETER SkipAppPublish
    跳过主程序发布，直接拿 publish\TranslationApp.exe 现有产物打包（调安装器版面时省时间）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\make-setup.ps1
#>
[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",

    [switch]$SkipAppPublish
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "publish"
$payload = Join-Path $publishDir "TranslationApp.exe"
$setupDir = Join-Path $publishDir "setup"
$staging = Join-Path $repoRoot "out\setup-staging"
$setupProject = Join-Path $repoRoot "src\TranslationApp.Setup\TranslationApp.Setup.csproj"
$setupExeName = "TranslationApp.Setup.exe"

# 递归删除只允许落在仓库内的 out\ 下，避免任何路径拼错导致误删。
function Remove-StagingDirectory([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $guard = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "out"))
    if (-not $full.StartsWith($guard + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除仓库 out\ 之外的目录：$full"
    }

    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

# ── 1 · 清场 ────────────────────────────────────────────────────────────────
$running = @(Get-Process -Name "TranslationApp" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ("停掉正在运行的实例：{0} 个（PID {1}）" -f $running.Count, ($running.Id -join ', ')) -ForegroundColor Yellow
    $running | Stop-Process -Force
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if (@(Get-Process -Name "TranslationApp" -ErrorAction SilentlyContinue).Count -eq 0) { break }
    }
}

# ── 2 · 主程序 ──────────────────────────────────────────────────────────────
if ($SkipAppPublish) {
    Write-Host "跳过主程序发布，直接用现有产物打包。" -ForegroundColor Yellow
}
else {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "publish.ps1") -Configuration Release -Runtime $Runtime
    if ($LASTEXITCODE -ne 0) {
        throw "build\publish.ps1 失败（退出码 $LASTEXITCODE）"
    }
}

if (-not (Test-Path -LiteralPath $payload)) {
    throw "找不到主程序载荷：$payload"
}

# ── 3 · 载荷指纹 ────────────────────────────────────────────────────────────
$payloadItem = Get-Item -LiteralPath $payload
$payloadBytes = $payloadItem.Length
$payloadSha = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash

# ProductVersion 形如 0.3.1+5a3efe2a…（SourceLink 拼的提交号），文件名与契书上只要 0.3.1。
$payloadVersion = $payloadItem.VersionInfo.ProductVersion
if ($payloadVersion) { $payloadVersion = ($payloadVersion -split '\+')[0] }
if (-not $payloadVersion) { $payloadVersion = "1.0.0" }

Write-Host ""
Write-Host "== 译印立契：Configuration=Release Runtime=$Runtime ==" -ForegroundColor Cyan
Write-Host ("载荷：{0}" -f $payload)
Write-Host ("版本：{0} · {1:N1} MB · SHA-256 {2}" -f $payloadVersion, ($payloadBytes / 1MB), $payloadSha)

# ── 4 · 安装器 ──────────────────────────────────────────────────────────────
Remove-StagingDirectory $staging

dotnet publish $setupProject -c Release -r $Runtime --self-contained `
    -p:PublishSingleFile=true `
    "-p:Version=$payloadVersion" `
    "-p:PayloadPath=$payload" `
    "-p:PayloadSha256=$payloadSha" `
    "-p:PayloadVersion=$payloadVersion" `
    "-p:PayloadBytes=$payloadBytes" `
    -o $staging
if ($LASTEXITCODE -ne 0) {
    throw "安装器 dotnet publish 失败（退出码 $LASTEXITCODE）"
}

$built = Join-Path $staging $setupExeName
if (-not (Test-Path -LiteralPath $built)) {
    throw "安装器产物未找到：$built"
}

# 与主程序同一条门禁：单文件发布只许有一个 EXE，多出来的都是漏配。
$unexpected = Get-ChildItem -LiteralPath $staging -File |
    Where-Object { $_.Name -ne $setupExeName }
if ($unexpected) {
    throw "安装器单文件发布出现额外文件：$($unexpected.Name -join ', ')"
}

New-Item -ItemType Directory -Force -Path $setupDir | Out-Null
$setupExe = Join-Path $setupDir ("译印-Setup-{0}.exe" -f $payloadVersion)
Copy-Item -LiteralPath $built -Destination $setupExe -Force

$setupMB = [math]::Round((Get-Item -LiteralPath $setupExe).Length / 1MB, 1)
Write-Host ""
Write-Host ("安装包已出：{0}（{1} MB）" -f $setupExe, $setupMB) -ForegroundColor Green
Write-Host "双击即可立契；收印在「应用和功能」里，或安装器窗口左下角的「收印」。" -ForegroundColor Green
