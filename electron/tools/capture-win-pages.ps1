# 逐页截图 Win 端启动器（UIAutomation 点导航 + PrintWindow 抓窗口）
# 用法：powershell -File tools/capture-win-pages.ps1 -Pid 14900 -OutDir C:\path
param(
  [Parameter(Mandatory = $true)][int]$TargetPid,
  [Parameter(Mandatory = $true)][string]$OutDir
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Cap {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$pages = @(
  @{ Key = 'home';        Label = '首页' },
  @{ Key = 'versions';    Label = '版本' },
  @{ Key = 'download';    Label = '下载' },
  @{ Key = 'saves';       Label = '存档' },
  @{ Key = 'multiplayer'; Label = '联机' },
  @{ Key = 'lab';         Label = '实验室' },
  @{ Key = 'projections'; Label = '投影管理' },
  @{ Key = 'running';     Label = '运行状态' },
  @{ Key = 'accounts';    Label = '账户' },
  @{ Key = 'plugins';     Label = '插件' },
  @{ Key = 'nova';        Label = 'Nova' },
  @{ Key = 'logs';        Label = '启动器日志' },
  @{ Key = 'settings';    Label = '设置' }
)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$proc = Get-Process -Id $TargetPid
$h = $proc.MainWindowHandle
$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
if ($null -eq $root) { Write-Error 'UIA root not found'; exit 1 }

function Capture-Window([string]$path) {
  $r = New-Object Cap+RECT
  [Cap]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.Right - $r.Left
  $ht = $r.Bottom - $r.Top
  if ($w -le 0 -or $ht -le 0) { Write-Warning "bad rect $w x $ht"; return }
  $bmp = New-Object System.Drawing.Bitmap $w, $ht
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [Cap]::PrintWindow($h, $hdc, 2) | Out-Null
  $g.ReleaseHdc($hdc)
  $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}

$cond = [System.Windows.Automation.Condition]::TrueCondition
foreach ($p in $pages) {
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  $target = $null
  foreach ($c in $all) {
    if ($c.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and
        $c.Current.Name -eq $p.Label) {
      $target = $c
      break
    }
  }
  if ($null -eq $target) {
    Write-Warning "nav item not found: $($p.Label)"
    continue
  }
  try {
    $sel = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sel.Select()
  } catch {
    try {
      $inv = $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
      $inv.Invoke()
    } catch {
      Write-Warning "cannot activate $($p.Label): $_"
      continue
    }
  }
  Start-Sleep -Milliseconds 1600
  $file = Join-Path $OutDir ("win-" + $p.Key + ".png")
  Capture-Window $file
  Write-Host "captured $($p.Key) -> $file"
}
