#requires -Version 5.1
<#
.SYNOPSIS
    生成品牌图标 Assets\app.ico —— 琥珀渐变圆角方块 + 白色闪电（v2.0 品牌资产「琥珀闪电」，
    docs/brand-asset-sheet.html 已于 2026-09-14 经用户确认）。
    多尺寸 16/32/48/256；单母题（一笔闪电）、右倾姿态表达「快」，
    16px 托盘尺寸经剪影测试可读。改品牌资产时才需要重跑本脚本。
#>
[CmdletBinding()]
param(
    [string]$OutFile = ""
)

$ErrorActionPreference = "Stop"
if (-not $OutFile) { $OutFile = Join-Path $PSScriptRoot "..\src\TranslationApp.App\Assets\app.ico" }
Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $gp.AddArc($x, $y, $d, $d, 180, 90)
    $gp.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $gp.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $gp.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $gp.CloseFigure()
    return $gp
}

# 闪电母题（48×48 视框坐标）：上入左出、下入右收的经典一笔闪电
$boltPoints48 = @(
    @{ X = 28.5; Y = 5.5 },
    @{ X = 12.0; Y = 28.5 },
    @{ X = 21.5; Y = 28.5 },
    @{ X = 19.5; Y = 42.5 },
    @{ X = 37.0; Y = 19.5 },
    @{ X = 25.5; Y = 19.5 }
)

function New-IconPng([int]$size, [string]$tmpPath) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # 独占色「琥珀流光」：左上亮 → 右下深的对角渐变（135°）
    $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $c1 = [System.Drawing.Color]::FromArgb(255, 255, 194, 77)   # FFC24D
    $c2 = [System.Drawing.Color]::FromArgb(255, 240, 126, 0)    # F07E00
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($size, $size)), $c1, $c2)

    $r = [single]($size * 0.23)
    $path = New-RoundedRectPath 0 0 $size $size $r
    $g.FillPath($brush, $path)

    # 白色闪电（沿 48 视框等比缩放）
    $scale = $size / 48.0
    $pts = foreach ($p in $boltPoints48) {
        New-Object System.Drawing.PointF([single]($p.X * $scale), [single]($p.Y * $scale))
    }
    $g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.PointF[]]$pts)

    $bmp.Save($tmpPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $brush.Dispose(); $path.Dispose(); $bmp.Dispose()
}

$sizes = @(16, 32, 48, 256)
$tmpPng = Join-Path ([System.IO.Path]::GetTempPath()) "suyi_icon_tmp.png"
$pngs = @{}
foreach ($s in $sizes) {
    New-IconPng $s $tmpPng
    $pngs[$s] = [System.IO.File]::ReadAllBytes($tmpPng)
}
Remove-Item $tmpPng -ErrorAction SilentlyContinue

# 组装 ICO（ICONDIR + ICONDIRENTRY 数组 + PNG 数据）
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write($dim); $bw.Write($dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$s].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngs[$s].Length
}
foreach ($s in $sizes) { $bw.Write([byte[]]$pngs[$s]) }

$icoBytes = $ms.ToArray()
$bw.Dispose()

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
[System.IO.File]::WriteAllBytes($OutFile, [byte[]]$icoBytes)
Write-Host ("图标已生成：{0}（{1} 字节，尺寸 {2}）" -f $OutFile, $icoBytes.Length, ($sizes -join "/")) -ForegroundColor Green
