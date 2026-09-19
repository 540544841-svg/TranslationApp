# 签名测试向量的独立参考实现：TC3-HMAC-SHA256（腾讯云）+ MD5（百度）。
# 仅用于推算单元测试里硬编码的期望值，不属于产品代码。
# 用 .NET 原生密码学从头实现一遍，与 C# 产品代码相互独立，两者对得上才说明实现无误。
# 其中“腾讯云官方文档示例”的输出与腾讯云文档公布的签名一致，可交叉验证算法理解正确。
#
# 用法：powershell -ExecutionPolicy Bypass -File build\compute-signature-vectors.ps1
$ErrorActionPreference = "Stop"

function Get-Sha256Hex([string]$s) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($s)
    return ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLower()
}

function Get-Hmac([byte[]]$key, [string]$data) {
    $h = New-Object System.Security.Cryptography.HMACSHA256
    $h.Key = $key
    return $h.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($data))
}

function Get-Tc3([string]$secretId, [string]$secretKey, [string]$service, [string]$hostName, [string]$contentType, [string]$payload, [long]$ts) {
    $date = [System.DateTimeOffset]::FromUnixTimeSeconds($ts).UtcDateTime.ToString("yyyy-MM-dd")
    $canonicalRequest = @("POST", "/", "", "content-type:$contentType", "host:$hostName", "", "content-type;host", (Get-Sha256Hex $payload)) -join "`n"
    $scope = "$date/$service/tc3_request"
    $stringToSign = @("TC3-HMAC-SHA256", "$ts", $scope, (Get-Sha256Hex $canonicalRequest)) -join "`n"

    $kDate = Get-Hmac ([System.Text.Encoding]::UTF8.GetBytes("TC3" + $secretKey)) $date
    $kService = Get-Hmac $kDate $service
    $kSigning = Get-Hmac $kService "tc3_request"
    $signature = ([System.BitConverter]::ToString((Get-Hmac $kSigning $stringToSign)) -replace '-', '').ToLower()

    Write-Host "  date             = $date"
    Write-Host "  unixSeconds      = $ts"
    Write-Host "  sha256(payload)  = $(Get-Sha256Hex $payload)"
    Write-Host "  canonicalRequest = $($canonicalRequest -replace "`n", '\n')"
    Write-Host "  stringToSign     = $($stringToSign -replace "`n", '\n')"
    Write-Host "  signature        = $signature"
    Write-Host "  authorization    = TC3-HMAC-SHA256 Credential=$secretId/$scope, SignedHeaders=content-type;host, Signature=$signature"
    Write-Host ""
}

Write-Host "===== A. 腾讯云官方文档示例（service=cvm，用于验证参考实现本身是否正确）====="
$payloadCvm = '{"Limit": 1, "Filters": [{"Values": ["\u672a\u547d\u540d"], "Name": "instance-name"}]}'
Get-Tc3 "AKIDz8krbsJ5yKBZQpn74WFkmLPx3*******" "Gu5t9xGARNpq86cd98joQYCN3*******" "cvm" "cvm.tencentcloudapi.com" "application/json; charset=utf-8" $payloadCvm 1551113065

Write-Host "===== B. 本项目 TMT 引擎固定向量（service=tmt）====="
$payloadTmt = '{"SourceText":"hello","Source":"en","Target":"zh","ProjectId":0}'
Get-Tc3 "AKIDEXAMPLE1234567890" "SECRETKEYEXAMPLE0987654321" "tmt" "tmt.tencentcloudapi.com" "application/json; charset=utf-8" $payloadTmt 1735689600

Write-Host "===== C. 百度签名固定向量 ====="
function Get-Md5Hex([string]$s) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    return ([System.BitConverter]::ToString($md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($s))) -replace '-', '').ToLower()
}
Write-Host ("  appid=20250101000000001 q='hello' salt=Ab3xY9kL2mQ7 key=Kx7Yq2Ws9Zn4 => sign=" + (Get-Md5Hex "20250101000000001helloworldAb3xY9kL2mQ7Kx7Yq2Ws9Zn4"))
Write-Host ("  appid=20250101000000001 q='hello' salt=Ab3xY9kL2mQ7 key=Kx7Yq2Ws9Zn4 => sign=" + (Get-Md5Hex ("20250101000000001" + "hello" + "Ab3xY9kL2mQ7" + "Kx7Yq2Ws9Zn4")))
Write-Host ("  appid=20250101000000001 q='hello world' salt=salt1234 key=Kx7Yq2Ws9Zn4 => sign=" + (Get-Md5Hex ("20250101000000001" + "hello world" + "salt1234" + "Kx7Yq2Ws9Zn4")))
Write-Host ("  appid=20250101000000001 q='你好，世界' salt=Zz9Aa1Bb2Cc3 key=Kx7Yq2Ws9Zn4 => sign=" + (Get-Md5Hex ("20250101000000001" + "你好，世界" + "Zz9Aa1Bb2Cc3" + "Kx7Yq2Ws9Zn4")))
