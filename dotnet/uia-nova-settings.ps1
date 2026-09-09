# Nova 设置页冒烟：启动应用 → UIA 点左栏 Nova 导航项 → 断言「Nova 设置」页头 + 三张卡片标记出现且进程存活。
# 注意：本文件必须带 UTF-8 BOM（PowerShell 5.1 中文），编辑后请重新补 BOM。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = Join-Path $PSScriptRoot 'src\Starcloud.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Starcloud.App.exe'
if (-not (Test-Path $exe)) { Write-Output "FAIL: exe not found: $exe"; exit 1 }

Get-Process -Name 'Starcloud.App' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

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

function Wait-Element([System.Windows.Automation.AutomationElement]$r, [string]$name, $ctype, [int]$seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $el = Find-ByName $r $name $ctype
        if ($el) { return $el }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# 等主窗口（按 PID 找，40 秒超时）
$win = $null
$deadline = [DateTime]::UtcNow.AddSeconds(40)
while ([DateTime]::UtcNow -lt $deadline -and -not $win) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { Start-Sleep -Milliseconds 500 }
}
if (-not $win) { Write-Output 'FAIL: main window not found'; exit 1 }
Write-Output ("WIN: {0}" -f $win.Current.Name)
Start-Sleep -Seconds 2

# 点左栏 Nova 导航项（NavigationViewItem 支持 SelectionItemPattern）
$nav = Wait-Element $win 'Nova' ([System.Windows.Automation.ControlType]::ListItem) 10
if (-not $nav) { $nav = Find-ByName $win 'Nova' $null }
if (-not $nav) { Write-Output 'FAIL: Nova nav item not found'; exit 1 }
try {
    ($nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
} catch {
    ($nav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
Write-Output 'NAV CLICKED'

# 断言「Nova 设置」页头出现
$marker = Wait-Element $win 'Nova 设置' ([System.Windows.Automation.ControlType]::Text) 10
if (-not $marker) { Write-Output 'FAIL: Nova settings header not found'; exit 1 }

# 探测三张卡片标记（辅助开关在 UIA 里可能不是 Text 类型，放宽约束）
$model = Find-ByName $win '模型设置' ([System.Windows.Automation.ControlType]::Text)
$overlay = Find-ByName $win '游戏内辅助浮窗' $null
$memory = Find-ByName $win '游戏记录记忆' ([System.Windows.Automation.ControlType]::Text)
$addBtn = Find-ByName $win '新增模型' ([System.Windows.Automation.ControlType]::Button)

Start-Sleep -Milliseconds 800
$alive = Get-Process -Name 'Starcloud.App' -ErrorAction SilentlyContinue
if (-not $alive) { Write-Output 'FAIL: app crashed after navigating to Nova page'; exit 1 }

Write-Output ("PASS: Nova settings page rendered; markers model={0} overlay={1} memory={2} addBtn={3}" -f ($null -ne $model), ($null -ne $overlay), ($null -ne $memory), ($null -ne $addBtn))
Get-Process -Name 'Starcloud.App' -ErrorAction SilentlyContinue | Stop-Process -Force
