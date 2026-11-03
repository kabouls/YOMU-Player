param(
    [string]$SourcePng = (Join-Path $PSScriptRoot '..\src\YomuPlayer.Desktop\Assets\app_logo.png'),
    [string]$OutputIcon = (Join-Path $PSScriptRoot '..\src\YomuPlayer.Desktop\Assets\app-nightly.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-NightlyBitmap([int]$size) {
    $base = [System.Drawing.Image]::FromFile($SourcePng)
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($base, 0, 0, $size, $size)

    $r = [Math]::Max(6, [int]($size * 0.38))
    $x = $size - $r - [Math]::Max(1, [int]($size * 0.04))
    $y = $size - $r - [Math]::Max(1, [int]($size * 0.04))
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 235, 20, 60))
    $g.FillEllipse($red, $x, $y, $r, $r)

    if ($size -ge 24) {
        $fontSize = [Math]::Max(8, [int]($r * 0.72))
        $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $rect = New-Object System.Drawing.RectangleF $x, $y, $r, $r
        $g.DrawString('N', $font, [System.Drawing.Brushes]::White, $rect, $fmt)
        $font.Dispose(); $fmt.Dispose()
    }

    $red.Dispose(); $g.Dispose(); $base.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bmp = New-NightlyBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,@($size, $ms.ToArray())
    $ms.Dispose(); $bmp.Dispose()
}

$stream = [System.IO.File]::Create($OutputIcon)
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + (16 * $images.Count)
foreach ($item in $images) {
    $size = [int]$item[0]; $bytes = [byte[]]$item[1]
    $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach ($item in $images) { $writer.Write([byte[]]$item[1]) }
$writer.Flush(); $writer.Dispose(); $stream.Dispose()
Write-Host "Wrote $OutputIcon"
