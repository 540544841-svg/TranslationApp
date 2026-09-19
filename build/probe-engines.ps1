# Reachability probe for the official engines (doc 13.9 items 5/6/7/8).
# Sends a deliberately credential-less request: HTTP 401/403/456 means the host is
# reachable and answering (expected), a timeout / DNS failure means it is NOT reachable.
# ASCII only: PowerShell 5.1 misparses UTF-8 without BOM.
param(
    [int]$TimeoutSec = 12
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Net.Http

$targets = @(
    @{ Name = 'Azure Translator'; Method = 'POST'; Url = 'https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=zh-Hans'
       Body = '[{"Text":"hello"}]'; ContentType = 'application/json' },
    @{ Name = 'DeepL (free)';     Method = 'POST'; Url = 'https://api-free.deepl.com/v2/translate'
       Body = '{"text":["hello"],"target_lang":"ZH"}'; ContentType = 'application/json' },
    @{ Name = 'DeepL (paid)';     Method = 'POST'; Url = 'https://api.deepl.com/v2/translate'
       Body = '{"text":["hello"],"target_lang":"ZH"}'; ContentType = 'application/json' },
    @{ Name = 'DeepL (portal)';   Method = 'GET';  Url = 'https://www.deepl.com/pro-api'
       Body = $null; ContentType = $null }
)

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseProxy = $false
$handler.AllowAutoRedirect = $true
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)

foreach ($t in $targets) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $status = ''
    $note = ''
    try {
        if ($t.Method -eq 'GET') {
            $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $t.Url)
        } else {
            $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, $t.Url)
            $content = New-Object System.Net.Http.StringContent($t.Body, [System.Text.Encoding]::UTF8, $t.ContentType)
            $req.Content = $content
        }
        $resp = $client.SendAsync($req).GetAwaiter().GetResult()
        $status = [int]$resp.StatusCode
        $note = $resp.ReasonPhrase
        $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($body.Length -gt 220) { $body = $body.Substring(0, 220) }
        $body = $body -replace '\s+', ' '
        $resp.Dispose()
        $req.Dispose()
        Write-Output ("{0} | {1} | HTTP {2} {3} | {4} ms | {5}" -f $t.Name, $t.Url, $status, $note, $sw.ElapsedMilliseconds, $body)
    } catch {
        Write-Output ("{0} | {1} | ERROR | {2} ms | {3}" -f $t.Name, $t.Url, $sw.ElapsedMilliseconds, $_.Exception.Message)
    }
}

$client.Dispose()
Write-Output 'probe-done'
