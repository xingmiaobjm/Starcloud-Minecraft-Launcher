# One-off generator for the app icon placeholder (Assets/AppIcon.ico).
#
# Placeholder on purpose: the real artwork is not decided yet, so this draws a
# neutral dark plate with an isometric cube (same visual language as the
# Assets/MC-Icons block renders) and packs it into a multi-size ICO.
#
# ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI,
# and a mangled CJK comment can swallow the next line (see gen-wallpapers.ps1).
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\gen-app-icon.ps1
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\dotnet\src\Starcloud.App\Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outFile = Join-Path $outDir 'AppIcon.ico'

# 16/24/32/48 are the sizes Explorer, the taskbar and Alt-Tab actually use.
$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-PointF {
    param([double]$X, [double]$Y)
    New-Object System.Drawing.PointF([float]$X, [float]$Y)
}

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # 1. rounded-square plate (dark slate, same family as the app's dark theme)
    $pad = [int][Math]::Round($Size * 0.02)
    $r = [Math]::Max(1, [int][Math]::Round($Size * 0.20))
    $d = $r * 2
    $w = $Size - 2 * $pad
    $plate = New-Object System.Drawing.Drawing2D.GraphicsPath
    $plate.AddArc([float]$pad, [float]$pad, [float]$d, [float]$d, [float]180, [float]90)
    $plate.AddArc([float]($pad + $w - $d), [float]$pad, [float]$d, [float]$d, [float]270, [float]90)
    $plate.AddArc([float]($pad + $w - $d), [float]($pad + $w - $d), [float]$d, [float]$d, [float]0, [float]90)
    $plate.AddArc([float]$pad, [float]($pad + $w - $d), [float]$d, [float]$d, [float]90, [float]90)
    $plate.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle($pad, $pad, $w, $w)
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.ColorTranslator]::FromHtml('#3B4453'),
        [System.Drawing.ColorTranslator]::FromHtml('#191D25'),
        [float]60)
    $g.FillPath($lg, $plate)
    $lg.Dispose()
    $plate.Dispose()

    # 2. isometric cube: top / left / right faces get three shades so it still
    #    reads as a cube when scaled down to 16 px.
    $hh = $Size * 0.17
    $ww = $hh * 0.866
    $cx = $Size / 2.0
    $cy = $Size / 2.0

    $top = @(
        (New-PointF $cx ($cy - $hh)),
        (New-PointF ($cx + $ww) ($cy - $hh / 2)),
        (New-PointF $cx $cy),
        (New-PointF ($cx - $ww) ($cy - $hh / 2))
    )
    $left = @(
        (New-PointF ($cx - $ww) ($cy - $hh / 2)),
        (New-PointF $cx $cy),
        (New-PointF $cx ($cy + $hh)),
        (New-PointF ($cx - $ww) ($cy + $hh / 2))
    )
    $right = @(
        (New-PointF ($cx + $ww) ($cy - $hh / 2)),
        (New-PointF $cx $cy),
        (New-PointF $cx ($cy + $hh)),
        (New-PointF ($cx + $ww) ($cy + $hh / 2))
    )

    $faces = @(
        @{ Points = $top; Hex = '#A9B4C7' },
        @{ Points = $left; Hex = '#78849B' },
        @{ Points = $right; Hex = '#586275' }
    )
    foreach ($f in $faces) {
        $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($f.Hex))
        $g.FillPolygon($brush, [System.Drawing.PointF[]]$f.Points)
        $brush.Dispose()
    }

    $g.Dispose()
    $bmp
}

# Packs a bitmap into a 32bpp BITMAPINFOHEADER DIB (the classic ICO payload:
# XOR bitmap bottom-up + a zeroed AND mask, alpha channel drives transparency).
function ConvertTo-IconDib {
    param([System.Drawing.Bitmap]$Bmp)

    $w = $Bmp.Width
    $h = $Bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $Bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $Bmp.UnlockBits($data)

    $maskRow = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $maskSize = $maskRow * $h
    $xorSize = $w * 4 * $h

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40)              # biSize
    $bw.Write([int32]$w)               # biWidth
    $bw.Write([int32]($h * 2))         # biHeight = XOR + AND
    $bw.Write([uint16]1)               # biPlanes
    $bw.Write([uint16]32)              # biBitCount
    $bw.Write([uint32]0)               # biCompression = BI_RGB
    $bw.Write([uint32]$xorSize)        # biSizeImage
    $bw.Write([int32]0)                # biXPelsPerMeter
    $bw.Write([int32]0)                # biYPelsPerMeter
    $bw.Write([uint32]0)               # biClrUsed
    $bw.Write([uint32]0)               # biClrImportant
    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($raw, $y * $stride, $w * 4)
    }
    $bw.Write((New-Object byte[] $maskSize), 0, $maskSize)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()
    , $bytes
}

$images = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap -Size $s
    $dib = ConvertTo-IconDib -Bmp $bmp
    $bmp.Dispose()
    $images += , @{ Size = $s; Data = $dib }
}

$fs = [System.IO.File]::Create($outFile)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)                    # idReserved
$bw.Write([uint16]1)                    # idType = 1 (icon)
$bw.Write([uint16]$images.Count)        # idCount
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }   # 0 means 256
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)                  # bColorCount
    $bw.Write([byte]0)                  # bReserved
    $bw.Write([uint16]1)                # wPlanes
    $bw.Write([uint16]32)               # wBitCount
    $bw.Write([uint32]$img.Data.Length)
    $bw.Write([uint32]$offset)
    $offset += $img.Data.Length
}
foreach ($img in $images) {
    $bw.Write($img.Data, 0, $img.Data.Length)
}
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

Write-Host ("AppIcon.ico  {0} sizes ({1})  {2:N0} KB" -f `
    $images.Count, ($sizes -join '/'), ((Get-Item $outFile).Length / 1KB))
