$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$path = Join-Path $PSScriptRoot '..\src\LetMeSleep\Assets\LetMeSleep.ico'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$images = [Collections.Generic.List[byte[]]]::new()
$glyph = [char]::ConvertFromUtf32(0x1F4A4)
$font = [Drawing.Font]::new('Segoe UI Emoji', 200, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(45, 122, 224))
$canvas = [Drawing.Bitmap]::new(512, 512)
$graphics = [Drawing.Graphics]::FromImage($canvas)

try {
    if ($font.Name -ne 'Segoe UI Emoji') {
        throw 'Segoe UI Emoji is required to generate the sleeping symbol icon.'
    }
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.DrawString($glyph, $font, $brush, 40, 40, [Drawing.StringFormat]::GenericTypographic)
    $left = 512
    $top = 512
    $right = -1
    $bottom = -1
    for ($y = 0; $y -lt 512; $y++) {
        for ($x = 0; $x -lt 512; $x++) {
            if ($canvas.GetPixel($x, $y).A -gt 0) {
                $left = [Math]::Min($left, $x)
                $top = [Math]::Min($top, $y)
                $right = [Math]::Max($right, $x)
                $bottom = [Math]::Max($bottom, $y)
            }
        }
    }
    if ($right -lt $left) { throw 'The sleeping symbol could not be rendered.' }
    $bounds = [Drawing.Rectangle]::new($left, $top, $right - $left + 1, $bottom - $top + 1)
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size)
        $scaled = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $scaled.Clear([Drawing.Color]::Transparent)
            $scaled.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $padding = [Math]::Max(1, [Math]::Round($size * 0.06))
            $scale = ($size - 2 * $padding) / [Math]::Max($bounds.Width, $bounds.Height)
            $width = [int][Math]::Round($bounds.Width * $scale)
            $height = [int][Math]::Round($bounds.Height * $scale)
            $destination = [Drawing.Rectangle]::new(
                [int](($size - $width) / 2), [int](($size - $height) / 2), $width, $height)
            $scaled.DrawImage($canvas, $destination, $bounds, [Drawing.GraphicsUnit]::Pixel)
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $images.Add($stream.ToArray())
        }
        finally {
            $stream.Dispose()
            $scaled.Dispose()
            $bitmap.Dispose()
        }
    }

    $file = [IO.File]::Create($path)
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($image in $images) { $writer.Write($image) }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}
finally {
    $graphics.Dispose()
    $canvas.Dispose()
    $brush.Dispose()
    $font.Dispose()
}
Write-Output "Generated $path"
