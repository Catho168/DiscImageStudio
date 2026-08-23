param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\src\DiscImageStudio.App\Assets")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null

function New-RoundedRectanglePath([Drawing.RectangleF]$Rectangle, [float]$Radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $arc = [Drawing.RectangleF]::new($Rectangle.X, $Rectangle.Y, $diameter, $diameter)
    $path.AddArc($arc, 180, 90)
    $arc.X = $Rectangle.Right - $diameter
    $path.AddArc($arc, 270, 90)
    $arc.Y = $Rectangle.Bottom - $diameter
    $path.AddArc($arc, 0, 90)
    $arc.X = $Rectangle.Left
    $path.AddArc($arc, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-LogoPng([int]$Width, [int]$Height, [string]$FileName, [bool]$IncludeTitle) {
    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $background = [Drawing.RectangleF]::new(0, 0, $Width, $Height)
    $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new(
        $background,
        [Drawing.Color]::FromArgb(255, 82, 103, 245),
        [Drawing.Color]::FromArgb(255, 23, 33, 62),
        42.0)
    $radius = [Math]::Max(2, [Math]::Min($Width, $Height) * 0.12)
    $rounded = New-RoundedRectanglePath $background $radius
    $graphics.FillPath($gradient, $rounded)

    $unit = [Math]::Min($Width, $Height)
    $logoCentreX = if ($IncludeTitle) { $Height * 0.52 } else { $Width * 0.5 }
    $logoCentreY = $Height * 0.5
    $discRadius = $unit * 0.30
    $discRect = [Drawing.RectangleF]::new(
        $logoCentreX - $discRadius,
        $logoCentreY - $discRadius,
        $discRadius * 2,
        $discRadius * 2)
    $whiteBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(246, 250, 255))
    $graphics.FillEllipse($whiteBrush, $discRect)

    $ringPen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(100, 82, 103, 245), [Math]::Max(1, $unit * 0.018))
    $graphics.DrawArc($ringPen, $discRect, 205, 292)
    $innerRadius = $discRadius * 0.19
    $innerBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 35, 48, 86))
    $graphics.FillEllipse(
        $innerBrush,
        [float]($logoCentreX - $innerRadius),
        [float]($logoCentreY - $innerRadius),
        [float]($innerRadius * 2),
        [float]($innerRadius * 2))

    $laserPen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(255, 255, 154, 73), [Math]::Max(1.5, $unit * 0.035))
    $laserPen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $laserPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine(
        $laserPen,
        [float]($logoCentreX + ($discRadius * 0.18)),
        [float]($logoCentreY + ($discRadius * 0.18)),
        [float]($logoCentreX + ($discRadius * 0.70)),
        [float]($logoCentreY + ($discRadius * 0.70)))

    if ($IncludeTitle) {
        $titleSize = [Math]::Max(12, $Height * 0.105)
        $subtitleSize = [Math]::Max(7, $Height * 0.048)
        $titleFont = [Drawing.Font]::new("Segoe UI", $titleSize, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
        $subtitleFont = [Drawing.Font]::new("Segoe UI", $subtitleSize, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
        $titleBrush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
        $subtitleBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(210, 224, 230, 255))
        $textX = $Height * 0.98
        $graphics.DrawString("Disc Image Studio", $titleFont, $titleBrush, [float]$textX, [float]($Height * 0.31))
        $graphics.DrawString("Optical art · CD / DVD", $subtitleFont, $subtitleBrush, [float]$textX, [float]($Height * 0.55))
        $titleFont.Dispose()
        $subtitleFont.Dispose()
        $titleBrush.Dispose()
        $subtitleBrush.Dispose()
    }

    $path = Join-Path $output $FileName
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $laserPen.Dispose()
    $innerBrush.Dispose()
    $ringPen.Dispose()
    $whiteBrush.Dispose()
    $rounded.Dispose()
    $gradient.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    return $path
}

New-LogoPng 44 44 "Square44x44Logo.png" $false | Out-Null
New-LogoPng 50 50 "StoreLogo.png" $false | Out-Null
New-LogoPng 150 150 "Square150x150Logo.png" $false | Out-Null
New-LogoPng 256 256 "DiscImageStudio-256.png" $false | Out-Null
New-LogoPng 310 150 "Wide310x150Logo.png" $true | Out-Null
New-LogoPng 620 300 "SplashScreen.png" $true | Out-Null

$pngPath = Join-Path $output "DiscImageStudio-256.png"
$pngBytes = [IO.File]::ReadAllBytes($pngPath)
$icoBytes = [byte[]]::new(22 + $pngBytes.Length)
[BitConverter]::GetBytes([uint16]0).CopyTo($icoBytes, 0)
[BitConverter]::GetBytes([uint16]1).CopyTo($icoBytes, 2)
[BitConverter]::GetBytes([uint16]1).CopyTo($icoBytes, 4)
$icoBytes[6] = 0
$icoBytes[7] = 0
$icoBytes[8] = 0
$icoBytes[9] = 0
[BitConverter]::GetBytes([uint16]1).CopyTo($icoBytes, 10)
[BitConverter]::GetBytes([uint16]32).CopyTo($icoBytes, 12)
[BitConverter]::GetBytes([uint32]$pngBytes.Length).CopyTo($icoBytes, 14)
[BitConverter]::GetBytes([uint32]22).CopyTo($icoBytes, 18)
[Buffer]::BlockCopy($pngBytes, 0, $icoBytes, 22, $pngBytes.Length)
[IO.File]::WriteAllBytes((Join-Path $output "DiscImageStudio.ico"), $icoBytes)

Write-Host "Generated Microsoft Store assets in $output"
