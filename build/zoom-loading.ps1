Add-Type -AssemblyName System.Drawing
$src = New-Object System.Drawing.Bitmap('D:\A\ZCode_my\TranslationApp\artifacts\ui\quick-error-loading.png')
$scale = 3
$w = 400 * $scale
$h = 50 * $scale
$out = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($out)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$rect = New-Object System.Drawing.Rectangle(30, 300, 400, 50)
$dest = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
$g.DrawImage($src, $dest, $rect, [System.Drawing.GraphicsUnit]::Pixel)
$out.Save('D:\A\ZCode_my\TranslationApp\build\probe\loading-zoom.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $out.Dispose(); $src.Dispose()
Write-Host 'saved'
