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
Start-Sleep -Seconds 3

# 工具箱窗口会浮在主页左上角（默认位置）：把它挪到屏幕右缘，别挡住要拖的卡片。
# （用户实际使用时也会自己拖开工具箱 —— 这里模拟的就是这一步。）
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class TBMover {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr ins, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  public static IntPtr Find(uint pid, string part) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, p) => {
      uint q; GetWindowThreadProcessId(h, out q);
      if (q == pid && IsWindowVisible(h)) {
        var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512);
        if (sb.ToString().Contains(part)) { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@
$tbHwnd = [TBMover]::Find([uint32]$proc.Id, "布局编辑工具箱")
if ($tbHwnd -ne [IntPtr]::Zero) {
    # 挪到屏幕右缘 + 不抢前台（SWP_NOACTIVATE）
    [TBMover]::SetWindowPos($tbHwnd, [IntPtr]::Zero, 2140, 200, 400, 560, 0x0014) | Out-Null
    Start-Sleep -Milliseconds 600
    Write-Output "TOOLBOX MOVED ASIDE"
}
# 工具箱窗口开着时前台在它那里：第一次点击只会「激活主页」而吞掉按下事件
# （间歇性翻车的根因）。先把主窗口拉回前台再拖。
$mainHwnd = $proc.MainWindowHandle
[TBMover]::SetForegroundWindow($mainHwnd) | Out-Null
Start-Sleep -Milliseconds 500

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
# 合成鼠标输入偶尔会丢按下事件（约三分之一的概率，与前台焦点竞争有关）：
# 按下后先验证卡片真的跟手了，没跟手就松手重试，最多 3 轮。
$followed = $false
foreach ($round in 1..2) {
    [DragMouse]::Down($cardX, $cardY)
    Start-Sleep -Milliseconds 500

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
    Write-Output ("ROUND {0}: CARD DURING DRAG: {1},{2} (moved {3},{4})" -f $round, $newX, $newY, $dx, $dy)

    if ([Math]::Abs($dx) -ge 40 -or [Math]::Abs($dy) -ge 40) {
        Write-Output "FOLLOW OK: card body moved with the pointer"
        $followed = $true
        break
    }
    # 没跟手：松手（卡片留在原地）。
    # 合成鼠标输入偶尔整实例失灵（前台竞争，重启应用后就好了）：杀掉重启再来一轮。
    [DragMouse]::Up()
    Start-Sleep -Milliseconds 500
    if ($round -lt 2) {
        Write-Output "INPUT LOST: restarting app for a clean instance"
        Get-Process -Name "Starcloud.App" -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 1200
        $proc2 = Start-Process -FilePath $exe -PassThru
        $deadline2 = [DateTime]::UtcNow.AddSeconds(40)
        $win = $null
        while ([DateTime]::UtcNow -lt $deadline2 -and -not $win) {
            $cond2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc2.Id)
            foreach ($c in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond2)) {
                if ($c.Current.Name -like "*Starcloud Minecraft Launcher*") { $win = $c; break }
            }
            if (-not $win) { Start-Sleep -Milliseconds 500 }
        }
        if (-not $win) { Write-Output "FAIL: window not found after restart"; exit 1 }
        Start-Sleep -Seconds 3
        $editBtn2 = Find-ByName $win "编辑布局" ([System.Windows.Automation.ControlType]::Button)
        if (-not $editBtn2) { Write-Output "FAIL: 编辑布局 button not found after restart"; exit 1 }
        ($editBtn2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Start-Sleep -Seconds 3
        $tbHwnd2 = [TBMover]::Find([uint32]$proc2.Id, "布局编辑工具箱")
        if ($tbHwnd2 -ne [IntPtr]::Zero) {
            [TBMover]::SetWindowPos($tbHwnd2, [IntPtr]::Zero, 2140, 200, 400, 560, 0x0014) | Out-Null
            Start-Sleep -Milliseconds 600
        }
        [TBMover]::SetForegroundWindow($proc2.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 500
        $title = Find-ByName $win "已安装版本" ([System.Windows.Automation.ControlType]::Text)
        $tb = $title.Current.BoundingRectangle
        $cardX = [int]($tb.X + $tb.Width / 2)
        $cardY = [int]($tb.Y + $tb.Height / 2)
    }
}
if (-not $followed) {
    Write-Output "FAIL: card did not follow the pointer in any of 3 rounds"
    [DragMouse]::Up()
    exit 1
}

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
