# 工具箱窗口生命周期冒烟：
#   启动应用 → 点「编辑布局」→ 工具箱窗口出现 → 主页进入编辑态（卡片有缩放把手）
#   → 正常关掉工具箱窗口 → 主页退出编辑态 → 位置被记住
param([int]$TimeoutSec = 60)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class TW {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static System.Collections.Generic.List<IntPtr> All(uint pid) {
    var l = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h,p) => { uint q; GetWindowThreadProcessId(h, out q); if (q==pid && IsWindowVisible(h)) l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
  public static string T(IntPtr h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512); return sb.ToString(); }
  // WM_CLOSE：等价于用户点关闭按钮
  public static void Close(IntPtr h) { SendMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); }
}
"@

Get-Process -Name "Starcloud.App" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$exe = Join-Path $PSScriptRoot "src\Starcloud.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Starcloud.App.exe"
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

$main = $null
$deadline = [DateTime]::UtcNow.AddSeconds(40)
while ([DateTime]::UtcNow -lt $deadline -and -not $main) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    foreach ($c in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        if ($c.Current.Name -like "*Starcloud Minecraft Launcher*") { $main = $c; break }
    }
    if (-not $main) { Start-Sleep -Milliseconds 500 }
}
if (-not $main) { Write-Output "FAIL: main window not found"; exit 1 }
Start-Sleep -Seconds 3

$editBtn = Find-ByName $main "编辑布局" ([System.Windows.Automation.ControlType]::Button)
($editBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Start-Sleep -Seconds 3

$toolbox = [IntPtr]::Zero
foreach ($h in [TW]::All([uint32]$proc.Id)) { if ([TW]::T($h) -like "*布局编辑工具箱*") { $toolbox = $h } }
if ($toolbox -eq [IntPtr]::Zero) { Write-Output "FAIL: toolbox window not found"; exit 1 }
$r = New-Object TW+RECT
[TW]::GetWindowRect($toolbox, [ref]$r) | Out-Null
Write-Output ("TOOLBOX at {0},{1}" -f $r.Left, $r.Top)

# 编辑态：主页网格里应出现缩放把手（编辑层的按钮）
$grid = Find-AutoId $main "HomeGrid"
if (-not $grid) { Write-Output "FAIL: HomeGrid not found"; exit 1 }
$handles = 0
foreach ($b in $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))) {
    if ([string]::IsNullOrEmpty($b.Current.Name)) { $handles++ }
}
Write-Output ("EDIT MODE: {0} resize handles in grid" -f $handles)
if ($handles -lt 1) { Write-Output "FAIL: home grid not in edit mode"; exit 1 }

# 关掉工具箱
[TW]::Close($toolbox)
Write-Output "TOOLBOX CLOSED"
Start-Sleep -Seconds 2

# 窗口应已消失
$still = $false
foreach ($h in [TW]::All([uint32]$proc.Id)) { if ([TW]::T($h) -like "*布局编辑工具箱*") { $still = $true } }
if ($still) { Write-Output "FAIL: toolbox window still open"; exit 1 }
Write-Output "TOOLBOX CLOSED OK"

# 主页应退出编辑态（缩放把手消失）
Start-Sleep -Seconds 1
$handles2 = 0
foreach ($b in $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))) {
    if ([string]::IsNullOrEmpty($b.Current.Name)) { $handles2++ }
}
Write-Output ("AFTER CLOSE: {0} resize handles in grid" -f $handles2)
if ($handles2 -ge $handles) { Write-Output "FAIL: home grid still in edit mode"; exit 1 }
Write-Output "EDIT MODE EXITED OK"

# 位置应被记住
$settings = Get-Content "$env:APPDATA\starcloud-launcher\launcher.settings.json" -Raw | ConvertFrom-Json
Write-Output ("SAVED POSITION: {0},{1}" -f $settings.toolbox_window_x, $settings.toolbox_window_y)
if ($null -eq $settings.toolbox_window_x) { Write-Output "FAIL: toolbox position not saved"; exit 1 }
Write-Output "POSITION SAVED OK"

Write-Output "PASS"
exit 0
