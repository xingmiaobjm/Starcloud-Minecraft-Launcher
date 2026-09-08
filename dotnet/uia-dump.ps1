param([int]$ProcessId = 0, [string]$ClickName = "", [switch]$Login, [string]$LoginId = "xingmiao", [string]$LoginPwd = "1234", [string]$Ask = "", [string]$SendName = "Send", [switch]$Probe, [string]$Fill = "", [string]$FinalClick = "")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if ($ProcessId -le 0) {
    $p = Get-Process -Name "Starcloud.App" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { Write-Output "FAIL: Starcloud.App not running"; exit 1 }
    $ProcessId = $p.Id
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Output "FAIL: window not found"; exit 1 }
Write-Output ("WIN: {0}" -f $win.Current.Name)

if ($ClickName) {
    foreach ($nm in ($ClickName -split ',')) {
        $nameCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $nm)
        $target = $null
        for ($i = 0; $i -lt 10 -and -not $target; $i++) {
            $target = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
            if (-not $target) { Start-Sleep -Milliseconds 300 }
        }
        if (-not $target) { Write-Output "FAIL: element '$nm' not found"; exit 1 }
        try { ($target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() } catch {
            try { ($target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch { Write-Output "FAIL: cannot invoke '$nm'"; exit 1 }
        }
        Start-Sleep -Milliseconds 1200
        Write-Output ("CLICKED: {0}" -f $nm)
    }
}

if ($Fill) {
    foreach ($pair in ($Fill -split ';')) {
        $parts = $pair -split '\|', 2
        $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $parts[0])
        $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc)
        if (-not $el) { Write-Output "FAIL: fill target '$($parts[0])' not found"; exit 1 }
        ($el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($parts[1])
        Write-Output ("FILLED: {0}" -f $parts[0])
    }
    Start-Sleep -Milliseconds 400
}

if ($FinalClick) {
    $ncF = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $FinalClick)
    $tF = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $ncF)
    if (-not $tF) { Write-Output "FAIL: final click '$FinalClick' not found"; exit 1 }
    try { ($tF.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch { Write-Output "FAIL: cannot invoke '$FinalClick'"; exit 1 }
    Start-Sleep -Milliseconds 1200
    Write-Output ("FINAL-CLICKED: {0}" -f $FinalClick)
}

if ($Login) {
    function Find-UIA([string]$name, $ctype) {
        $nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
        if ($ctype) {
            $tc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctype)
            $and = New-Object System.Windows.Automation.AndCondition($nc, $tc)
            return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
        }
        return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc)
    }
    $idEl = Find-UIA 'Starcloud ID' ([System.Windows.Automation.ControlType]::Edit)
    $pwEl = Find-UIA '密码' ([System.Windows.Automation.ControlType]::Edit)
    if (-not $idEl -or -not $pwEl) { Write-Output "FAIL: login inputs not found"; exit 1 }
    ($idEl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($LoginId)
    ($pwEl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($LoginPwd)
    $btn = Find-UIA '登录' ([System.Windows.Automation.ControlType]::Button)
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Seconds 3
    Write-Output "LOGIN-CLICKED"  
}

if ($Probe) {
    $edits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    Write-Output ("EDITS: {0}" -f $edits.Count)
    foreach ($e in $edits) {
        Write-Output ("  Edit Name='{0}' AutoId='{1}' Class='{2}'" -f $e.Current.Name, $e.Current.AutomationId, $e.Current.ClassName)
    }
    $btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    Write-Output ("BUTTONS: {0}" -f $btns.Count)
    foreach ($b in $btns) {
        Write-Output ("  Button Name='{0}' AutoId='{1}'" -f $b.Current.Name, $b.Current.AutomationId)
    }
    $ncT = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $LoginId)
    $byName = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ncT)
    Write-Output ("BY-NAME '{0}': {1}" -f $LoginId, $byName.Count)
    foreach ($b in $byName) { Write-Output ("  {0} AutoId='{1}'" -f $b.Current.ControlType.ProgrammaticName, $b.Current.AutomationId) }
    $tcE = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $andE = New-Object System.Windows.Automation.AndCondition($ncT, $tcE)
    $byAnd = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $andE)
    Write-Output ("BY-AND: {0}" -f $byAnd.Count)
    exit 0
}

if ($Ask) {
    $edit = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    if (-not $edit) { Write-Output "FAIL: chat input not found"; exit 1 }
    ($edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Ask)
    $nameCond2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $SendName)
    $typeCond2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $sendCond = New-Object System.Windows.Automation.AndCondition($nameCond2, $typeCond2)
    $send = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $sendCond)
    if (-not $send) { Write-Output "FAIL: send button not found"; exit 1 }
    ($send.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Seconds 4
    Write-Output ("ASKED: {0}" -f $Ask)
}

$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
Write-Output ("ALL ELEMENTS: {0}" -f $all.Count)
foreach ($a in $all) {
    $n = $a.Current.Name
    if ($n) { Write-Output ("  [{0}] {1}" -f $a.Current.ControlType.ProgrammaticName, $n) }
}
