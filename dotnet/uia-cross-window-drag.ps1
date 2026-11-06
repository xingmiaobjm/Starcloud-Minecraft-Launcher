# 跨窗口拖拽冒烟：从独立工具箱窗口把卡片拖到主页窗口落位
#   启动应用 → 点「编辑布局」→ 工具箱窗口出现（独立顶层窗口）
#   → 从工具箱拖「收藏的版本」到主页窗口 → home-layout.json 里 favorites.visible=true
#   → 所有可见卡片互不重叠
param([int]$TimeoutSec = 90)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class DragMouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  public static void Down(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(200);
    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(250);
  }
  public static void MoveTo(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(120);
  }
  public static void Up() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(250); }
}
"@

Get-Process -Name "Starcloud.App" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$exe = Join-Path $PSScriptRoot "src\Starcloud.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Starcloud.App.exe"
$dataDir = Join-Path $env:APPDATA "starcloud-launcher"
$layout = Join-Path $dataDir "home-layout.json"
$backup = "$layout.bak"
if (Test-Path $layout) { Copy-Item $layout $backup -Force; Remove-Item $layout -Force }

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

# 主窗口
$main = $null
$deadline = [DateTime]::UtcNow.AddSeconds(40)
while ([DateTime]::UtcNow -lt $deadline -and -not $main) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $cands = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($c in $cands) {
        if ($c.Current.Name -like "*Starcloud Minecraft Launcher*") { $main = $c; break }
    }
    if (-not $main) { Start-Sleep -Milliseconds 500 }
}
if (-not $main) { Write-Output "FAIL: main window not found"; exit 1 }
Write-Output ("MAIN: {0}" -f $main.Current.Name)
Start-Sleep -Seconds 3

# 进编辑模式 → 工具箱窗口
$editBtn = Find-ByName $main "编辑布局" ([System.Windows.Automation.ControlType]::Button)
if (-not $editBtn) { Write-Output "FAIL: 编辑布局 button not found"; exit 1 }
($editBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Start-Sleep -Seconds 3

# 工具箱窗口：按标题找
$toolbox = $null
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
foreach ($c in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
    if ($c.Current.Name -like "*布局编辑工具箱*") { $toolbox = $c; break }
}
if (-not $toolbox) { Write-Output "FAIL: toolbox window not found"; exit 1 }
Write-Output ("TOOLBOX: {0}" -f $toolbox.Current.Name)

# 工具箱里的「收藏的版本」条目
$fav = $null
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while ([DateTime]::UtcNow -lt $deadline -and -not $fav) {
    foreach ($c in $toolbox.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)))) {
        if ($c.Current.Name -eq "收藏的版本") {
            $r = $c.Current.BoundingRectangle
            if (-not [double]::IsInfinity($r.X) -and $r.Width -gt 0) { $fav = $c; break }
        }
    }
    if (-not $fav) { Start-Sleep -Milliseconds 600 }
}
if (-not $fav) { Write-Output "FAIL: palette item 收藏的版本 not found in toolbox"; exit 1 }
$fb = $fav.Current.BoundingRectangle
Write-Output ("PALETTE ITEM: {0},{1}" -f [int]$fb.X, [int]$fb.Y)

# 主页网格
$grid = Find-AutoId $main "HomeGrid"
if (-not $grid) { Write-Output "FAIL: HomeGrid not found"; exit 1 }
$gb = $grid.Current.BoundingRectangle
Write-Output ("GRID: {0},{1} {2}x{3}" -f [int]$gb.X, [int]$gb.Y, [int]$gb.Width, [int]$gb.Height)

# 从工具箱拖到主页网格中部偏下
$sx = [int]($fb.X + $fb.Width / 2)
$sy = [int]($fb.Y + $fb.Height / 2)
$tx = [int]($gb.X + $gb.Width * 0.3)
$ty = [int]($gb.Y + $gb.Height * 0.7)
Write-Output ("DRAG: ({0},{1}) -> ({2},{3})" -f $sx, $sy, $tx, $ty)

[DragMouse]::Down($sx, $sy)
for ($i = 1; $i -le 16; $i++) {
    [DragMouse]::MoveTo([int]($sx + ($tx - $sx) * $i / 16), [int]($sy + ($ty - $sy) * $i / 16))
}
Start-Sleep -Milliseconds 400
[DragMouse]::Up()
Write-Output "DRAG DONE"
Start-Sleep -Seconds 2

if (-not (Test-Path $layout)) { Write-Output "FAIL: home-layout.json not created"; exit 1 }
$cfg = Get-Content $layout -Raw | ConvertFrom-Json
$favCard = $cfg.cards | Where-Object { $_.id -eq "favorites" }
if (-not $favCard) { Write-Output "FAIL: favorites card not in layout"; exit 1 }
Write-Output ("FAVORITES: visible={0} col={1} row={2} span={3}x{4}" -f `
    $favCard.visible, $favCard.column, $favCard.row, $favCard.columnSpan, $favCard.rowSpan)
if (-not $favCard.visible) { Write-Output "FAIL: favorites not visible after cross-window drag"; exit 1 }
Write-Output "CROSS-WINDOW DROP COMMITTED OK"

$visible = @($cfg.cards | Where-Object { $_.visible })
for ($i = 0; $i -lt $visible.Count; $i++) {
    for ($j = $i + 1; $j -lt $visible.Count; $j++) {
        $a = $visible[$i]; $b = $visible[$j]
        $overlap = ($a.column -lt ($b.column + $b.columnSpan)) -and ($b.column -lt ($a.column + $a.columnSpan)) -and `
                   ($a.row -lt ($b.row + $b.rowSpan)) -and ($b.row -lt ($a.row + $a.rowSpan))
        if ($overlap) { Write-Output ("FAIL: cards overlap: {0} x {1}" -f $a.id, $b.id); exit 1 }
    }
}
Write-Output "NO OVERLAP OK"

if (Test-Path $backup) { Move-Item $backup $layout -Force }
Write-Output "PASS"
exit 0
