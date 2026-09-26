# Generates Assets/AppIcon.ico from the Starcloud brand mark.
#
# What the icon is (no new artwork is invented here, only plate + brand pieces):
#   plate  #101318 rounded square, same plate colour as the brand's
#          starcloud-round-dark.svg, with a white 14% hairline edge so the
#          silhouette still reads on a dark taskbar.
#   mark   starcloud-mark-dark.svg (light strokes) inside the plate, at the same
#          0.9 scale / centring the brand round variant uses.
#   small  16 and 24 px use starcloud-mark-small.svg (single raised block + star):
#          the main mark's 9/400 strokes are thinner than a pixel at that size.
#          32 px is the brand's documented switch-over point.
#
# Every ICO entry is rasterised by headless Edge at its exact pixel size, so
# 16/32/48 are drawn for that size instead of being downscales of one master.
#
# ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI,
# and a mangled CJK comment can swallow the next line (see gen-wallpapers.ps1).
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\gen-app-icon.ps1
Add-Type -AssemblyName System.Drawing

$root     = Split-Path -Parent $PSScriptRoot
$assetDir = Join-Path $root 'dotnet\src\Starcloud.App\Assets'
$brandDir = Join-Path $assetDir 'Brand'
$outFile  = Join-Path $assetDir 'AppIcon.ico'

# Fresh subfolder per run: no stale PNG can be mistaken for this run's output.
$stamp    = Get-Date -Format 'yyyyMMdd-HHmmss'
$workDir  = Join-Path $root ("dotnet\src\Starcloud.App\obj\appicon\" + $stamp)

$sizes = @(16, 24, 32, 48, 64, 128, 256)

# --- locate the headless renderer ----------------------------------------
$edge = $null
foreach ($c in @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'))) {
    if (Test-Path -LiteralPath $c) { $edge = $c; break }
}
if (-not $edge) { throw 'msedge.exe not found; cannot rasterise the brand SVG.' }

# --- brand pieces (the two source files live next to the app assets) ------
$fullSvg = Get-Content -LiteralPath (Join-Path $brandDir 'starcloud-mark-dark.svg') -Raw
$mark = [regex]::Match($fullSvg, '<g\s+transform="translate\(-6,-4\)">[\s\S]*</g>').Value
if (-not $mark) { throw 'mark group not found in starcloud-mark-dark.svg' }

$smallSvg = Get-Content -LiteralPath (Join-Path $brandDir 'starcloud-mark-small.svg') -Raw
$block = [regex]::Match($smallSvg, '<g\s+transform="translate\(601,423\)[\s\S]*</g>').Value
if (-not $block) { throw 'block group not found in starcloud-mark-small.svg' }

$plate     = '<rect x="16" y="16" width="480" height="480" rx="104" fill="#101318"/>'
$plateEdge = '<rect x="16" y="16" width="480" height="480" rx="104" fill="none"' +
             ' stroke="#FFFFFF" stroke-opacity="0.14" stroke-width="2.5"/>'

function Get-IconSvg {
    param([int]$Size)
    $markup = if ($Size -lt 32) {
        '<g transform="translate(256,256) scale(1.55) translate(-601,-423)">' + $block + '</g>'
    } else {
        '<g transform="translate(-206.87,-193.73) scale(0.9)">' + $mark + '</g>'
    }
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="' + $Size +
        '" height="' + $Size + '">' + $plate + $markup + $plateEdge + '</svg>'
}

# --- rasterise every size ------------------------------------------------
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
$pngs = @{}
foreach ($s in $sizes) {
    $html = '<!doctype html><html><head><meta charset="utf-8"><style>' +
            'html,body{margin:0;padding:0;overflow:hidden;background:transparent}' +
            'svg{display:block;width:' + $s + 'px;height:' + $s + 'px}</style></head><body>' +
            (Get-IconSvg -Size $s) + '</body></html>'
    $htmlPath = Join-Path $workDir ("icon-$s.html")
    $pngPath  = Join-Path $workDir ("icon-$s.png")
    Set-Content -LiteralPath $htmlPath -Value $html -Encoding UTF8
    $uri = 'file:///' + ($htmlPath -replace '\\', '/')
    & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
        --default-background-color=00000000 --window-size="$s,$s" "--screenshot=$pngPath" $uri |
        Out-Null
    for ($i = 0; $i -lt 60 -and -not (Test-Path -LiteralPath $pngPath); $i++) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath $pngPath)) { throw "headless render failed for ${s}px" }
    $pngs[$s] = $pngPath
    Write-Host ("  rendered {0,3}px  {1,6:N0} B" -f $s, (Get-Item -LiteralPath $pngPath).Length)
}

# Packs a bitmap into a 32bpp BITMAPINFOHEADER DIB (the classic ICO payload:
# XOR bitmap bottom-up + a zeroed AND mask, alpha channel drives transparency).
function ConvertTo-IconDib {
    param([System.Drawing.Bitmap]$Bmp)

    $w = $Bmp.Width
    $h = $Bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $Bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
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
    $src = [System.Drawing.Image]::FromFile($pngs[$s])
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImageUnscaled($src, 0, 0)
    $g.Dispose()
    $src.Dispose()
    $images += , @{ Size = $s; Data = (ConvertTo-IconDib -Bmp $bmp) }
    $bmp.Dispose()
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

Write-Host ("AppIcon.ico  {0} sizes ({1})  {2:N0} KB  from run {3}" -f `
    $images.Count, ($sizes -join '/'), ((Get-Item -LiteralPath $outFile).Length / 1KB), $stamp)
