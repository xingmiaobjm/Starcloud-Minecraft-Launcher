# 主页卡片拖拽冒烟：
#   进编辑模式 → 按住版本卡拖动 → 拖动途中卡片本体跟着指针走（合成变换位移），
#   而不是只挪一个落点框 → 松手后卡片落到新格位并落盘
#
# 判定「跟着走」的办法：拖到一半时，量卡片在屏幕上的位置 —— 若卡片跟着指针移动了，
# 说明变换生效；若卡片原地不动、只有落点框在动，就是没生效。
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
    SetCursorPos(x, y); System.Threading.Thread.Sleep(150);
    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(200);
  }
  public static void MoveTo(int x, int y) {
    // 只用 SetCursorPos：Windows 在光标位置变化时自行派发移动事件。
    // 不要再补 mouse_event(MOVE) —— 相对增量 0 会被当成绝对坐标 (0,0)，
    // 反而把光标搬到屏幕左上角去。
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(120);
  }
  public static void Up() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(200); }
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

$win = $null
$deadline = [DateTime]::UtcNow.AddSeconds(40)
while ([DateTime]::UtcNow -lt $deadline -and -not $win) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { Start-Sleep -Milliseconds 500 }
}
if (-not $win) { Write-Output "FAIL: window not found"; exit 1 }
Start-Sleep -Seconds 3

$editBtn = Find-ByName $win "编辑布局" ([System.Windows.Automation.ControlType]::Button)
if (-not $editBtn) { Write-Output "FAIL: 编辑布局 button not found"; exit 1 }
($editBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Write-Output "EDIT CLICKED"
Start-Sleep -Seconds 2

# 版本卡的卡头标题「已安装版本」用来定位卡片位置。
# 工具箱里也有同名文字（缩略图条目名），要挑主页那张 —— 左边那个。
$title = $null
foreach ($t in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)))) {
    if ($t.Current.Name -eq "已安装版本") {
        $r = $t.Current.BoundingRectangle
        if (-not [double]::IsInfinity($r.X) -and $r.X -lt 1000) { $title = $t; break }
    }
}
if (-not $title) { Write-Output "FAIL: version card title not found"; exit 1 }
$tb = $title.Current.BoundingRectangle
$cardX = [int]($tb.X + $tb.Width / 2)
$cardY = [int]($tb.Y + $tb.Height / 2)
Write-Output ("CARD TITLE: {0},{1} (rect {2},{3} {4}x{5})" -f $cardX, $cardY, [int]$tb.X, [int]$tb.Y, [int]$tb.Width, [int]$tb.Height)

# 拖动起点：在卡片标题上按下
[DragMouse]::Down($cardX, $cardY)
Start-Sleep -Milliseconds 300

# 拖到网格中部偏下
$targetX = $cardX + 260
$targetY = $cardY + 220
$steps = 8
for ($i = 1; $i -le $steps; $i++) {
    [DragMouse]::MoveTo([int]($cardX + ($targetX - $cardX) * $i / $steps),
                        [int]($cardY + ($targetY - $cardY) * $i / $steps))
}
Start-Sleep -Milliseconds 500

# 关键判定：拖动途中，卡片标题的位置应该已经跟着指针移动了
$title2 = Find-ByName $win "已安装版本" ([System.Windows.Automation.ControlType]::Text)
$tb2 = $title2.Current.BoundingRectangle
$newX = [int]($tb2.X + $tb2.Width / 2)
$newY = [int]($tb2.Y + $tb2.Height / 2)
$dx = $newX - $cardX
$dy = $newY - $cardY
Write-Output ("CARD DURING DRAG: {0},{1} (moved {2},{3})" -f $newX, $newY, $dx, $dy)

if ([Math]::Abs($dx) -lt 40 -and [Math]::Abs($dy) -lt 40) {
    Write-Output "FAIL: card did not follow the pointer (only the drop preview moved)"
    [DragMouse]::Up()
    exit 1
}
Write-Output "FOLLOW OK: card body moved with the pointer"

[DragMouse]::Up()
Start-Sleep -Seconds 2

# 松手后应落到新格位并落盘
if (-not (Test-Path $layout)) { Write-Output "FAIL: home-layout.json not created"; exit 1 }
$cfg = Get-Content $layout -Raw | ConvertFrom-Json
$versions = $cfg.cards | Where-Object { $_.id -eq "versions" }
Write-Output ("VERSIONS AFTER DROP: col={0} row={1} span={2}x{3}" -f `
    $versions.column, $versions.row, $versions.columnSpan, $versions.rowSpan)
Write-Output "DROP COMMITTED"

if (Test-Path $backup) { Move-Item $backup $layout -Force }
Write-Output "PASS"
exit 0
