#requires -Version 7.0
<#
.SYNOPSIS
    生成一组离线发布密钥，用于更新清单 RSA-PSS 签名。

.DESCRIPTION
    私钥只写入指定路径，公钥打印到控制台。将公钥替换到
    src\TranslationApp.Core\Updates\UpdateTrust.cs 的 ReleasePublicKeyPem 后才能用对应私钥发布更新。
    私钥不要放入仓库、网盘或 CI 日志。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PrivateKeyPath,

    [int]$KeySize = 3072,

    [string]$PublicKeyPath = ""
)

$ErrorActionPreference = "Stop"
if ($KeySize -lt 3072) {
    throw "RSA 发布密钥不得小于 3072 位"
}

$privateFullPath = [IO.Path]::GetFullPath($PrivateKeyPath)
$privateDirectory = Split-Path -Parent $privateFullPath
if ($privateDirectory) {
    New-Item -ItemType Directory -Path $privateDirectory -Force | Out-Null
}
if (Test-Path -LiteralPath $privateFullPath) {
    throw "私钥文件已存在，拒绝覆盖：$privateFullPath"
}

$rsa = [System.Security.Cryptography.RSA]::Create($KeySize)
try {
    $privatePem = $rsa.ExportPkcs8PrivateKeyPem()
    $publicPem = $rsa.ExportSubjectPublicKeyInfoPem()
}
finally {
    $rsa.Dispose()
}

[IO.File]::WriteAllText($privateFullPath, $privatePem, [Text.UTF8Encoding]::new($false))
if ($PublicKeyPath) {
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($PublicKeyPath),
        $publicPem,
        [Text.UTF8Encoding]::new($false))
}

Write-Host "私钥已生成：$privateFullPath" -ForegroundColor Yellow
Write-Host "请将下面公钥替换到 UpdateTrust.ReleasePublicKeyPem：" -ForegroundColor Cyan
Write-Host ""
Write-Output $publicPem
