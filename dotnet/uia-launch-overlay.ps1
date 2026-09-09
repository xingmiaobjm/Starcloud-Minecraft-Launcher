# LaunchOverlay 冒烟（T6）：
#   默认全流程：启动应用 → 选版本 → 点「启动游戏」→ 覆盖层出现（正在启动+进度条）
#   → 标题流转「已启动游戏」→ 步骤「游戏运行中」→ 点「返回主页」→ 覆盖层收起 + StatusText「游戏运行中」
#   -Cancel：覆盖层出现后点「取消」→ 按钮变「正在取消…」→ 收层 + StatusText「已取消启动」
param([switch]$Cancel, [int]$TimeoutSec = 150)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Get-Process -Name "Starcloud.App" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$exe = Join-Path $PSScriptRoot "src\Starcloud.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Starcloud.App.exe"
if (-not (Test-Path $exe)) { Write-Output "FAIL: exe not found: $exe"; exit 1 }

# 记录已有 java 进程：脚本结束时只清理本次新拉起的
$javaBefore = @(Get-Process -Name java -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })

$proc = Start-Process -FilePath $exe -PassThru
Write-Output ("APP PID: {0}" -f $proc.Id)

$root = [System.Windows.Automation.AutomationElement]::RootElement

function Find-ByName([System.Windows.Automation.AutomationElement]$r, [string]$name, $ctype) {
    $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $cond = $nc
    if ($ctype) {
        $tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctype)
        $cond = New-Object System.Windows.Automation.AndCondition($nc, $tc)
    }
    return $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-AutoId([System.Windows.Automation.AutomationElement]$r, [string]$autoId) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $autoId)
    return $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Wait-Element([System.Windows.Automation.AutomationElement]$r, [string]$name, $ctype, [int]$seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $el = Find-ByName $r $name $ctype
        if ($el) { return $el }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Gone-ByName([System.Windows.Automation.AutomationElement]$r, [string]$name) {
    return ($null -eq (Find-ByName $r $name $null))
}

# 等主窗口
$win = $null
$deadline = [DateTime]::UtcNow.AddSeconds(40)
while ([DateTime]::UtcNow -lt $deadline -and -not $win) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { Start-Sleep -Milliseconds 500 }
}
if (-not $win) { Write-Output "FAIL: window not found"; exit 1 }
Write-Output ("WIN: {0}" -f $win.Current.Name)
Start-Sleep -Seconds 2

# 选版本列表第一项（版本异步加载，重试）
$selected = $false
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while ([DateTime]::UtcNow -lt $deadline -and -not $selected) {
    $list = Find-AutoId $win "VersionList"
    if ($list) {
        $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $item = $list.FindFirst([System.Windows.Automation.TreeScope]::Children, $ic)
        if ($item) {
            try {
                ($item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                $selected = $true
                Write-Output ("VERSION SELECTED: {0}" -f $item.Current.Name)
            } catch { }
        }
    }
    if (-not $selected) { Start-Sleep -Milliseconds 800 }
}
if (-not $selected) { Write-Output "FAIL: no version item to select"; exit 1 }
Start-Sleep -Seconds 1

# 点「启动游戏」（等按钮可用）
$btn = $null
$deadline = [DateTime]::UtcNow.AddSeconds(15)
while ([DateTime]::UtcNow -lt $deadline) {
    $b = Find-ByName $win "启动游戏" ([System.Windows.Automation.ControlType]::Button)
    if ($b -and $b.Current.IsEnabled) { $btn = $b; break }
    Start-Sleep -Milliseconds 500
}
if (-not $btn) { Write-Output "FAIL: launch button not found or disabled"; exit 1 }
($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Write-Output "LAUNCH CLICKED"

# 覆盖层出现：标题「正在启动」+ 进度条
$t = Wait-Element $win "正在启动" ([System.Windows.Automation.ControlType]::Text) 15
if (-not $t) { Write-Output "FAIL: overlay title not found"; exit 1 }
$pb = Find-AutoId $win "LaunchProgress"
if (-not $pb) {
    $pbc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ProgressBar)
    $pb = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $pbc)
}
if (-not $pb) { Write-Output "FAIL: progress bar not found"; exit 1 }
Write-Output "OVERLAY VISIBLE (title=正在启动, progressbar ok)"

if ($Cancel) {
    Start-Sleep -Seconds 2
    $cb = Find-ByName $win "取消" ([System.Windows.Automation.ControlType]::Button)
    if (-not $cb) { Write-Output "FAIL: cancel button not found"; exit 1 }
    ($cb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Write-Output "CANCEL CLICKED"
    if (Wait-Element $win "正在取消…" ([System.Windows.Automation.ControlType]::Button) 5) {
        Write-Output "CANCEL STATE OK (正在取消…)"
    }
    $ok = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline) {
        $gone = (Gone-ByName $win "正在启动") -and (Gone-ByName $win "已启动游戏") -and (Gone-ByName $win "启动失败")
        $st = Find-ByName $win "已取消启动" ([System.Windows.Automation.ControlType]::Text)
        if ($gone -and $st) { $ok = $true; break }
        Start-Sleep -Milliseconds 800
    }
    if (-not $ok) { Write-Output "FAIL: cancel did not collapse overlay / status text missing"; exit 1 }
    Write-Output "CANCEL OK: overlay collapsed, StatusText=已取消启动"
} else {
    # 标题流转：等待窗口期间即变「已启动游戏」
    $t2 = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        $fail = Find-ByName $win "启动失败" ([System.Windows.Automation.ControlType]::Text)
        if ($fail) { Write-Output "FAIL: launch failed page shown"; exit 1 }
        $t2 = Find-ByName $win "已启动游戏" ([System.Windows.Automation.ControlType]::Text)
        if ($t2) { break }
        Start-Sleep -Milliseconds 800
    }
    if (-not $t2) { Write-Output "FAIL: title never became '已启动游戏'"; exit 1 }
    Write-Output "TITLE TRANSITION OK (已启动游戏)"

    # 等 Running 态（窗口已检测 / 兜底超时）：步骤文字变「游戏运行中」
    $running = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        $running = Find-ByName $win "游戏运行中" ([System.Windows.Automation.ControlType]::Text)
        if ($running) { break }
        Start-Sleep -Milliseconds 1000
    }
    if (-not $running) { Write-Output "FAIL: step text never became '游戏运行中'"; exit 1 }
    Write-Output "RUNNING STATE OK"

    # 点「返回主页」
    $bb = Wait-Element $win "返回主页" ([System.Windows.Automation.ControlType]::Button) 15
    if (-not $bb) { Write-Output "FAIL: back button not found"; exit 1 }
    ($bb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Write-Output "BACK CLICKED"

    # 覆盖层收起 + StatusText「游戏运行中」
    $ok = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        $goneTitle = Gone-ByName $win "已启动游戏"
        $st = Find-ByName $win "游戏运行中" ([System.Windows.Automation.ControlType]::Text)
        if ($goneTitle -and $st) { $ok = $true; break }
        Start-Sleep -Milliseconds 800
    }
    if (-not $ok) { Write-Output "FAIL: overlay not collapsed / StatusText missing"; exit 1 }
    Write-Output "BACK OK: overlay collapsed, StatusText=游戏运行中"
}

# 清理：只杀本次新拉起的 java（应用保留给手工观察）
$newJava = @(Get-Process -Name java -ErrorAction SilentlyContinue | Where-Object { $javaBefore -notcontains $_.Id })
foreach ($j in $newJava) { try { $j.Kill() } catch { } }
if ($newJava.Count -gt 0) { Write-Output ("CLEANUP: killed {0} java process(es)" -f $newJava.Count) }

Write-Output "PASS"
exit 0
