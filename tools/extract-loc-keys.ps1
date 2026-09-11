param(
  [string]$Root = "dotnet\src\Starcloud.App",
  [string]$Out  = "loc-keys.txt"
)

$keys = New-Object 'System.Collections.Generic.HashSet[string]'

$src = Get-ChildItem -Path $Root -Recurse -Include *.cs,*.xaml |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

# CS: Loc.T("...")  and  Loc.T(@"...")
$csRe = [regex]'Loc\.T\(\s*@?"((?:[^"\\]|\\.)*)"'
$src | Where-Object { $_.Extension -eq '.cs' } | ForEach-Object {
  $t = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
  foreach ($m in $csRe.Matches($t)) {
    $v = $m.Groups[1].Value
    $v = $v -replace '\\"', '"' -replace '\\\\', '\'
    [void]$keys.Add($v)
  }
}

# XAML: loc:Loc.Xxx="..."
$xRe = [regex]'loc:Loc\.[A-Za-z]+="([^"]*)"'
$src | Where-Object { $_.Extension -eq '.xaml' } | ForEach-Object {
  $t = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
  foreach ($m in $xRe.Matches($t)) {
    $v = $m.Groups[1].Value
    $v = $v -replace '&quot;', '"' -replace '&amp;', '&' -replace '&lt;', '<' -replace '&gt;', '>'
    [void]$keys.Add($v)
  }
}

$sorted = $keys | Sort-Object
$sorted | Set-Content -LiteralPath $Out -Encoding UTF8
Write-Host ("TOTAL=" + $sorted.Count)
