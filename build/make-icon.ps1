# Builds build\icon.ico from build\icon.png as a real multi-size icon.
# Windows needs 16/24/32/48 for the title bar, taskbar and Alt-Tab; an ICO that
# only carries a 256x256 frame makes those surfaces fall back to a generic icon.
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $PSScriptRoot "icon.png"
$target = Join-Path $PSScriptRoot "icon.ico"
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

$original = [System.Drawing.Image]::FromFile($source)

# A small source (the current one is 48x48) goes blurry when bicubic stretches it to 64 or 256.
# Frames larger than the source are drawn from a nearest-neighbor master instead: the source is
# upscaled once by a whole factor to at least 1024px, so every source pixel becomes a sharp block,
# and downsampling that keeps hard edges with at most one blended pixel. Frames at or below the
# source size still come from the source itself, so the 48px frame is the source unchanged.
$factor = [int][Math]::Ceiling(1024 / [Math]::Min($original.Width, $original.Height))
if ($factor -lt 1) { $factor = 1 }
$image = New-Object System.Drawing.Bitmap(($original.Width * $factor), ($original.Height * $factor), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($image)
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
# Half offset keeps nearest-neighbor from shifting the blocks by half a source pixel.
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$graphics.Clear([System.Drawing.Color]::Transparent)
$graphics.DrawImage($original, 0, 0, $image.Width, $image.Height)
$graphics.Dispose()

# Clamping the edges stops the downsampler from blending transparent border pixels inward.
$edges = New-Object System.Drawing.Imaging.ImageAttributes
$edges.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
$frames = @()

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $from = if ($size -gt $original.Width) { $image } else { $original }
    $graphics.DrawImage($from, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)), 0, 0, $from.Width, $from.Height, [System.Drawing.GraphicsUnit]::Pixel, $edges)
    $graphics.Dispose()

    if ($size -eq 64) { $bitmap.Save((Join-Path $PSScriptRoot "icon64.png"), [System.Drawing.Imaging.ImageFormat]::Png) }

    $stream = New-Object System.IO.MemoryStream
    if ($size -ge 64) {
        # 256px frames are stored as PNG inside the ICO.
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        # Smaller frames use the classic DIB layout: BITMAPINFOHEADER with the
        # doubled height, bottom-up BGRA rows, then the 1bpp AND mask.
        $writer = New-Object System.IO.BinaryWriter($stream)
        $maskStride = [int]([Math]::Floor(($size + 31) / 32) * 4)
        $writer.Write([int]40)
        $writer.Write([int]$size)
        $writer.Write([int]($size * 2))
        $writer.Write([int16]1)
        $writer.Write([int16]32)
        $writer.Write([int]0)
        $writer.Write([int]($size * $size * 4 + $maskStride * $size))
        $writer.Write([int]0)
        $writer.Write([int]0)
        $writer.Write([int]0)
        $writer.Write([int]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                $writer.Write([byte]$pixel.B)
                $writer.Write([byte]$pixel.G)
                $writer.Write([byte]$pixel.R)
                $writer.Write([byte]$pixel.A)
            }
        }
        $writer.Write((New-Object byte[] ($maskStride * $size)))
        $writer.Flush()
    }
    $bitmap.Dispose()
    $frames += [pscustomobject]@{ Size = $size; Data = $stream.ToArray() }
    $stream.Dispose()
}
$image.Dispose()
$original.Dispose()
$edges.Dispose()

$ico = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($ico)
$writer.Write([int16]0)
$writer.Write([int16]1)
$writer.Write([int16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([int16]1)
    $writer.Write([int16]32)
    $writer.Write([int]$frame.Data.Length)
    $writer.Write([int]$offset)
    $offset += $frame.Data.Length
}
foreach ($frame in $frames) { $writer.Write($frame.Data) }
$writer.Flush()

[System.IO.File]::WriteAllBytes($target, $ico.ToArray())
$writer.Dispose()
$ico.Dispose()

"wrote {0} with {1} frames ({2} bytes)" -f $target, $frames.Count, (Get-Item $target).Length
