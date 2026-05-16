Add-Type -AssemblyName System.Drawing
$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::FromArgb(255, 12, 18, 29))
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 56, 189, 248))
$points = @(
    [System.Drawing.Point]::new(128, 44),
    [System.Drawing.Point]::new(152, 104),
    [System.Drawing.Point]::new(216, 104),
    [System.Drawing.Point]::new(166, 142),
    [System.Drawing.Point]::new(186, 206),
    [System.Drawing.Point]::new(128, 168),
    [System.Drawing.Point]::new(70, 206),
    [System.Drawing.Point]::new(90, 142),
    [System.Drawing.Point]::new(40, 104),
    [System.Drawing.Point]::new(104, 104)
)
$g.FillPolygon($brush, $points)
$icoPath = Join-Path $PSScriptRoot "app.ico"
$icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$stream = [System.IO.File]::Open($icoPath, [System.IO.FileMode]::Create)
$icon.Save($stream)
$stream.Close()
$pngPath = Join-Path $PSScriptRoot "app.png"
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Host "Created $icoPath"
