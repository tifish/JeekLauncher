param()

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$ProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$AssetsDir = Join-Path $ProjectRoot 'JeekLauncher\Assets'
$PngPath = Join-Path $AssetsDir 'jeek.png'
$IcoPath = Join-Path $AssetsDir 'jeek.ico'

New-Item -ItemType Directory -Force -Path $AssetsDir | Out-Null

function New-RoundedRectanglePath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2

    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    return $path
}

function New-ScaledPoint {
    param([float]$X, [float]$Y, [float]$Scale)
    return [System.Drawing.PointF]::new($X * $Scale, $Y * $Scale)
}

function New-PngBytes {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $scale = $Size / 256.0
        $margin = [Math]::Max(0.5, 8.0 * $scale)
        $radius = 54.0 * $scale
        $tileSize = $Size - ($margin * 2)
        $tile = New-RoundedRectanglePath $margin $margin $tileSize $tileSize $radius

        $gradientRect = [System.Drawing.RectangleF]::new($margin, $margin, $tileSize, $tileSize)
        $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $gradientRect,
            [System.Drawing.Color]::FromArgb(255, 20, 148, 255),
            [System.Drawing.Color]::FromArgb(255, 130, 67, 230),
            [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal
        )
        $blend = [System.Drawing.Drawing2D.ColorBlend]::new()
        $blend.Positions = [float[]](0.0, 0.52, 1.0)
        $blend.Colors = [System.Drawing.Color[]](
            [System.Drawing.Color]::FromArgb(255, 20, 148, 255),
            [System.Drawing.Color]::FromArgb(255, 50, 80, 232),
            [System.Drawing.Color]::FromArgb(255, 132, 70, 226)
        )
        $gradient.InterpolationColors = $blend
        $graphics.FillPath($gradient, $tile)
        $gradient.Dispose()

        if ($Size -ge 32) {
            $highlightPath = New-RoundedRectanglePath ($margin + 2 * $scale) ($margin + 2 * $scale) ($tileSize - 4 * $scale) ($tileSize - 4 * $scale) ($radius - 2 * $scale)
            $highlight = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                $gradientRect,
                [System.Drawing.Color]::FromArgb(70, 255, 255, 255),
                [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
                [System.Drawing.Drawing2D.LinearGradientMode]::Vertical
            )
            $graphics.FillPath($highlight, $highlightPath)
            $highlight.Dispose()
            $highlightPath.Dispose()
        }

        $shadowPath = New-RoundedRectanglePath ($margin + 1 * $scale) ($margin + 1 * $scale) ($tileSize - 2 * $scale) ($tileSize - 2 * $scale) ($radius - 1 * $scale)
        $shadowPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(42, 0, 0, 0), [Math]::Max(1.0, 3.0 * $scale))
        $graphics.DrawPath($shadowPen, $shadowPath)
        $shadowPen.Dispose()
        $shadowPath.Dispose()

        $white = [System.Drawing.Color]::FromArgb(246, 255, 255, 255)
        $softWhite = [System.Drawing.Color]::FromArgb(208, 255, 255, 255)
        $strokeWidth = [Math]::Max(2.0, 29.0 * $scale)
        $accentWidth = [Math]::Max(1.0, 7.0 * $scale)

        if ($Size -ge 32) {
            $accentPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(120, 124, 240, 255), $accentWidth)
            $accentPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $accentPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $graphics.DrawLine($accentPen, (105 * $scale), (179 * $scale), (72 * $scale), (179 * $scale))
            $accentPen.Dispose()
        }

        $jPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $jPath.StartFigure()
        $jPath.AddLine((151 * $scale), (67 * $scale), (151 * $scale), (150 * $scale))
        $jPath.AddBezier(
            (151 * $scale), (150 * $scale),
            (151 * $scale), (190 * $scale),
            (103 * $scale), (206 * $scale),
            (77 * $scale), (174 * $scale)
        )

        $jPen = [System.Drawing.Pen]::new($white, $strokeWidth)
        $jPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $jPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $jPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawPath($jPen, $jPath)
        $jPen.Dispose()
        $jPath.Dispose()

        $topPen = [System.Drawing.Pen]::new($white, $strokeWidth)
        $topPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $topPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
        $topPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawLine($topPen, (88 * $scale), (68 * $scale), (174 * $scale), (68 * $scale))
        $topPen.Dispose()

        $arrowBrush = [System.Drawing.SolidBrush]::new($softWhite)
        $arrow = [System.Drawing.PointF[]](
            (New-ScaledPoint 168 39 $scale),
            (New-ScaledPoint 217 68 $scale),
            (New-ScaledPoint 168 97 $scale)
        )
        $graphics.FillPolygon($arrowBrush, $arrow)
        $arrowBrush.Dispose()

        if ($Size -ge 64) {
            $shinePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(78, 255, 255, 255), [Math]::Max(1.0, 3.0 * $scale))
            $shinePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $shinePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $graphics.DrawLine($shinePen, (51 * $scale), (45 * $scale), (120 * $scale), (29 * $scale))
            $shinePen.Dispose()
        }

        $tile.Dispose()

        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        # Comma keeps PowerShell from unrolling the byte[] into an object[],
        # which would make BinaryWriter.Write drop the image data.
        return ,$stream.ToArray()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Write-UInt16LE {
    param([System.IO.BinaryWriter]$Writer, [int]$Value)
    $Writer.Write([UInt16]$Value)
}

function Write-UInt32LE {
    param([System.IO.BinaryWriter]$Writer, [long]$Value)
    $Writer.Write([UInt32]$Value)
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @(foreach ($size in $sizes) {
    [PSCustomObject]@{
        Size = $size
        Bytes = New-PngBytes -Size $size
    }
})

$pngFrame = $images | Where-Object { $_.Size -eq 256 } | Select-Object -First 1
[System.IO.File]::WriteAllBytes($PngPath, [byte[]]$pngFrame.Bytes)

$icoStream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($icoStream)

try {
    Write-UInt16LE $writer 0
    Write-UInt16LE $writer 1
    Write-UInt16LE $writer $images.Count

    $offset = 6 + ($images.Count * 16)
    foreach ($image in $images) {
        $sizeByte = if ($image.Size -eq 256) { 0 } else { $image.Size }
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        Write-UInt16LE $writer 1
        Write-UInt16LE $writer 32
        Write-UInt32LE $writer $image.Bytes.Length
        Write-UInt32LE $writer $offset
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write($image.Bytes)
    }

    [System.IO.File]::WriteAllBytes($IcoPath, $icoStream.ToArray())
}
finally {
    $writer.Dispose()
    $icoStream.Dispose()
}

$pngInfo = Get-Item -LiteralPath $PngPath
$icoInfo = Get-Item -LiteralPath $IcoPath

Write-Host ("{0} {1} bytes" -f $pngInfo.FullName, $pngInfo.Length)
Write-Host ("{0} {1} bytes" -f $icoInfo.FullName, $icoInfo.Length)
