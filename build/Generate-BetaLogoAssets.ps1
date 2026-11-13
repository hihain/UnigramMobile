<#
.SYNOPSIS
Regenerates the BETA app's original Metro message-logo asset family.

.DESCRIPTION
The default artwork includes the BETA label. Pass -NoBeta when generating an
otherwise identical unbranded variant, such as a Store-listing preview.
#>
[CmdletBinding()]
param(
    [string]$AssetDirectory,
    [switch]$NoBeta
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($AssetDirectory)) {
    $AssetDirectory = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\Unigram\Unigram\Assets\Logos'
}

$blue = [System.Drawing.Color]::FromArgb(0, 120, 215)
$white = [System.Drawing.Color]::White
$betaFill = [System.Drawing.Color]::FromArgb(242, 169, 0)
$betaBorder = [System.Drawing.Color]::FromArgb(255, 224, 120)
$betaText = [System.Drawing.Color]::FromArgb(20, 20, 20)

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [float]$Radius
    )

    $diameter = [Math]::Min($Radius * 2, [Math]::Min($Rectangle.Width, $Rectangle.Height))
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-BetaLabel {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$Width,
        [int]$Height
    )

    $label = [System.Drawing.RectangleF]::new(
        [float]($Width * 0.64),
        [float]($Height * 0.045),
        [float]($Width * 0.32),
        [float]($Height * 0.17))
    $borderWidth = [Math]::Max(1, [int][Math]::Round($Height * 0.008))

    $fillBrush = [System.Drawing.SolidBrush]::new($betaFill)
    $borderPen = [System.Drawing.Pen]::new($betaBorder, $borderWidth)
    $Graphics.FillRectangle($fillBrush, $label)
    $Graphics.DrawRectangle($borderPen, $label.X, $label.Y, $label.Width, $label.Height)

    $fontSize = [Math]::Max(4, $label.Height * 0.57)
    $font = [System.Drawing.Font]::new('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $textBrush = [System.Drawing.SolidBrush]::new($betaText)
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $Graphics.DrawString('BETA', $font, $textBrush, $label, $format)

    $format.Dispose()
    $textBrush.Dispose()
    $font.Dispose()
    $borderPen.Dispose()
    $fillBrush.Dispose()
}

function Draw-MonochromeMessageBubble {
    param(
        [System.Drawing.Graphics]$Graphics,
        [float]$X,
        [float]$Y,
        [float]$Width
    )

    $bodyHeight = $Width * 0.48
    $lineWidth = [Math]::Max(1, $Width * 0.055)
    $pen = [System.Drawing.Pen]::new($white, $lineWidth)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $radius = $bodyHeight * 0.22
    $diameter = $radius * 2
    $bottom = $Y + $bodyHeight
    $tailLeft = $X + ($Width * 0.16)
    $tailRight = $X + ($Width * 0.36)
    $tailTip = [System.Drawing.PointF]::new($X + ($Width * 0.07), $bottom + ($Width * 0.15))
    $bodyPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $bodyPath.StartFigure()
    $bodyPath.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $bodyPath.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $bodyPath.AddArc($X + $Width - $diameter, $bottom - $diameter, $diameter, $diameter, 0, 90)
    $bodyPath.AddLine($X + $Width - $radius, $bottom, $tailRight, $bottom)
    $bodyPath.AddLine($tailRight, $bottom, $tailTip.X, $tailTip.Y)
    $bodyPath.AddLine($tailTip.X, $tailTip.Y, $tailLeft, $bottom)
    $bodyPath.AddLine($tailLeft, $bottom, $X + $radius, $bottom)
    $bodyPath.AddArc($X, $bottom - $diameter, $diameter, $diameter, 90, 90)
    $bodyPath.CloseFigure()
    $Graphics.DrawPath($pen, $bodyPath)

    $gliderNose = [System.Drawing.PointF]::new($X + ($Width * 0.88), $Y + ($bodyHeight * 0.50))
    $upperWing = @(
        [System.Drawing.PointF]::new($X + ($Width * 0.50), $Y + ($bodyHeight * 0.24)),
        $gliderNose,
        [System.Drawing.PointF]::new($X + ($Width * 0.46), $Y + ($bodyHeight * 0.47)))
    $lowerWing = @(
        [System.Drawing.PointF]::new($X + ($Width * 0.50), $Y + ($bodyHeight * 0.76)),
        $gliderNose,
        [System.Drawing.PointF]::new($X + ($Width * 0.46), $Y + ($bodyHeight * 0.53)))
    $Graphics.DrawPolygon($pen, $upperWing)
    $Graphics.DrawPolygon($pen, $lowerWing)

    foreach ($row in 0..2) {
        $trailY = $Y + ($bodyHeight * (0.30 + ($row * 0.18)))
        $startX = $X + ($Width * (0.18 + ($row * 0.06)))
        $endX = $X + ($Width * (0.43 - ($row * 0.04)))
        $Graphics.DrawLine($pen, $startX, $trailY, $endX, $trailY)
    }

    $bodyPath.Dispose()
    $pen.Dispose()
}

function Draw-MessageBubble {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$CanvasWidth,
        [int]$CanvasHeight,
        [bool]$Monochrome,
        [bool]$ShowBeta
    )

    $designWidth = if ($CanvasWidth -gt ($CanvasHeight * 1.5)) {
        $CanvasHeight * 0.82
    }
    else {
        [Math]::Min($CanvasWidth, $CanvasHeight) * 0.78
    }

    $bodyHeight = $designWidth * 0.48
    $designHeight = $bodyHeight + ($designWidth * 0.14)
    $x = ($CanvasWidth - $designWidth) / 2
    $y = ($CanvasHeight - $designHeight) / 2

    if ($Monochrome) {
        Draw-MonochromeMessageBubble -Graphics $Graphics -X $x -Y $y -Width $designWidth
        return
    }

    $body = [System.Drawing.RectangleF]::new($x, $y, $designWidth, $bodyHeight)
    $whiteBrush = [System.Drawing.SolidBrush]::new($white)
    $blueBrush = [System.Drawing.SolidBrush]::new($blue)
    $bodyPath = New-RoundedRectanglePath -Rectangle $body -Radius ($bodyHeight * 0.22)
    $Graphics.FillPath($whiteBrush, $bodyPath)

    $tail = @(
        [System.Drawing.PointF]::new($x + ($designWidth * 0.16), $y + $bodyHeight),
        [System.Drawing.PointF]::new($x + ($designWidth * 0.07), $y + $bodyHeight + ($designWidth * 0.15)),
        [System.Drawing.PointF]::new($x + ($designWidth * 0.36), $y + $bodyHeight))
    $Graphics.FillPolygon($whiteBrush, $tail)

    $gliderNose = [System.Drawing.PointF]::new($x + ($designWidth * 0.88), $y + ($bodyHeight * 0.50))
    $upperWing = @(
        [System.Drawing.PointF]::new($x + ($designWidth * 0.50), $y + ($bodyHeight * 0.24)),
        $gliderNose,
        [System.Drawing.PointF]::new($x + ($designWidth * 0.46), $y + ($bodyHeight * 0.47)))
    $lowerWing = @(
        [System.Drawing.PointF]::new($x + ($designWidth * 0.50), $y + ($bodyHeight * 0.76)),
        $gliderNose,
        [System.Drawing.PointF]::new($x + ($designWidth * 0.46), $y + ($bodyHeight * 0.53)))
    $Graphics.FillPolygon($blueBrush, $upperWing)
    $Graphics.FillPolygon($blueBrush, $lowerWing)

    foreach ($row in 0..2) {
        $trailWidth = $designWidth * (0.23 - ($row * 0.025))
        $trailHeight = $bodyHeight * 0.085
        $trailX = $x + ($designWidth * (0.18 + ($row * 0.045)))
        $trailY = $y + ($bodyHeight * (0.29 + ($row * 0.18)))
        $trail = [System.Drawing.RectangleF]::new($trailX, $trailY, $trailWidth, $trailHeight)
        $trailPath = New-RoundedRectanglePath -Rectangle $trail -Radius ($trailHeight / 2)
        $Graphics.FillPath($blueBrush, $trailPath)
        $trailPath.Dispose()
    }

    if ($ShowBeta) {
        Draw-BetaLabel -Graphics $Graphics -Width $CanvasWidth -Height $CanvasHeight
    }

    $bodyPath.Dispose()
    $blueBrush.Dispose()
    $whiteBrush.Dispose()
}

function Save-Png {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [string]$Path
    )

    $stream = [System.IO.MemoryStream]::new()
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
    }
    finally {
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $AssetDirectory -PathType Container)) {
    throw "Logo asset directory does not exist: $AssetDirectory"
}

$assets = Get-ChildItem -LiteralPath $AssetDirectory -Filter '*.png' -File
foreach ($asset in $assets) {
    $source = [System.Drawing.Image]::FromFile($asset.FullName)
    try {
        $bitmap = [System.Drawing.Bitmap]::new($source.Width, $source.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    }
    finally {
        $source.Dispose()
    }

    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)

            $monochrome = $asset.Name.StartsWith('LockScreenLogo', [System.StringComparison]::OrdinalIgnoreCase)
            Draw-MessageBubble -Graphics $graphics -CanvasWidth $bitmap.Width -CanvasHeight $bitmap.Height -Monochrome $monochrome -ShowBeta (-not ($monochrome -or $NoBeta))
        }
        finally {
            $graphics.Dispose()
        }

        Save-Png -Bitmap $bitmap -Path $asset.FullName
    }
    finally {
        $bitmap.Dispose()
    }
}

Write-Host "Generated $($assets.Count) BETA logo assets in $AssetDirectory."
