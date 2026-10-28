# 截取 Starcloud 的顶层窗口
#   -Out   输出文件
#   -Name  窗口标题里要匹配的片段（不给就抓主窗口）
#
# 用 PrintWindow 按窗口句柄抓图，而不是屏幕拷贝：
# 屏幕拷贝只能抓到「当前最上面」的那个窗口 —— 小工具箱被别的东西挡住时
# 截到的就是底下那个（实测踩过），看起来像工具箱内容错乱。
param([string]$Out = "", [string]$Name = "")

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win32Shot {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static System.Collections.Generic.List<IntPtr> TopWindows(uint wantPid) {
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
}
"@

$p = Get-Process -Name "Starcloud.App" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output "FAIL: app not running"; exit 1 }

$target = [IntPtr]::Zero
if ($Name) {
    foreach ($h in [Win32Shot]::TopWindows([uint32]$p.Id)) {
        if ([Win32Shot]::TitleOf($h) -like "*$Name*") { $target = $h; break }
    }
    if ($target -eq [IntPtr]::Zero) {
        Write-Output "FAIL: no window matching '$Name'; available:"
        foreach ($h in [Win32Shot]::TopWindows([uint32]$p.Id)) { Write-Output ("  - '{0}'" -f [Win32Shot]::TitleOf($h)) }
        exit 1
    }
} else {
    $target = $p.MainWindowHandle
    if ($target -eq [IntPtr]::Zero) { Write-Output "FAIL: no main window"; exit 1 }
}

$r = New-Object Win32Shot+RECT
[Win32Shot]::GetWindowRect($target, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
if ($w -le 0 -or $h -le 0) { Write-Output "FAIL: bad window rect"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT(2)：能抓到被遮挡/最小化窗口的内容
[Win32Shot]::PrintWindow($target, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc)

if (-not $Out) { $Out = Join-Path $PSScriptRoot "..\.zcode\shot.png" }
$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("SAVED: {0} ({1}x{2})" -f $Out, $w, $h)
exit 0
