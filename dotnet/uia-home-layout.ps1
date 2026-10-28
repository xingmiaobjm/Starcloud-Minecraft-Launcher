# 主页可编辑冒烟（完整）：
#   启动应用 → 点「编辑布局」→ 工具箱出现（悬浮，不改主界面布局）
#   → 记录编辑前后的卡片几何（确认主页布局没被动过）
#   → 从工具箱把「收藏的版本」拖到主页 → home-layout.json 里 favorites.visible=true
#   → 点「完成」→ 工具箱收起
param([switch]$Shot, [string]$ShotPath = "")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Mouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  public static void Drag(int x1, int y1, int x2, int y2) {
    SetCursorPos(x1, y1);
    System.Threading.Thread.Sleep(150);
    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(150);
    for (int i = 1; i <= 16; i++) {
      SetCursorPos(x1 + (x2 - x1) * i / 16, y1 + (y2 - y1) * i / 16);
      System.Threading.Thread.Sleep(50);
    }
    System.Threading.Thread.Sleep(300);
    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
  }
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(120);
    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(80);
    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
  }
  public static void SetPos(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(120); }
  public static void Scroll(int delta) { mouse_event(0x0800, 0, 0, (uint)delta, UIntPtr.Zero); }
}
"@

Get-Process -Name "Starcloud.App" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$exe = Join-Path $PSScriptRoot "src\Starcloud.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Starcloud.App.exe"
if (-not (Test-Path $exe)) { Write-Output "FAIL: exe not found"; exit 1 }

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
function Rect-Of($el) {
    $b = $el.Current.BoundingRectangle
    if ([double]::IsInfinity($b.X) -or [double]::IsInfinity($b.Width)) { return "inf" }
    return ("{0},{1} {2}x{3}" -f [int]$b.X, [int]$b.Y, [int]$b.Width, [int]$b.Height)
}

$win = $null
$deadline = [DateTime]::UtcNow.AddSeconds(40)
while ([DateTime]::UtcNow -lt $deadline -and -not $win) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { Start-Sleep -Milliseconds 500 }
}
if (-not $win) { Write-Output "FAIL: window not found"; exit 1 }
Write-Output ("WIN: {0}" -f $win.Current.Name)
Start-Sleep -Seconds 3

# 编辑前：网格几何
$grid = Find-AutoId $win "HomeGrid"
if (-not $grid) { Write-Output "FAIL: HomeGrid not found"; exit 1 }
$gridBefore = Rect-Of $grid
Write-Output ("GRID BEFORE EDIT: {0}" -f $gridBefore)

# 进编辑模式
$editBtn = Find-ByName $win "编辑布局" ([System.Windows.Automation.ControlType]::Button)
if (-not $editBtn) { Write-Output "FAIL: 编辑布局 button not found"; exit 1 }
($editBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Write-Output "EDIT CLICKED"
Start-Sleep -Seconds 2

# 编辑后：网格几何必须与之前一致（工具箱悬浮，不动主界面布局）
$gridAfter = Rect-Of (Find-AutoId $win "HomeGrid")
Write-Output ("GRID AFTER  EDIT: {0}" -f $gridAfter)
if ($gridBefore -ne $gridAfter) {
    Write-Output "FAIL: grid geometry changed in edit mode (toolbox must float, not resize layout)"
    exit 1
}
Write-Output "FLOATING OK: grid geometry unchanged in edit mode"

# 工具箱位置
$bar = Find-AutoId $win "LayoutEditBar"
if ($bar) { Write-Output ("TOOLBOX: {0}" -f (Rect-Of $bar)) }
else { Write-Output "TOOLBOX: (not exposed by automation id)" }

if ($Shot -and $ShotPath) {
    pwsh -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "shot-window.ps1") -Out $ShotPath | Out-Null
    Write-Output ("SHOT: {0}" -f $ShotPath)
}

# 从工具箱拖「收藏的版本」到主页
# 工具箱条目在 ScrollViewer 里：先滚到底再找（缩略图让每条更高，列表装不下 7 条）
$bar = Find-AutoId $win "LayoutEditBar"
if ($bar) {
    $br = $bar.Current.BoundingRectangle
    $cx = [int]($br.X + $br.Width / 2)
    $cy = [int]($br.Y + $br.Height * 0.35)
    [Mouse]::SetPos($cx, $cy)
    # 正 delta = 向下滚（WinUI 惯例）
    for ($i = 0; $i -lt 12; $i++) { [Mouse]::Scroll(120); Start-Sleep -Milliseconds 120 }
    Start-Sleep -Milliseconds 800
}

# 工具箱条目在 ScrollViewer 里，UIA 树中可能滞后：多等几轮并按名字精确匹配 Button
$fav = $null
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while ([DateTime]::UtcNow -lt $deadline -and -not $fav) {
    foreach ($cand in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)))) {
        if ($cand.Current.Name -eq "收藏的版本") {
            $r = $cand.Current.BoundingRectangle
            if (-not [double]::IsInfinity($r.X) -and $r.Width -gt 0) { $fav = $cand; break }
        }
    }
    if (-not $fav) { Start-Sleep -Milliseconds 700 }
}
if (-not $fav) { Write-Output "FAIL: palette item 收藏的版本 not found (or not laid out)"; exit 1 }
$fb = $fav.Current.BoundingRectangle
Write-Output ("PALETTE ITEM: {0}" -f (Rect-Of $fav))

$gb = (Find-AutoId $win "HomeGrid").Current.BoundingRectangle
$tx = [int]($gb.X + $gb.Width * 0.25)
$ty = [int]($gb.Y + $gb.Height * 0.72)
Write-Output ("DRAG TARGET: {0},{1}" -f $tx, $ty)
[Mouse]::Drag([int]($fb.X + $fb.Width / 2), [int]($fb.Y + $fb.Height / 2), $tx, $ty)
Write-Output "DRAG DONE"
Start-Sleep -Seconds 2

# 校验落盘
if (-not (Test-Path $layout)) { Write-Output "FAIL: home-layout.json not created"; exit 1 }
$cfg = Get-Content $layout -Raw | ConvertFrom-Json
$favCard = $cfg.cards | Where-Object { $_.id -eq "favorites" }
if (-not $favCard) { Write-Output "FAIL: favorites card not in layout"; exit 1 }
Write-Output ("FAVORITES: visible={0} col={1} row={2} span={3}x{4}" -f `
    $favCard.visible, $favCard.column, $favCard.row, $favCard.columnSpan, $favCard.rowSpan)
if (-not $favCard.visible) { Write-Output "FAIL: favorites not visible after drag"; exit 1 }
Write-Output "DROP COMMITTED OK"

# 落盘后不能有重叠
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

# 点「完成」→ 工具箱收起
$done = Find-ByName $win "完成" ([System.Windows.Automation.ControlType]::Button)
if (-not $done) { Write-Output "FAIL: 完成 button not found"; exit 1 }
($done.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Write-Output "DONE CLICKED"
Start-Sleep -Seconds 1

if (Test-Path $backup) { Move-Item $backup $layout -Force }

Write-Output "PASS"
exit 0
