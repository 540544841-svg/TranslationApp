#requires -Version 5.1
<#
.SYNOPSIS
    译印一键发版：重签更新清单 → 建 tag/Release → 上传附件（GitHub Release，客户端自动更新用）。

.DESCRIPTION
    顺序固定，四步：

    1. 重签更新清单：调 build\make-update-manifest.ps1，用 publish\TranslationApp.exe 的**真实**
       字节数 / SHA-256 / 版本号生成 publish\latest.json。DownloadUrl 由本机 origin 远程地址推导。
       清单不重签 = 签名与产物对不上，客户端会判校验失败，所以这一步不能跳。
    2. 核对清单：解出 payload 与本地 exe 的 SHA-256 / 字节数比对，对不上立刻停（防止拿旧清单发出去）。
    3. 凭据：-Token → 环境变量 GITHUB_TOKEN → git credential fill（本机已存凭据）。
    4. 建 tag 与 Release（已存在则复用，-Force 才覆盖名称/说明与同名附件），再上传附件。
       注意：GitHub 的 Release 附件名不支持非 ASCII，安装包会以 INKSEAL-Setup-<版本>.exe 上传。

.PARAMETER Version
    版本号，须与 csproj 的 <Version> 一致（如 0.3.1）。tag 自动取 v<版本号>。

.PARAMETER Assets
    只传指定附件（相对仓库根或绝对路径）。缺省 = 主程序 exe + latest.json（+ 已存在的安装包）。

.PARAMETER DryRun
    只打印将要执行的步骤与 API 调用，不联网、不重签、不改动仓库。

.EXAMPLE
    pwsh -File build\publish-release.ps1 -Version 0.3.1 -Notes "修复备份恢复可能带回数据的问题。"

.EXAMPLE
    pwsh -File build\publish-release.ps1 -Version 0.3.2 -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Tag = "",

    [string]$Notes = "",

    [string]$Token = "",

    [string]$PrivateKeyPath = "",

    [string]$PublishDir = "",

    [string[]]$Assets = @(),

    [switch]$SkipManifest,

    [switch]$Draft,

    [switch]$Force,

    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishDir) { $PublishDir = Join-Path $repoRoot "publish" }
if (-not $Tag) { $Tag = "v$Version" }
if (-not $PrivateKeyPath) { $PrivateKeyPath = Join-Path $PSScriptRoot "secrets\translationapp-release-private.pem" }

$exe = Join-Path $PublishDir "TranslationApp.exe"
$manifestPath = Join-Path $PublishDir "latest.json"
$setupExe = Join-Path $PublishDir ("setup\译印-Setup-{0}.exe" -f $Version)

# 仓库坐标从 origin 推导，脚本里不写死 owner/repo
$remote = (git -C $repoRoot remote get-url origin).Trim()
if ($remote -notmatch 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$') {
    throw "无法从 origin 解析 GitHub 仓库：$remote"
}
$owner = $Matches['owner']
$repoName = $Matches['repo']
$branch = (git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
$apiBase = "https://api.github.com/repos/$owner/$repoName"
$uploadBase = "https://uploads.github.com/repos/$owner/$repoName/releases"
$downloadUrl = "https://github.com/$owner/$repoName/releases/download/$Tag/TranslationApp.exe"
$manifestUrl = "https://github.com/$owner/$repoName/releases/latest/download/latest.json"
$releaseName = "译印 INKSEAL v$Version"

# 附件名必须 ASCII，否则 GitHub 会把非 ASCII 前缀吞掉（踩过：译印-Setup-0.3.1.exe → -Setup-0.3.1.exe）
function Get-RemoteAssetName([string]$Path, [string]$ReleaseVersion) {
    $n = Split-Path -Leaf $Path
    if ($n -match '^[A-Za-z0-9._-]+$') { return $n }
    if ($n -like '*Setup*') { return "INKSEAL-Setup-$ReleaseVersion.exe" }
    throw "附件名含非 ASCII 且无法自动改名：$n"
}

# ---------- 附件清单 ----------
if (-not $Assets) {
    $Assets = @($exe, $manifestPath)
    if (Test-Path -LiteralPath $setupExe) { $Assets += $setupExe }
}
$assetPlan = foreach ($a in $Assets) {
    $p = if ([IO.Path]::IsPathRooted($a)) { $a } else { Join-Path $repoRoot $a }
    if (-not (Test-Path -LiteralPath $p)) { throw "附件不存在：$p" }
    [pscustomobject]@{
        Path = (Resolve-Path -LiteralPath $p).Path
        Name = Get-RemoteAssetName $p $Version
        Size = (Get-Item -LiteralPath $p).Length
    }
}

# ---------- DryRun：只打印计划 ----------
if ($DryRun) {
    Write-Host "== 发版计划（DryRun，未联网）==" -ForegroundColor Cyan
    Write-Host ("仓库      : {0}/{1}（分支 {2}）" -f $owner, $repoName, $branch)
    Write-Host ("版本      : {0}    tag: {1}" -f $Version, $Tag)
    Write-Host ("Release 名: {0}    draft={1} force={2}" -f $releaseName, [bool]$Draft, [bool]$Force)
    Write-Host ("主程序    : {0}" -f $downloadUrl)
    Write-Host ("客户端清单: {0}" -f $manifestUrl)
    Write-Host "附件："
    $assetPlan | ForEach-Object { Write-Host ("  {0,-34} {1,12:N0} 字节  ← {2}" -f $_.Name, $_.Size, $_.Path) }
    if (-not $SkipManifest) {
        Write-Host ("清单重签  : {0} -File build\make-update-manifest.ps1 -ExePath {1} -Version {2} -DownloadUrl {3}" -f "pwsh", $exe, $Version, $downloadUrl)
    }
    $tokenProbe = if ($Token) { "-Token 已给出" } elseif ($env:GITHUB_TOKEN) { "环境变量 GITHUB_TOKEN" } else { "git credential fill" }
    Write-Host ("凭据来源  : {0}" -f $tokenProbe)
    exit 0
}

# ---------- 1) 重签清单 ----------
if (-not $SkipManifest) {
    if (-not (Test-Path -LiteralPath $exe)) { throw "缺少主程序产物：$exe（先跑 build\publish.ps1）" }
    $manifestScript = Join-Path $PSScriptRoot "make-update-manifest.ps1"
    $margs = @("-NoProfile", "-File", $manifestScript,
        "-ExePath", $exe,
        "-PrivateKeyPath", $PrivateKeyPath,
        "-Version", $Version,
        "-DownloadUrl", $downloadUrl,
        "-OutputPath", $manifestPath)
    if ($Notes) { $margs += @("-Notes", $Notes) }
    $sig = Get-AuthenticodeSignature -LiteralPath $exe
    if ($sig.Status -ne "Valid") {
        $margs += "-AllowUnsigned"
        Write-Warning ("EXE 未通过 Authenticode 校验（{0}），清单按本地联调口径生成。" -f $sig.Status)
    }
    & pwsh @margs
    if ($LASTEXITCODE -ne 0) { throw "更新清单生成失败（退出码 $LASTEXITCODE）" }
}

# ---------- 2) 核对清单与产物一致 ----------
if ((Test-Path -LiteralPath $manifestPath) -and (Test-Path -LiteralPath $exe)) {
    $doc = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($doc.payload)) | ConvertFrom-Json
    $exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    $exeSize = (Get-Item -LiteralPath $exe).Length
    if ($payload.sha256 -ne $exeHash -or [long]$payload.sizeBytes -ne $exeSize) {
        throw ("清单与产物不一致：清单 sha256={0}/{1} 字节，产物 sha256={2}/{3} 字节。请重跑 -SkipManifest 之外的步骤重签。" -f $payload.sha256, $payload.sizeBytes, $exeHash, $exeSize)
    }
    Write-Host ("清单核对通过：{0} / {1:N0} 字节 / keyId={2}" -f $payload.sha256.Substring(0, 16), $payload.sizeBytes, $doc.keyId) -ForegroundColor Green
}

# ---------- 3) 凭据 ----------
$authToken = $Token
if (-not $authToken) { $authToken = $env:GITHUB_TOKEN }
if (-not $authToken) {
    Write-Host "凭据：使用本机 git 已存凭据（git credential fill）" -ForegroundColor DarkGray
    $cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    $authToken = ($cred | Where-Object { $_ -like 'password=*' }) -replace '^password=',''
}
if (-not $authToken) { throw "没有可用凭据：请传 -Token、设置 GITHUB_TOKEN，或先让 git 记住 GitHub 凭据。" }

$headers = @{
    Authorization = "Bearer $authToken"
    Accept        = "application/vnd.github+json"
    "User-Agent"  = "inkseal-release"
}

# 令牌等请求头写进临时文件交给 curl，避免令牌出现在进程命令行里（API 与上传共用这一份）
$script:hdrFile = Join-Path ([IO.Path]::GetTempPath()) ("inkseal-hdr-{0}.hdr" -f ([Guid]::NewGuid().ToString("N")))
[IO.File]::WriteAllText($script:hdrFile, ("Authorization: Bearer {0}`nAccept: application/vnd.github+json`nUser-Agent: inkseal-release`n" -f $authToken))

# curl 不读 WinINET 设置，系统代理要显式带上；这台机器上「直连」和「代理」会交替抖动，两条都试。
$script:proxyUrl = $null
try {
    $inet = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction Stop
    if ($inet.ProxyEnable -eq 1 -and $inet.ProxyServer) {
        $s = $inet.ProxyServer
        if ($s -match '=') {
            $https = ($s -split ';') | Where-Object { $_ -match '^\s*https=' } | Select-Object -First 1
            if ($https) { $s = ($https -split '=', 2)[1] }
        }
        if ($s -and $s -notmatch '^[a-z]+://') { $s = "http://$s" }
        $script:proxyUrl = $s.Trim()
    }
} catch { }

# 统一走 curl 调 GitHub API：直连优先，失败换系统代理，都失败退避重试。
# 4xx（除 429）直接抛，不重试；404 抛 "HTTP 404"，调用方据此判断「tag 不存在」。
function Invoke-Api {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [string]$Method = "Get",
        $Body = $null,
        [int]$Retry = 4,
        [int]$TimeoutSec = 180
    )
    if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw "缺少 curl.exe，无法访问 GitHub API" }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("inkseal-api-{0}" -f ([Guid]::NewGuid().ToString("N")))
    $jsonFile = "$tmp.json"
    $outFile = "$tmp.out"
    if ($null -ne $Body) { [IO.File]::WriteAllText($jsonFile, ($Body | ConvertTo-Json -Compress -Depth 5)) }

    $routes = @(, @())
    if ($script:proxyUrl) { $routes += , @("--proxy", $script:proxyUrl) }

    try {
        for ($i = 1; $i -le $Retry; $i++) {
            foreach ($route in $routes) {
                # curl 的 -X 会把方法名原样发出去，大小写不对（PowerShell 的 "Get"）会被 GitHub
                # 边缘判成异常方法、直接回 403 拦截页。所以方法统一转大写，GET 干脆不加 -X。
                $verb = $Method.ToUpperInvariant()
                $curlArgs = @("-sS", "--connect-timeout", "25", "--max-time", "$TimeoutSec", "-H", "@$script:hdrFile")
                if ($verb -ne "GET") {
                    $curlArgs += @("-X", $verb, "-H", "Content-Type: application/json; charset=utf-8")
                }
                if (Test-Path -LiteralPath $jsonFile) { $curlArgs += @("--data-binary", "@$jsonFile") }
                $curlArgs += $route

                $code = curl.exe @curlArgs -o $outFile -w "%{http_code}" $Uri 2>$null
                $ok = ($LASTEXITCODE -eq 0) -and ($code -match '^\d{3}$')
                if ($ok) {
                    $status = [int]$code
                    $raw = if ([IO.File]::Exists($outFile)) { [IO.File]::ReadAllText($outFile) } else { "" }

                    if ($status -ge 200 -and $status -lt 300) {
                        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
                        return ($raw | ConvertFrom-Json)
                    }

                    # 只有 GitHub API 自己的 JSON 错误响应才算真失败（形如 {"message":"..."}）；
                    # 被边缘拦截/代理异常时返回的是 HTML 拦截页，那种要换另一条链路再试。
                    $isApiError = $raw.TrimStart().StartsWith("{")
                    if ($isApiError -and $status -ne 429 -and $status -ne 404 -and $status -lt 500) {
                        throw ("HTTP {0}：{1} → {2}" -f $status, $Uri, $raw)
                    }
                    if ($isApiError -and $status -eq 404) { throw "HTTP 404：$Uri" }

                    $via = if ($route.Count) { "代理" } else { "直连" }
                    if ($isApiError) { Write-Warning ("HTTP {0}（{1}），稍后重试：{2}" -f $status, $via, $Uri) }
                    else { Write-Warning ("HTTP {0}：链路被拦截（{1}），换链路重试：{2}" -f $status, $via, $Uri) }
                }
                else {
                    $via = if ($route.Count) { "代理" } else { "直连" }
                    Write-Warning ("传输失败（第 {0}/{1} 次，{2}）：{3}" -f $i, $Retry, $via, $Uri)
                }
            }            Start-Sleep -Seconds (2 * $i)
        }
        throw "API 请求连续失败：$Uri"
    }
    finally {
        foreach ($f in @($jsonFile, $outFile)) { if ([IO.File]::Exists($f)) { [IO.File]::Delete($f) } }
    }
}
# ---------- 4) 建/复用 Release ----------
# 用「列 releases 再按 tag 匹配」而不是 /releases/tags/{tag}：草稿（draft）release 不建 tag，
# 用 tags 端点会 404，于是重复发版会不断新建草稿（踩过）。
$rel = $null
try {
    $all = @(Invoke-Api -Uri "$apiBase/releases?per_page=100")
    $rel = $all | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
} catch { $rel = $null }

$body = [ordered]@{ name = $releaseName; body = $Notes; draft = [bool]$Draft; prerelease = $false }
if (-not $rel) {
    $body["tag_name"] = $Tag
    $body["target_commitish"] = $branch
    $rel = Invoke-Api -Uri "$apiBase/releases" -Method Post -Body $body -TimeoutSec 120
    Write-Host ("已创建 Release：{0}（id={1}，tag={2}）" -f $rel.name, $rel.id, $rel.tag_name) -ForegroundColor Green
}
elseif ($Force) {
    $rel = Invoke-Api -Uri "$apiBase/releases/$($rel.id)" -Method Patch -Body $body -TimeoutSec 60
    Write-Host ("已更新 Release 说明：{0}（id={1}）" -f $rel.name, $rel.id) -ForegroundColor Green
}
else {
    Write-Host ("Release 已存在，复用：{0}（id={1}）" -f $rel.name, $rel.id) -ForegroundColor Yellow
}

# ---------- 5) 上传附件 ----------
$existing = @(Invoke-Api -Uri "$apiBase/releases/$($rel.id)/assets" -TimeoutSec 40)
$useCurl = [bool](Get-Command curl.exe -ErrorAction SilentlyContinue)
$headerFile = $null

foreach ($a in $assetPlan) {
    $hit = $existing | Where-Object { $_.name -eq $a.Name } | Select-Object -First 1
    if ($hit -and -not $Force) {
        Write-Host ("跳过（已存在）：{0}" -f $a.Name) -ForegroundColor DarkGray
        continue
    }
    if ($hit) {
        Invoke-Api -Uri "$apiBase/releases/assets/$($hit.id)" -Method Delete -TimeoutSec 40 | Out-Null
        Write-Host ("已删除同名旧附件：{0}" -f $a.Name) -ForegroundColor Yellow
    }

    $url = "$uploadBase/$($rel.id)/assets?name=" + [Uri]::EscapeDataString($a.Name)
    if ($useCurl) {
        # 令牌走同一份临时头文件；直连失败再走系统代理
        $upRoutes = @(, @())
        if ($script:proxyUrl) { $upRoutes += , @("--proxy", $script:proxyUrl) }
        $uploaded = $null
        foreach ($route in $upRoutes) {
            $upArgs = @("-sS", "--retry", "2", "--retry-all-errors", "--retry-delay", "3", "--connect-timeout", "30", "--max-time", "3600",
                "-X", "POST", "-H", "@$script:hdrFile", "-H", "Content-Type: application/octet-stream",
                "--data-binary", "@$($a.Path)") + $route
            Write-Host ("上传中：{0}（{1:N0} 字节）…" -f $a.Name, $a.Size) -ForegroundColor DarkGray
            $raw = curl.exe @upArgs $url
            $o = $raw | ConvertFrom-Json
            if ($o -and $o.id) { $uploaded = $o; break }
            Write-Warning ("上传失败（{0}），换链路重试：{1}" -f $(if ($route.Count) { "代理" } else { "直连" }), $raw)
        }
        if (-not $uploaded) { throw ("上传失败：{0}" -f $a.Name) }
        $o = $uploaded
    }
    else {
        $o = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -InFile $a.Path `
            -ContentType "application/octet-stream" -TimeoutSec 3600
    }
    Write-Host ("已上传：{0}（{1:N0} 字节）" -f $o.name, $o.size) -ForegroundColor Green
}

# ---------- 6) 汇总 ----------
if ($script:hdrFile -and [IO.File]::Exists($script:hdrFile)) { [IO.File]::Delete($script:hdrFile) }
git -C $repoRoot fetch origin --tags 2>$null | Out-Null
Write-Host ""
Write-Host ("发布完成：{0}（{1}）" -f $Tag, $rel.html_url) -ForegroundColor Green
Write-Host ("  客户端更新清单地址：{0}" -f $manifestUrl)
Write-Host ("  版本本体直链      ：{0}" -f $downloadUrl)
Write-Host "  自检建议：换台机器/不带凭据拉一次上面两个地址，核对 latest.json 的 sha256 与 sizeBytes。" -ForegroundColor DarkGray
