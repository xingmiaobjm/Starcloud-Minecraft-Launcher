# Exports the Starcloud product icon as a PNG ladder from the brand mark.
#
# Nothing new is drawn here: it is the same plate + mark composition that
# tools\gen-app-icon.ps1 packs into AppIcon.ico, only written out as PNGs so
# they can be used on a store page, a website or in docs.
#
#   dark plate   #101318 rounded square + starcloud-mark-dark.svg  (the shipped icon)
#   light plate  #F5F6F8 rounded square + starcloud-mark-light.svg (light surfaces)
#   mark only    transparent background, both stroke colours
#
# Below 32 px the single-block mark is used: the main mark's 9/400 strokes are
# thinner than a pixel there (brand rule, same switch point as the .ico).
#
# ASCII-only on purpose (PowerShell 5.1 reads BOM-less .ps1 as ANSI).
# Usage: powershell -ExecutionPolicy Bypass -File tools\export-icon-png.ps1
Add-Type -AssemblyName System.Drawing

$root     = Split-Path -Parent $PSScriptRoot
$brandDir = Join-Path $root 'dotnet\src\Starcloud.App\Assets\Brand'
$outDir   = Join-Path $root 'docs\assets\brand'
$workDir  = Join-Path $root 'dotnet\src\Starcloud.App\obj\icon-preview\work'

$sizes  = @(1024, 512, 256, 128, 64, 48, 32, 24, 16)
$markSizes = @(512, 256, 128)

$edge = $null
foreach ($c in @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'))) {
    if (Test-Path -LiteralPath $c) { $edge = $c; break }
}
if (-not $edge) { throw 'msedge.exe not found; cannot rasterise the brand SVG.' }

# --- brand pieces ---------------------------------------------------------
function Get-Group {
    param([string]$File, [string]$Pattern)
    $svg = Get-Content -LiteralPath (Join-Path $brandDir $File) -Raw
    $m = [regex]::Match($svg, $Pattern)
    if (-not $m.Success) { throw "group not found in $File" }
    $m.Value
}
$markDark  = Get-Group 'starcloud-mark-dark.svg'  '<g\s+transform="translate\(-6,-4\)">[\s\S]*</g>'
$markLight = Get-Group 'starcloud-mark-light.svg' '<g\s+transform="translate\(-6,-4\)">[\s\S]*</g>'
$block     = Get-Group 'starcloud-mark-small.svg' '<g\s+transform="translate\(601,423\)[\s\S]*</g>'

# Plate geometry is copied from the shipped icon: 16 px inset, rx 104/512.
function Get-PlatedSvg {
    param([int]$Size, [string]$Variant)
    $markup = if ($Size -lt 32) {
        '<g transform="translate(256,256) scale(1.55) translate(-601,-423)">' + $block + '</g>'
    } else {
        $m = if ($Variant -eq 'light') { $markLight } else { $markDark }
        '<g transform="translate(-206.87,-193.73) scale(0.9)">' + $m + '</g>'
    }
    $plate = if ($Variant -eq 'light') {
        '<rect x="16" y="16" width="480" height="480" rx="104" fill="#F5F6F8"/>'
    } else {
        '<rect x="16" y="16" width="480" height="480" rx="104" fill="#101318"/>'
    }
    $rim = if ($Variant -eq 'light') {
        '<rect x="16" y="16" width="480" height="480" rx="104" fill="none"' +
        ' stroke="#111418" stroke-opacity="0.10" stroke-width="2.5"/>'
    } else {
        '<rect x="16" y="16" width="480" height="480" rx="104" fill="none"' +
        ' stroke="#FFFFFF" stroke-opacity="0.14" stroke-width="2.5"/>'
    }
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="' + $Size +
        '" height="' + $Size + '">' + $plate + $markup + $rim + '</svg>'
}

function Get-MarkSvg {
    param([int]$Size, [string]$Variant)
    $m = if ($Variant -eq 'light') { $markLight } else { $markDark }
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="316 297 400 400" width="' + $Size +
        '" height="' + $Size + '">' + $m + '</svg>'
}

# --- rasterise ------------------------------------------------------------
function Export-Png {
    param([string]$Markup, [int]$Size, [string]$Name)

    # Transparent background: the rounded plate's corners must not carry a
    # colour, or the PNG can only sit on the surface it was rendered against.
    $html = '<!doctype html><html><head><meta charset="utf-8"><style>' +
            'html,body{margin:0;padding:0;overflow:hidden;background:transparent}' +
            'svg{display:block}</style></head><body>' + $Markup + '</body></html>'
    $htmlPath = Join-Path $workDir ($Name + '.html')
    $pngPath  = Join-Path $outDir  ($Name + '.png')
    Set-Content -LiteralPath $htmlPath -Value $html -Encoding UTF8
    $uri = 'file:///' + ($htmlPath -replace '\\', '/')
    # Interpolated strings on purpose: '+ "," +' would parse as the comma
    # operator and hand Edge several positional args ("Multiple targets").
    $edgeArgs = @('--headless=new', '--disable-gpu', '--hide-scrollbars',
                  '--force-device-scale-factor=1', "--window-size=$Size,$Size",
                  '--default-background-color=00000000',
                  "--screenshot=$($pngPath -replace '\\', '/')")
    & $edge @edgeArgs $uri | Out-Null
    for ($i = 0; $i -lt 80 -and -not (Test-Path -LiteralPath $pngPath); $i++) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath $pngPath)) { throw "render failed for $Name" }
    $px = [System.Drawing.Image]::FromFile($pngPath)
    Write-Host ("  {0,-30} {1,5}x{2,-5} {3,7:N0} B" -f `
        ($Name + '.png'), $px.Width, $px.Height, (Get-Item -LiteralPath $pngPath).Length)
    $px.Dispose()
}

New-Item -ItemType Directory -Force -Path $outDir, $workDir | Out-Null

Write-Host 'product icon (dark plate, as shipped in AppIcon.ico)'
foreach ($s in $sizes) { Export-Png (Get-PlatedSvg $s 'dark') $s "starcloud-icon-dark-$s" }

Write-Host 'product icon (light plate)'
foreach ($s in $sizes) { Export-Png (Get-PlatedSvg $s 'light') $s "starcloud-icon-light-$s" }

Write-Host 'mark only (transparent)'
foreach ($s in $markSizes) { Export-Png (Get-MarkSvg $s 'light') $s "starcloud-mark-light-$s" }
foreach ($s in $markSizes) { Export-Png (Get-MarkSvg $s 'dark')  $s "starcloud-mark-dark-$s" }

Write-Host ''
Write-Host ("exported to {0}" -f $outDir)
