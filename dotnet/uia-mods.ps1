Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, 17692)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Output "FAIL: window not found"; exit 1 }

# find nav item by name
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$modsItem = $null
foreach ($a in $all) {
    if ($a.Current.Name -eq [char]0x793E + [string][char]0x533A + [char]0x8D44 + [char]0x6E90 -and $a.Current.ControlType.ProgrammaticName -ne "ControlType.Text") { $modsItem = $a; break }
}
if (-not $modsItem) { Write-Output "FAIL: nav item not found"; exit 1 }
$sel = $modsItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$sel.Select()
Start-Sleep -Seconds 8

# page texts
$texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
$names = @()
foreach ($t in $texts) { if ($t.Current.Name) { $names += $t.Current.Name } }
Write-Output ("TEXT COUNT: {0}" -f $names.Count)
$names | Select-Object -First 40 | ForEach-Object { Write-Output ("  - " + $_) }

# edits / combos / buttons
$edits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
Write-Output ("EDITS: {0}" -f $edits.Count)
foreach ($e in $edits) { Write-Output ("  edit: '{0}'" -f $e.Current.Name) }
$combos = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
Write-Output ("COMBOS: {0}" -f $combos.Count)
foreach ($c in $combos) { Write-Output ("  combo: name='{0}' expand='{1}'" -f $c.Current.Name, $c.Current.DefaultAction) }
$btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
Write-Output ("BUTTONS: {0}" -f $btns.Count)
$btnNames = @()
foreach ($b in $btns) { $btnNames += $b.Current.Name }
Write-Output ("  btn names: {0}" -f (($btnNames | Where-Object { $_ }) -join ", "))

# lists (left categories + result rows)
$lists = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)))
foreach ($l in $lists) {
    $items = $l.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $in = @()
    foreach ($i in $items) { $in += $i.Current.Name }
    $joined = ($in | Where-Object { $_ }) -join " | "
    if ($joined.Length -gt 400) { $joined = $joined.Substring(0, 400) }
    Write-Output ("LIST items={0}: {1}" -f $items.Count, $joined)
}

# info bar state (search error?)
$bars = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Group)))
foreach ($g in $bars) { if ($g.Current.Name -match "FAIL|ERROR") { Write-Output ("INFOBAR: {0}" -f $g.Current.Name) } }
