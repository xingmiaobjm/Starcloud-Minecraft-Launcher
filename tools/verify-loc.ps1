$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$appRoot = Join-Path $repo 'dotnet\src\Starcloud.App'
$jsonPath = Join-Path $appRoot 'Strings\en-US.json'

function Norm([string]$s) {
  if ($null -eq $s) { return '' }
  $r = $s.Replace('\' + 'n', "`n")
  $r = $r -replace '&#x0[aA];', "`n"
  return $r
}

$expected = New-Object 'System.Collections.Generic.HashSet[string]'

$csRe = [regex]'Loc\.T\(\s*@?"((?:[^"\\]|\\.)*)"'
$xRe  = [regex]'loc:Loc\.[A-Za-z]+="([^"]*)"'

$files = Get-ChildItem -Path $appRoot -Recurse -Include *.cs,*.xaml |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

foreach ($f in $files) {
  $t = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
  if ($f.Extension -eq '.cs') {
    foreach ($m in $csRe.Matches($t)) {
      $v = $m.Groups[1].Value -replace '\\"', '"' -replace '\\\\', '\'
      [void]$expected.Add((Norm $v))
    }
  } else {
    foreach ($m in $xRe.Matches($t)) {
      $v = [System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value)
      [void]$expected.Add((Norm $v))
    }
  }
}

$json = [System.IO.File]::ReadAllText($jsonPath, [System.Text.Encoding]::UTF8)
$dict = $json | ConvertFrom-Json
$have = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($p in $dict.PSObject.Properties) { [void]$have.Add($p.Name) }

# Statically invisible keys: runtime enum values passed to Loc.T(variable),
# plus Core-produced text localized by Loc.TCore.
$extraPath = Join-Path $PSScriptRoot 'extra-keys.txt'
if (Test-Path $extraPath) {
  foreach ($l in [System.IO.File]::ReadAllLines($extraPath, [System.Text.Encoding]::UTF8)) {
    if ($l.Trim().Length -gt 0) { [void]$expected.Add((Norm $l)) }
  }
}
$corePath = Join-Path $PSScriptRoot 'core-strings.json'
if (Test-Path $corePath) {
  $core = ([System.IO.File]::ReadAllText($corePath, [System.Text.Encoding]::UTF8)) | ConvertFrom-Json
  foreach ($p in $core.PSObject.Properties) { [void]$expected.Add((Norm $p.Name)) }
}

$missing = @($expected | Where-Object { -not $have.Contains($_) } | Sort-Object)
$unused  = @($have | Where-Object { -not $expected.Contains($_) } | Sort-Object)

Write-Host ("EXPECTED=" + $expected.Count + " DICT=" + $have.Count)
Write-Host ("MISSING=" + $missing.Count)
foreach ($k in $missing) { Write-Host ("  MISS <" + $k + ">") }
Write-Host ("UNUSED=" + $unused.Count)
foreach ($k in $unused) { Write-Host ("  UNUSED <" + $k + ">") }
