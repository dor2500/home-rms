<#
.SYNOPSIS
  Generates multi-resolution Windows .ico and .png icons for Home RMS Controller and Agent.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$ctrlAssets = Join-Path $root 'src/HomeRms.Controller/Assets'
$agentAssets = Join-Path $root 'src/HomeRms.Agent/Assets'
New-Item -ItemType Directory -Force -Path $ctrlAssets | Out-Null
New-Item -ItemType Directory -Force -Path $agentAssets | Out-Null

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [float]($r * 2.0)
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc([float]($x + $w - $d), $y, $d, $d, 270, 90)
    $path.AddArc([float]($x + $w - $d), [float]($y + $h - $d), $d, $d, 0, 90)
    $path.AddArc($x, [float]($y + $h - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Render-IconBitmap([int]$size, [string]$variant) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0

    # 1. Outer squircle background
    $pad = [float](12 * $s)
    $boxW = [float]($size - 2 * $pad)
    $radius = [float](56 * $s)
    $bgPath = New-RoundedRectPath $pad $pad $boxW $boxW $radius

    if ($variant -eq 'controller') {
        $bgTop = [System.Drawing.Color]::FromArgb(255, 24, 34, 54)
        $bgBot = [System.Drawing.Color]::FromArgb(255, 11, 16, 27)
        $primaryTop = [System.Drawing.Color]::FromArgb(255, 96, 165, 250)
        $primaryBot = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)
        $badgeTop = [System.Drawing.Color]::FromArgb(255, 52, 211, 153)
        $badgeBot = [System.Drawing.Color]::FromArgb(255, 16, 185, 129)
    } else {
        $bgTop = [System.Drawing.Color]::FromArgb(255, 18, 38, 44)
        $bgBot = [System.Drawing.Color]::FromArgb(255, 10, 20, 26)
        $primaryTop = [System.Drawing.Color]::FromArgb(255, 52, 211, 153)
        $primaryBot = [System.Drawing.Color]::FromArgb(255, 5, 150, 105)
        $badgeTop = [System.Drawing.Color]::FromArgb(255, 96, 165, 250)
        $badgeBot = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)
    }

    $bgBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0),
        [System.Drawing.PointF]::new($size, $size),
        $bgTop, $bgBot)
    $g.FillPath($bgBrush, $bgPath)

    $borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(180, 71, 85, 105), [Math]::Max(1.0, [float](5 * $s)))
    $g.DrawPath($borderPen, $bgPath)

    # 2. Monitor frame
    $monX = [float](44 * $s)
    $monY = [float](52 * $s)
    $monW = [float](168 * $s)
    $monH = [float](112 * $s)
    $monR = [float](20 * $s)
    $monPath = New-RoundedRectPath $monX $monY $monW $monH $monR

    $screenFill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 9, 13, 22))
    $g.FillPath($screenFill, $monPath)

    $monGrad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new($monX, $monY),
        [System.Drawing.PointF]::new([float]($monX + $monW), [float]($monY + $monH)),
        $primaryTop, $primaryBot)
    $monPen = [System.Drawing.Pen]::new($monGrad, [Math]::Max(1.5, [float](10 * $s)))
    $g.DrawPath($monPen, $monPath)

    # 3. Monitor Stand
    $standBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 100, 116, 139))
    $neckW = [float](36 * $s)
    $neckH = [float](24 * $s)
    $g.FillRectangle($standBrush, [float](128 * $s - $neckW / 2), [float](164 * $s), $neckW, $neckH)

    $basePath = New-RoundedRectPath ([float](82 * $s)) ([float](184 * $s)) ([float](92 * $s)) ([float](12 * $s)) ([float](5 * $s))
    $g.FillPath($standBrush, $basePath)

    # 4. Screen Center Symbol
    $dotBrush = [System.Drawing.SolidBrush]::new($primaryTop)
    $dotR = [float](12 * $s)
    $cx = [float](96 * $s)
    $cy = [float](108 * $s)
    $g.FillEllipse($dotBrush, [float]($cx - $dotR), [float]($cy - $dotR), [float]($dotR * 2), [float]($dotR * 2))

    $wavePen = [System.Drawing.Pen]::new($primaryTop, [Math]::Max(1.2, [float](8 * $s)))
    $wavePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $wavePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($wavePen, [float]($cx - 28 * $s), [float]($cy - 28 * $s), [float](56 * $s), [float](56 * $s), -45, 90)
    $g.DrawArc($wavePen, [float]($cx - 48 * $s), [float]($cy - 48 * $s), [float](96 * $s), [float](96 * $s), -45, 90)

    # 5. Corner Security Shield Badge
    $shX = [float](146 * $s)
    $shY = [float](122 * $s)
    $shW = [float](72 * $s)
    $shH = [float](82 * $s)
    $shield = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float]($shX + $shW / 2), $shY),
        [System.Drawing.PointF]::new([float]($shX + $shW), [float]($shY + $shH * 0.18)),
        [System.Drawing.PointF]::new([float]($shX + $shW), [float]($shY + $shH * 0.58)),
        [System.Drawing.PointF]::new([float]($shX + $shW / 2), [float]($shY + $shH)),
        [System.Drawing.PointF]::new($shX, [float]($shY + $shH * 0.58)),
        [System.Drawing.PointF]::new($shX, [float]($shY + $shH * 0.18))
    )
    $shield.AddPolygon($pts)

    $badgeBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new($shX, $shY),
        [System.Drawing.PointF]::new([float]($shX + $shW), [float]($shY + $shH)),
        $badgeTop, $badgeBot)
    $g.FillPath($badgeBrush, $shield)

    $shBorder = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 11, 16, 27), [Math]::Max(1.0, [float](6 * $s)))
    $g.DrawPath($shBorder, $shield)

    # Checkmark inside shield
    $chkPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [Math]::Max(1.2, [float](8 * $s)))
    $chkPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $chkPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $chkPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $chkPts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float]($shX + $shW * 0.28), [float]($shY + $shH * 0.50)),
        [System.Drawing.PointF]::new([float]($shX + $shW * 0.45), [float]($shY + $shH * 0.66)),
        [System.Drawing.PointF]::new([float]($shX + $shW * 0.74), [float]($shY + $shH * 0.35))
    )
    $g.DrawLines($chkPen, $chkPts)

    $g.Dispose()
    return $bmp
}

function Write-MultiSizeIco([string]$icoPath, [string]$pngPath, [string]$variant) {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $pngBuffers = [System.Collections.Generic.List[byte[]]]::new()

    foreach ($sz in $sizes) {
        $bmp = Render-IconBitmap $sz $variant
        if ($sz -eq 256) {
            $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        $ms = [System.IO.MemoryStream]::new()
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngBuffers.Add($ms.ToArray())
        $ms.Dispose()
        $bmp.Dispose()
    }

    $fs = [System.IO.File]::Create($icoPath)
    $bw = [System.IO.BinaryWriter]::new($fs)

    # ICONDIR header
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$sizes.Count)

    $offset = 6 + ($sizes.Count * 16)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $sz = $sizes[$i]
        $bytes = $pngBuffers[$i]
        $dimByte = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
        $bw.Write($dimByte)            # bWidth
        $bw.Write($dimByte)            # bHeight
        $bw.Write([byte]0)             # bColorCount
        $bw.Write([byte]0)             # bReserved
        $bw.Write([uint16]1)           # wPlanes
        $bw.Write([uint16]32)          # wBitCount
        $bw.Write([uint32]$bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $bytes.Length
    }

    foreach ($bytes in $pngBuffers) {
        $bw.Write($bytes)
    }

    $bw.Flush()
    $bw.Dispose()
    $fs.Dispose()
}

Write-MultiSizeIco (Join-Path $ctrlAssets 'controller.ico') (Join-Path $ctrlAssets 'controller.png') 'controller'
Write-MultiSizeIco (Join-Path $agentAssets 'agent.ico') (Join-Path $agentAssets 'agent.png') 'agent'
Write-Host "Generated multi-size icons in $ctrlAssets and $agentAssets" -ForegroundColor Green
