Add-Type -AssemblyName System.Drawing
$src = New-Object System.Drawing.Bitmap('D:\A\ZCode_my\TranslationApp\artifacts\ui\quick-error-loading.png')
$w = $src.Width
$h = $src.Height
for ($y = 150; $y -lt 360; $y++) {
    $run = 0
    $maxRun = 0
    $bestStart = -1
    $runStart = -1
    for ($x = 30; $x -lt ($w - 10); $x++) {
        $p = $src.GetPixel($x, $y)
        $isGray = ($p.R -gt 228 -and $p.R -lt 248 -and $p.G -gt 230 -and $p.G -lt 250 -and $p.B -gt 232 -and $p.B -lt 252)
        if ($isGray) {
            if ($run -eq 0) { $runStart = $x }
            $run = $run + 1
            if ($run -gt $maxRun) {
                $maxRun = $run
                $bestStart = $runStart
            }
        } else {
            $run = 0
        }
    }
    if ($maxRun -gt 100) {
        $end = $bestStart + $maxRun - 1
        Write-Host "TRACK y=$y run=$maxRun start=$bestStart end=$end"
        for ($dy = -1; $dy -le 1; $dy++) {
            $amber = 0
            $other = 0
            $samples = ""
            for ($x = $bestStart; $x -le $end; $x++) {
                $p = $src.GetPixel($x, ($y + $dy))
                $isWhite = ($p.R -eq 255 -and $p.G -eq 255 -and $p.B -eq 255)
                $isGray = ($p.R -gt 228 -and $p.R -lt 248 -and $p.G -gt 230 -and $p.G -lt 250 -and $p.B -gt 232 -and $p.B -lt 252)
                if (-not $isWhite -and -not $isGray) {
                    $other = $other + 1
                    $hex = $p.R.ToString('X2') + $p.G.ToString('X2') + $p.B.ToString('X2')
                    if ($samples.Length -lt 120) { $samples = $samples + " x" + $x + "=#" + $hex }
                }
                if ($p.R -gt 200 -and $p.G -gt 110 -and $p.G -lt 215 -and $p.B -lt 90) { $amber = $amber + 1 }
            }
            Write-Host "  dy=$dy other=$other amber=$amber$samples"
        }
    }
}
$src.Dispose()
