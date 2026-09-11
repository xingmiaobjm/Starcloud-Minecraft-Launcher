# One-off generator for built-in wallpapers (D1 custom background). ASCII-only on purpose:
# Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI, and a mangled CJK comment can swallow the next line.
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\dotnet\src\Starcloud.App\Assets\Wallpapers'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$W = 1920
$H = 1080

function New-Blob {
    param([int]$X, [int]$Y, [int]$R, [int]$A, [int]$Cr, [int]$Cg, [int]$Cb)
    @{ X = $X; Y = $Y; R = $R; A = $A; Cr = $Cr; Cg = $Cg; Cb = $Cb }
}

function New-Wallpaper {
    param([string]$File, [string]$TopHex, [string]$BottomHex, [array]$Blobs)

    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    $rect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
    $c1 = [System.Drawing.ColorTranslator]::FromHtml($TopHex)
    $c2 = [System.Drawing.ColorTranslator]::FromHtml($BottomHex)
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 45.0)
    $g.FillRectangle($lg, $rect)
    $lg.Dispose()

    foreach ($b in $Blobs) {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddEllipse($b.X - $b.R, $b.Y - $b.R, $b.R * 2, $b.R * 2)
        $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
        $pgb.CenterColor = [System.Drawing.Color]::FromArgb($b.A, $b.Cr, $b.Cg, $b.Cb)
        $n = $path.PointCount
        $sur = New-Object 'System.Drawing.Color[]' $n
        for ($i = 0; $i -lt $n; $i++) {
            $sur[$i] = [System.Drawing.Color]::FromArgb(0, $b.Cr, $b.Cg, $b.Cb)
        }
        $pgb.SurroundColors = $sur
        $g.FillPath($pgb, $path)
        $pgb.Dispose()
        $path.Dispose()
    }

    $g.Dispose()

    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
        Where-Object { $_.MimeType -eq 'image/jpeg' } | Select-Object -First 1
    $ep = New-Object System.Drawing.Imaging.EncoderParameters(1)
    $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
        [System.Drawing.Imaging.Encoder]::Quality, [int64]90)
    $dest = Join-Path $outDir $File
    $bmp.Save($dest, $codec, $ep)
    $bmp.Dispose()
    Write-Host ("{0}  {1:N0} KB" -f $File, ((Get-Item $dest).Length / 1KB))
}

# dark group
New-Wallpaper -File 'aurora.jpg' -TopHex '#0B1026' -BottomHex '#061218' -Blobs @(
    (New-Blob 420 380 620 120 22 199 160),
    (New-Blob 1400 300 560 130 124 58 237),
    (New-Blob 1050 820 640 100 34 211 238)
)

New-Wallpaper -File 'nebula.jpg' -TopHex '#1A0B2E' -BottomHex '#0A0618' -Blobs @(
    (New-Blob 1500 420 700 125 124 58 237),
    (New-Blob 560 700 520 110 217 70 239),
    (New-Blob 900 220 480 95 59 130 246)
)

New-Wallpaper -File 'midnight.jpg' -TopHex '#0A1B2E' -BottomHex '#050B14' -Blobs @(
    (New-Blob 360 260 540 115 37 99 235),
    (New-Blob 1450 760 620 105 6 182 212),
    (New-Blob 900 540 460 60 61 211 34)
)

# light group
New-Wallpaper -File 'dawn.jpg' -TopHex '#FFE9D6' -BottomHex '#FFD3BC' -Blobs @(
    (New-Blob 1420 320 620 130 251 146 60),
    (New-Blob 500 780 560 100 244 114 182),
    (New-Blob 880 480 700 105 250 204 21)
)

New-Wallpaper -File 'mist.jpg' -TopHex '#E9F0F8' -BottomHex '#D8E4F2' -Blobs @(
    (New-Blob 480 340 600 135 147 197 253),
    (New-Blob 1420 760 560 100 94 234 212),
    (New-Blob 1050 300 520 110 165 180 252)
)

New-Wallpaper -File 'sand.jpg' -TopHex '#FDF6EC' -BottomHex '#F3E3CD' -Blobs @(
    (New-Blob 1360 340 600 100 245 158 11),
    (New-Blob 560 760 540 90 253 164 175),
    (New-Blob 980 260 520 85 134 239 172)
)
