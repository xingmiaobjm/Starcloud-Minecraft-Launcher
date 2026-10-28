# 主页布局工具箱独立窗口冒烟：
#   启动应用 → 点「编辑布局」→ 独立工具箱窗口出现（顶层窗口枚举能找到）
#   → 工具箱窗口在主页窗口之外（不挡主页）
#   → 关掉工具箱 → 主页退出编辑态
param([string]$ShotPath = "")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class TopWin {
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static System.Collections.Generic.List<IntPtr> All(uint wantPid) {
    var list = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h, p) => {
      if (!IsWindowVisible(h)) return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == wantPid) list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list;
  }
  public static string TitleOf(IntPtr h) {
    var sb = new StringBuilder(512);
    GetWindowTextW(h, sb, 512);
    return sb.ToString();
  }
  public static RECT RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
}
"@

Get-Process -Name "Starcloud.App" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$exe = Join-Path $PSScriptRoot "src\Starcloud.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\Starcloud.App.exe"
if (-not (Test-Path $exe)) { Write-Output "FAIL: exe not found"; exit 1 }

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

Write-Output "--- TOP LEVEL WINDOWS (before) ---"
foreach ($h in [TopWin]::All([uint32]$proc.Id)) {
    $r = [TopWin]::RectOf($h)
    Write-Output ("  '{0}' @ {1},{2} {3}x{4}" -f [TopWin]::TitleOf($h), $r.Left, $r.Top, ($r.Right-$r.Left), ($r.Bottom-$r.Top))
}

# 点「编辑布局」
$editBtn = Find-ByName $win "编辑布局" ([System.Windows.Automation.ControlType]::Button)
if (-not $editBtn) { Write-Output "FAIL: 编辑布局 button not found"; exit 1 }
($editBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Write-Output "EDIT CLICKED"
Start-Sleep -Seconds 3

Write-Output "--- TOP LEVEL WINDOWS (after) ---"
$toolbox = [IntPtr]::Zero
foreach ($h in [TopWin]::All([uint32]$proc.Id)) {
    $r = [TopWin]::RectOf($h)
    Write-Output ("  '{0}' @ {1},{2} {3}x{4}" -f [TopWin]::TitleOf($h), $r.Left, $r.Top, ($r.Right-$r.Left), ($r.Bottom-$r.Top))
    $t = [TopWin]::TitleOf($h)
    if ($t -like "*布局编辑工具箱*") { $toolbox = $h }
}

if ($toolbox -eq [IntPtr]::Zero) {
    Write-Output "FAIL: toolbox window not found among top-level windows"
    exit 1
}
Write-Output "TOOLBOX WINDOW FOUND"

if ($ShotPath) {
    pwsh -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "shot-window.ps1") -Out $ShotPath -Name "布局编辑工具箱" | Out-Null
    Write-Output ("SHOT: {0}" -f $ShotPath)
}

Write-Output "PASS"
exit 0
