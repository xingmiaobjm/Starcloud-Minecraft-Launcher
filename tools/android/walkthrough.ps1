# Android device walkthrough: get a phone to the point where the Web UI shell can be checked.
#
# WHY ASCII ONLY: Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI, so Chinese
# comments turn into mojibake and the parser breaks (same rule as tools/build-installer.ps1
# in the desktop repo).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/walkthrough.ps1
#   powershell -ExecutionPolicy Bypass -File tools/walkthrough.ps1 -Serial 192.168.1.20:5555
#
# Steps: wait for a device -> install app-debug.apk -> launch WebUiActivity ->
#        screenshot + dump StarcloudWeb logs -> stream logcat.
#
# Note: versions.install / launch.start need the component bundles
# (JRE / LWJGL / Mesa) deployed on the device; they are not in the repo:
#   powershell -ExecutionPolicy Bypass -File components/fetch-components.ps1

param(
    [string]$Serial = '',
    [int]$WaitSeconds = 60
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apk = Join-Path $root 'Starcloud.Launcher.Compose/app/build/outputs/apk/debug/app-debug.apk'
$pkg = 'cn.starcloud.launcher.compose'
$activity = "$pkg/cn.starcloud.launcher.web.WebUiActivity"

$adb = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
if (-not (Test-Path -LiteralPath $adb)) {
    $cmd = Get-Command adb.exe -ErrorAction SilentlyContinue
    if (-not $cmd) { throw 'adb.exe not found (PATH or %LOCALAPPDATA%\Android\Sdk\platform-tools)' }
    $adb = $cmd.Source
}

function Invoke-Adb {
    param([string[]]$Arguments)
    if ($Serial) { & $adb -s $Serial @Arguments } else { & $adb @Arguments }
}

# 1. wait for a device
$device = $null
for ($i = 0; $i -lt $WaitSeconds; $i++) {
    $lines = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\sdevice$' }
    if ($lines) {
        $device = ($lines[0] -split '\s+')[0]
        break
    }
    if ($i -eq 0) { Write-Host 'Waiting for a device (USB debugging or adb connect) ...' }
    Start-Sleep -Seconds 1
}
if (-not $device) {
    throw "No Android device after $WaitSeconds s. Enable USB debugging, or run: adb connect HOST:PORT"
}
if (-not $Serial) { $Serial = $device }
Write-Host "Device: $Serial"

if (-not (Test-Path -LiteralPath $apk)) {
    throw "APK not found: $apk`nBuild it first: cd Starcloud.Launcher.Compose; gradle :app:assembleDebug"
}

# 2. install
Write-Host 'Installing APK ...'
Invoke-Adb @('install', '-r', '-d', $apk) | Out-Host

# 3. launch the web shell
Write-Host 'Starting Web UI shell ...'
Invoke-Adb @('shell', 'am', 'start', '-n', $activity) | Out-Host
Start-Sleep -Seconds 4

# 4. screenshot + bridge logs
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$shot = Join-Path $root "tools/walkthrough-$stamp.png"
$bytes = & $adb -s $Serial exec-out screencap -p
[System.IO.File]::WriteAllBytes($shot, [byte[]]$bytes)
Write-Host "Screenshot: $shot"

Write-Host '--- StarcloudWeb (last 40 lines) ---'
Invoke-Adb @('logcat', '-d', '-s', 'StarcloudWeb') | Select-Object -Last 40 | Out-Host

Write-Host ''
Write-Host 'Manual checks next:'
Write-Host '  versions.install : page -> Download -> pick a version -> install (expect install.progress)'
Write-Host '  launch.start     : page -> Launch game (expect game.log / game.progress)'
Write-Host ''
Write-Host 'Streaming logcat (Ctrl+C to stop):'
Invoke-Adb @('logcat', '-s', 'StarcloudWeb', 'AndroidRuntime:E')
