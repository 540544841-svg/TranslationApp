#requires -Version 7.0
<#
.SYNOPSIS
    为速译发布 EXE 生成 RSA-PSS 签名更新清单。

.DESCRIPTION
    先校验 EXE 的 SHA-256 与 Authenticode 签名，再对清单 payload 做 RSA-PSS(SHA-256) 签名。
    私钥必须只保存在离线发布机，绝不能提交到仓库。默认要求 EXE 已有有效 Authenticode 签名；
    仅供本地联调时可显式传 -AllowUnsigned，但不要用于正式发布。

.EXAMPLE
    pwsh -File build\make-update-manifest.ps1 `
      -ExePath publish\TranslationApp.exe `
      -PrivateKeyPath D:\secure\translationapp-release-private.pem `
      -Version 1.3.0 `
      -DownloadUrl https://example.com/TranslationApp/1.3.0/TranslationApp.exe `
      -OutputPath publish\latest.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [Parameter(Mandatory = $true)]
    [string]$PrivateKeyPath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$DownloadUrl,

    [string]$FileName = "",

    [string]$MinimumVersion = "",

    [switch]$Mandatory,

    [string]$Notes = "",

    [string]$KeyId = "release-2026-02",

    [string]$OutputPath = "latest.json",

    [switch]$SkipTrustKeyCheck,

    [switch]$AllowUnsigned
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^v?\d+(\.\d+){0,3}([-+][0-9A-Za-z.-]+)?$') {
    throw "Version 格式不正确：$Version"
}
if ($MinimumVersion -and $MinimumVersion -notmatch '^v?\d+(\.\d+){0,3}([-+][0-9A-Za-z.-]+)?$') {
    throw "MinimumVersion 格式不正确：$MinimumVersion"
}

$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
$file = Get-Item -LiteralPath $resolvedExe
if (-not $FileName) {
    $FileName = $file.Name
}
if ($FileName -ne [IO.Path]::GetFileName($FileName) -or $FileName -notmatch '\.exe$') {
    throw "FileName 必须是不含目录的 .exe 文件名：$FileName"
}

$uri = [Uri]$DownloadUrl
$isLoopbackHttp = $uri.Scheme -eq "http" -and ($uri.IsLoopback -or $uri.Host -eq "localhost")
if ($uri.Scheme -ne "https" -and -not $isLoopbackHttp) {
    throw "DownloadUrl 必须使用 HTTPS（本机回环测试地址除外）"
}

$signature = Get-AuthenticodeSignature -LiteralPath $resolvedExe
if ($signature.Status -ne "Valid") {
    if (-not $AllowUnsigned) {
        throw "EXE 的 Authenticode 签名无效：$($signature.Status)。正式发布必须先签名，或仅供联调时传 -AllowUnsigned。"
    }
    Write-Warning "EXE 未通过 Authenticode 校验，本次仅生成联调清单：$($signature.Status)"
}
else {
    Write-Host "Authenticode：Valid，签名者 $($signature.SignerCertificate.Subject)" -ForegroundColor Green
}

$hash = (Get-FileHash -LiteralPath $resolvedExe -Algorithm SHA256).Hash
$payloadObject = [ordered]@{
    schemaVersion = 1
    version = $Version
    downloadUrl = $DownloadUrl
    sha256 = $hash
    sizeBytes = [long]$file.Length
    fileName = $FileName
    minimumVersion = $MinimumVersion
    mandatory = [bool]$Mandatory
    notes = $Notes
}
$payloadJson = $payloadObject | ConvertTo-Json -Depth 5 -Compress
$payloadBytes = [Text.Encoding]::UTF8.GetBytes($payloadJson)

$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem((Get-Content -LiteralPath $PrivateKeyPath -Raw))
    if (-not $SkipTrustKeyCheck) {
        $trustPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\TranslationApp.Core\Updates\UpdateTrust.cs'
        $trustSource = Get-Content -LiteralPath $trustPath -Raw
        $publicKeyMatch = [regex]::Match(
            $trustSource,
            'ReleasePublicKeyPem\s*=\s*"""\s*(?<pem>-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----)\s*"""',
            [Text.RegularExpressions.RegexOptions]::Singleline)
        if (-not $publicKeyMatch.Success) {
            throw "无法从 UpdateTrust.cs 读取内置公钥"
        }

        # 比完整性而非比排版：C# 原始字符串字面量在源码里带缩进（运行时已被剔除），
        # 因此剔除全部空白字符后再比对，否则缩进差异会让这项校验恒失败。
        $trustedPem = ($publicKeyMatch.Groups['pem'].Value -replace '\s', '')
        $derivedPem = ($rsa.ExportSubjectPublicKeyInfoPem() -replace '\s', '')
        if ($trustedPem -ne $derivedPem) {
            throw "私钥与程序内置发布公钥不匹配；请先用 new-update-signing-key.ps1 轮换密钥并重建程序。"
        }
    }
    $signatureBytes = $rsa.SignData(
        $payloadBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pss)
}
finally {
    $rsa.Dispose()
}

$envelope = [ordered]@{
    keyId = $KeyId
    payload = [Convert]::ToBase64String($payloadBytes)
    signature = [Convert]::ToBase64String($signatureBytes)
}
$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
$json = $envelope | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText(
    [IO.Path]::GetFullPath($OutputPath),
    $json,
    [Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "更新清单已生成：$([IO.Path]::GetFullPath($OutputPath))" -ForegroundColor Green
Write-Host "版本：$Version"
Write-Host "SHA-256：$hash"
Write-Host "大小：$($file.Length) 字节"
Write-Host "KeyId：$KeyId"
