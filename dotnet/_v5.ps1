param([string]$Click = "")
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p = Get-Process Starcloud.App | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pc)
$nc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "26.3-3")
foreach ($w in $wins) {
    $all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nc)
    for ($i = 0; $i -lt $all.Count; $i++) {
        $r = $all.Item($i).Current.BoundingRectangle
        Write-Output ("[" + $i + "] X=" + [int]$r.X + " Y=" + [int]$r.Y + " W=" + [int]$r.Width)
    }
}
