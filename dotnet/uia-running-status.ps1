# RunningStatusPage smoke (T12):
#   1) nav pane has a "running status" item, immediately after "multiplayer"
#   2) selecting it renders the page (empty state, or hero when a game is running)
#   3) "recent runs" section and "sampling settings" button exist
# Pure ASCII on purpose: PowerShell 5.1 mis-parses non-ASCII source. CJK strings are
# built from code points via S("8FD0...") so the file stays byte-clean.
param([int]$TimeoutSec = 60)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function S([string]$hex) {
    $out = ""
    for ($i = 0; $i -lt $hex.Length; $i += 4) {
        $out += [char][Convert]::ToInt32($hex.Substring($i, 4), 16)
    }
    return $out
}

$T_RUNNING_STATUS = S "8FD0884C72B66001"                 # running status (nav + heading)
$T_MULTIPLAYER    = S "8054673A"                         # multiplayer
$T_HERO_CHIP      = S "8FD0884C4E2D"                     # "running" chip on hero card
$T_EMPTY_TITLE    = S "73B057286CA167096E38620F57288FD0884C"  # no game is running right now
$T_RECENT         = S "67008FD18FD0884C"                 # recent runs
$T_SAMPLE_BTN     = S "91C768378BBE7F6E"                 # sampling settings
$T_GO_HOME        = S "53BB99969875"                     # go to home

Get-Process -Name "Starcloud.App" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$exe = Join-Path $PSScriptRoot "src\Starcloud.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Starcloud.App.exe"
if (-not (Test-Path $exe)) { Write-Output "FAIL: exe not found: $exe"; exit 1 }

$proc = Start-Process -FilePath $exe -PassThru
Write-Output ("APP PID: {0}" -f $proc.Id)

$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $null
$deadline = [DateTime]::UtcNow.AddSeconds(40)
while ([DateTime]::UtcNow -lt $deadline -and -not $win) {
    $wc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $wc)
    if (-not $win) { Start-Sleep -Milliseconds 500 }
}
if (-not $win) { Write-Output "FAIL: window not found"; exit 1 }
Write-Output ("WIN: {0}" -f $win.Current.Name)
Start-Sleep -Seconds 3

# Text lookup by exact name (ControlType.Text), and a generic name lookup.
function Find-Text([string]$name) {
    $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $and = New-Object System.Windows.Automation.AndCondition($nc, $tc)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}

function Find-Any([string]$name) {
    $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc)
}

function Wait-Text([string]$name, [int]$seconds) {
    $dl = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $dl) {
        $el = Find-Text $name
        if ($el) { return $el }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# --- 1) nav order: the item right after "multiplayer" must be "running status" ---
$liCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)
$names = @()
foreach ($i in $items) { $names += $i.Current.Name }
Write-Output ("LISTITEM COUNT: {0}" -f $names.Count)

$mpIdx = -1
for ($i = 0; $i -lt $names.Count; $i++) {
    if ($names[$i] -eq $T_MULTIPLAYER) { $mpIdx = $i; break }
}
if ($mpIdx -lt 0) {
    Write-Output "FAIL: nav item 'multiplayer' not found"
    Write-Output ("nav: " + (($names | Select-Object -First 20) -join " | "))
    exit 1
}
if ($mpIdx + 1 -ge $names.Count) {
    Write-Output "FAIL: no nav item after 'multiplayer'"
    exit 1
}
$next = $items.Item($mpIdx + 1)
$nextName = $next.Current.Name
if ($nextName -and $nextName -ne $T_RUNNING_STATUS) {
    Write-Output ("FAIL: item after 'multiplayer' is not 'running status' (got '{0}')" -f $nextName)
    Write-Output ("nav: " + (($names | Select-Object -First 20) -join " | "))
    exit 1
}
Write-Output "NAV ORDER OK (multiplayer -> running status)"

# --- 2) select it and wait for page content ---
$selected = $false
try {
    ($next.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    $selected = $true
} catch { }
if (-not $selected) {
    try {
        ($next.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        $selected = $true
    } catch { }
}
if (-not $selected) { Write-Output "FAIL: could not select running status nav item"; exit 1 }

$mode = ""
$dl = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
while ([DateTime]::UtcNow -lt $dl) {
    if (Find-Text $T_EMPTY_TITLE) { $mode = "empty"; break }
    if (Find-Text $T_HERO_CHIP) { $mode = "running"; break }
    Start-Sleep -Milliseconds 500
}
if (-not $mode) { Write-Output "FAIL: running status page content not found"; exit 1 }
Write-Output ("PAGE OK (mode={0})" -f $mode)

# --- 3) page furniture ---
if (-not (Wait-Text $T_RECENT 15)) { Write-Output "FAIL: 'recent runs' section not found"; exit 1 }
Write-Output "RECENT RUNS OK"

if (-not (Find-Any $T_SAMPLE_BTN)) { Write-Output "FAIL: 'sampling settings' button not found"; exit 1 }
Write-Output "SAMPLING SETTINGS OK"

if ($mode -eq "empty") {
    if (-not (Find-Any $T_GO_HOME)) { Write-Output "FAIL: 'go home' action not found in empty state"; exit 1 }
    Write-Output "EMPTY STATE ACTION OK"
}

Write-Output "PASS"
exit 0
