param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\src\Huaci.App\Assets")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Bounds,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($Bounds.Left, $Bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Bounds.Left, $Bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-TranslationGlyphPath {
    param([int]$CanvasSize)

    # Build the character as a vector path so the same mark stays crisp in every
    # ICO frame. Use the code point rather than a source literal because Windows
    # PowerShell 5.1 does not consistently decode BOM-less UTF-8 scripts.
    $glyph = [string][char]0x8BD1
    $fontFamily = [System.Drawing.FontFamily]::new("Microsoft YaHei UI")
    $format = [System.Drawing.StringFormat]::GenericTypographic
    $format.FormatFlags = $format.FormatFlags -bor [System.Drawing.StringFormatFlags]::NoClip
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    try {
        $path.AddString(
            $glyph,
            $fontFamily,
            [int][System.Drawing.FontStyle]::Bold,
            [float]$CanvasSize,
            [System.Drawing.PointF]::Empty,
            $format)

        $bounds = $path.GetBounds()
        $targetSize = $CanvasSize * 0.75
        $scale = [Math]::Min($targetSize / $bounds.Width, $targetSize / $bounds.Height)
        $offsetX = (($CanvasSize - ($bounds.Width * $scale)) / 2.0) - ($bounds.X * $scale)
        $offsetY = (($CanvasSize - ($bounds.Height * $scale)) / 2.0) - ($bounds.Y * $scale) - ($CanvasSize * 0.012)

        $matrix = [System.Drawing.Drawing2D.Matrix]::new(
            [float]$scale,
            0,
            0,
            [float]$scale,
            [float]$offsetX,
            [float]$offsetY)
        try {
            $path.Transform($matrix)
        }
        finally {
            $matrix.Dispose()
        }

        return $path
    }
    catch {
        $path.Dispose()
        throw
    }
    finally {
        $format.Dispose()
        $fontFamily.Dispose()
    }
}

function New-LogoPng {
    param([int]$Size)

    # Four-times supersampling keeps the dense strokes of the Chinese character
    # legible in the 16/20/24 pixel notification-area frames.
    $canvasSize = $Size * 4
    $source = [System.Drawing.Bitmap]::new(
        $canvasSize,
        $canvasSize,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($source)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $unit = $canvasSize / 256.0
        $bounds = [System.Drawing.RectangleF]::new(8 * $unit, 8 * $unit, 240 * $unit, 240 * $unit)
        $backgroundPath = New-RoundedRectanglePath -Bounds $bounds -Radius (54 * $unit)
        try {
            $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                $bounds,
                [System.Drawing.ColorTranslator]::FromHtml("#8296FF"),
                [System.Drawing.ColorTranslator]::FromHtml("#405DDE"),
                48.0)
            try {
                $graphics.FillPath($background, $backgroundPath)
            }
            finally {
                $background.Dispose()
            }

            # A restrained inner rim preserves the silhouette on both light and
            # dark taskbars without adding noise at the smallest frame sizes.
            $rim = [System.Drawing.Pen]::new(
                [System.Drawing.Color]::FromArgb(105, 225, 230, 255),
                [Math]::Max(1.0, 2.0 * $unit))
            try {
                $graphics.DrawPath($rim, $backgroundPath)
            }
            finally {
                $rim.Dispose()
            }
        }
        finally {
            $backgroundPath.Dispose()
        }

        $glyphPath = New-TranslationGlyphPath -CanvasSize $canvasSize
        try {
            $glyphShadow = [System.Drawing.SolidBrush]::new(
                [System.Drawing.Color]::FromArgb(42, 15, 31, 92))
            $shadowMatrix = [System.Drawing.Drawing2D.Matrix]::new()
            try {
                $shadowMatrix.Translate(0, [float](3 * $unit))
                $glyphPath.Transform($shadowMatrix)
                $graphics.FillPath($glyphShadow, $glyphPath)
                $shadowMatrix.Reset()
                $shadowMatrix.Translate(0, [float](-3 * $unit))
                $glyphPath.Transform($shadowMatrix)
            }
            finally {
                $shadowMatrix.Dispose()
                $glyphShadow.Dispose()
            }

            $foreground = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
            try {
                $graphics.FillPath($foreground, $glyphPath)
            }
            finally {
                $foreground.Dispose()
            }
        }
        finally {
            $glyphPath.Dispose()
        }

        $bitmap = [System.Drawing.Bitmap]::new(
            $Size,
            $Size,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $outputGraphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $outputGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $outputGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $outputGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $outputGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $outputGraphics.DrawImage(
                $source,
                [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                0,
                0,
                $canvasSize,
                $canvasSize,
                [System.Drawing.GraphicsUnit]::Pixel)

            $stream = [System.IO.MemoryStream]::new()
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return $stream.ToArray()
        }
        finally {
            $outputGraphics.Dispose()
            $bitmap.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $source.Dispose()
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    [pscustomobject]@{ Size = $size; Bytes = (New-LogoPng -Size $size) }
}

$iconPath = Join-Path $OutputDirectory "Huaci.ico"
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { [byte]0 } else { [byte]$image.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

[System.IO.File]::WriteAllBytes(
    (Join-Path $OutputDirectory "Huaci-logo-256.png"),
    [byte[]](($images | Where-Object Size -eq 256).Bytes))

Write-Host "Generated icon: $iconPath"
