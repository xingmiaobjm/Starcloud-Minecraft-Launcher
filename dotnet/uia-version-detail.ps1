# VersionDetail 冒烟（T9）：
#   启动应用 → 侧栏「版本」→ 列表第一项 → 详情子页（各卡片 + 返回按钮断言）
#   → 修改「最大内存」→ 返回列表 → 重进详情（值保留 + version-settings.json 落盘断言）
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Get-Process -Name "Starcloud.App" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$exe = Join-Path $PSScriptRoot "src\Starcloud.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Starcloud.App.exe"
if (-not (Test-Path $exe)) { Write-Output "FAIL: exe not found: $exe"; exit 1 }

$proc = Start-Process -FilePath $exe -PassThru
Write-Output ("APP PID: {0}" -f $proc.Id)

$root = [System.Windows.Automation.AutomationElement]::RootElement

function Find-ByName($r, $name, $ctype) {
    $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $cond = $nc
    if ($ctype) {
        $tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctype)
        $cond = New-Object System.Windows.Automation.AndCondition($nc, $tc)
    }
    return $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-AutoId($r, $id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Wait-ByName($r, $name, $ctype, $seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $el = Find-ByName $r $name $ctype
        if ($el) { return $el }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

# 支持 SelectionItemPattern 的导航项（排除同名 TextBlock）
function Find-NavItem($r, $name) {
    $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $all = $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nc)
    foreach ($a in $all) {
        $p = $null
        if ($a.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { return $a }
    }
    return $null
}

function Get-Num($el) {
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { return [string]$p.Current.Value }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$p)) { return [string]$p.Current.Value }
    return $null
}

function Set-Num($el, $v) {
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { $p.SetValue([string]$v); return $true }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$p)) { $p.SetValue([double]$v); return $true }
    return $false
}

function Get-FirstRow($win) {
    $list = Find-AutoId $win "VersionListView"
    if (-not $list) { return $null }
    $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $first = $list.FindFirst([System.Windows.Automation.TreeScope]::Children, $ic)
    if (-not $first) { $first = $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $ic) }
    return $first
}

function Dump-Rows($win) {
    $list = Find-AutoId $win "VersionListView"
    if (-not $list) {
        Write-Output "DIAG: VersionListView not found; dumping texts"
        $ts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
        $i = 0
        foreach ($t in $ts) {
            if ($t.Current.Name -and $i -lt 30) { Write-Output ("  text: {0}" -f $t.Current.Name); $i++ }
        }
        $lv = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)))
        Write-Output ("DIAG: List count={0}" -f $lv.Count)
        foreach ($l in $lv) { Write-Output ("  list autoId='{0}' name='{1}'" -f $l.Current.AutomationId, $l.Current.Name) }
        return
    }
    Write-Output ("DIAG: list name='{0}' type={1}" -f $list.Current.Name, $list.Current.ControlType.ProgrammaticName)
    $kids = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    Write-Output ("DIAG: children={0}" -f $kids.Count)
    foreach ($k in $kids) {
        Write-Output ("  child: name='{0}' type={1}" -f $k.Current.Name, $k.Current.ControlType.ProgrammaticName)
    }
    $li = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    Write-Output ("DIAG: descendant ListItems={0}" -f $li.Count)
}

# 进入详情页：优先列表项 Invoke（IsItemClickEnabled），失败回退行菜单「版本详情」
function Open-Detail($win, $item) {
    $ip = $null
    if ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$ip)) {
        $ip.Invoke()
        if (Wait-ByName $win "返回版本列表" ([System.Windows.Automation.ControlType]::Button) 8) { return $true }
    }
    Write-Output "INFO: item invoke did not open detail, fallback to row menu"
    $more = $item.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "更多操作")),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))
    if (-not $more) { return $false }
    ($more.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 800
    $mi = Find-ByName $root "版本详情" ([System.Windows.Automation.ControlType]::MenuItem)
    if (-not $mi) { $mi = Find-ByName $win "版本详情" ([System.Windows.Automation.ControlType]::MenuItem) }
    if (-not $mi) { return $false }
    ($mi.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    return ($null -ne (Wait-ByName $win "返回版本列表" ([System.Windows.Automation.ControlType]::Button) 8))
}

# ── 等主窗口 ──
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

# ── 侧栏「版本」 ──
$nav = $null
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while ([DateTime]::UtcNow -lt $deadline -and -not $nav) {
    $nav = Find-NavItem $win "版本"
    if (-not $nav) { Start-Sleep -Milliseconds 500 }
}
if (-not $nav) { Write-Output "FAIL: nav item '版本' not found"; exit 1 }
($nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Write-Output "NAV OK (版本)"

# ── 列表第一项 ──
$item = $null
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while ([DateTime]::UtcNow -lt $deadline -and -not $item) {
    $item = Get-FirstRow $win
    if (-not $item) { Start-Sleep -Milliseconds 600 }
}
if (-not $item) { Dump-Rows $win; Write-Output "FAIL: no version row found"; exit 1 }

$versionId = $null
$texts = $item.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
foreach ($t in $texts) { if ($t.Current.Name) { $versionId = $t.Current.Name; break } }
Write-Output ("VERSION ID: {0}" -f $versionId)

if (-not (Open-Detail $win $item)) { Write-Output "FAIL: detail page not opened"; exit 1 }
Write-Output "DETAIL OPENED"

# ── 详情页断言：卡片标题 + 返回按钮 + 关键控件 ──
foreach ($card in @("概览", "内容", "游戏设置", "操作")) {
    if (-not (Find-ByName $win $card ([System.Windows.Automation.ControlType]::Text))) {
        Write-Output ("FAIL: card '{0}' not found" -f $card); exit 1
    }
}
$back = Find-ByName $win "返回版本列表" ([System.Windows.Automation.ControlType]::Button)
if (-not $back) { Write-Output "FAIL: back button not found"; exit 1 }
if (-not $back.Current.IsEnabled) { Write-Output "FAIL: back button disabled"; exit 1 }
foreach ($ctrl in @("版本隔离模式", "最大内存", "最小内存", "Java 运行时", "窗口宽度", "自动连服地址", "游戏语言", "额外 JVM 参数")) {
    if (-not (Find-ByName $win $ctrl $null)) {
        Write-Output ("FAIL: control '{0}' not found" -f $ctrl); exit 1
    }
}
Write-Output "DETAIL ASSERT OK (4 cards + back + settings controls)"

# ── 修改「最大内存」 ──
$maxBox = Find-ByName $win "最大内存" $null
$before = Get-Num $maxBox
Write-Output ("MAX BEFORE: {0}" -f $before)
$target = 3072
if ($before -eq "3072") { $target = 3584 }

$set = Set-Num $maxBox $target
if (-not $set) {
    $edit = $maxBox.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    if ($edit) { $set = Set-Num $edit $target }
}
if (-not $set) { Write-Output "FAIL: cannot set 最大内存"; exit 1 }
Start-Sleep -Seconds 2
Write-Output ("MAX SET: {0}" -f $target)

# ── 返回列表（触发 flush）→ 重进详情 → 值保留 ──
($back.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Start-Sleep -Seconds 2
$item2 = $null
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while ([DateTime]::UtcNow -lt $deadline -and -not $item2) {
    $item2 = Get-FirstRow $win
    if (-not $item2) { Start-Sleep -Milliseconds 600 }
}
if (-not $item2) { Write-Output "FAIL: version row not found after back"; exit 1 }
if (-not (Open-Detail $win $item2)) { Write-Output "FAIL: detail page not reopened"; exit 1 }
$maxBox2 = Wait-ByName $win "最大内存" $null 10
$after = Get-Num $maxBox2
if ($after -ne [string]$target) {
    Write-Output ("FAIL: max memory not persisted (expected {0}, got {1})" -f $target, $after); exit 1
}
Write-Output ("REOPEN VALUE OK: {0}" -f $after)

# ── 落盘断言：version-settings.json 含 memory_max_mb ──
$settings = Get-Content "$env:APPDATA\starcloud-launcher\launcher.settings.json" -Raw | ConvertFrom-Json
$vf = Join-Path $settings.dot_minecraft "starcloud\version-settings.json"
if (-not (Test-Path $vf)) { Write-Output "FAIL: version-settings.json not created"; exit 1 }
$vj = Get-Content $vf -Raw | ConvertFrom-Json
$found = $false
foreach ($p in $vj.profiles.PSObject.Properties) {
    if ($p.Value.memory_max_mb -eq $target) {
        $found = $true
        Write-Output ("JSON OK: {0} memory_max_mb={1}" -f $p.Name, $p.Value.memory_max_mb)
    }
}
if (-not $found) { Write-Output ("FAIL: memory_max_mb={0} not found in version-settings.json" -f $target); exit 1 }

Write-Output "PASS"
exit 0
