Add-Type -AssemblyName System.Drawing
$src = New-Object System.Drawing.Bitmap('D:\A\ZCode_my\TranslationApp\artifacts\ui\quick-error-loading.png')
$bestY = -1; $bestN = 0
for ($y = 310; $y -lt 340; $y++) {
    $n = 0
    for ($x = 40; $x -lt 420; $x++) {
        $p = $src.GetPixel($x, $y)
        if ($p.R -gt 235 -and $p.G -gt 238 -and $p.B -gt 240 -and $p.B -le 250) { $n++ }
    }
    if ($n -gt $bestN) { $bestN = $n; $bestY = $y }
}
Write-Host ("track row: y={0} lightpx={1}" -f $bestY, $bestN)

if ($bestY -ge 0) {
    for ($y = $bestY - 1; $y -le $bestY + 1; $y++) {
        $line = "y=$y :"
        for ($x = 40; $x -lt 425; $x++) {
            $p = $src.GetPixel($x, $y)
            if ($p.R -eq 255 -and $p.G -eq 255 -and $p.B -eq 255) { continue }
            $line += " x$x=#" + $p.R.ToString('X2') + $p.G.ToString('X2') + $p.B.ToString('X2')
        }
        Write-Host $line
    }
}
$src.Dispose()
