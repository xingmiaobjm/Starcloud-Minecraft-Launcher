param(
  [Parameter(Mandatory=$true)][ValidateSet('split','merge')][string]$Mode,
  [int]$Parts = 6
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$toolsDir = Join-Path $PSScriptRoot ''
$dictDir  = Join-Path $PSScriptRoot 'dict'
if (-not (Test-Path $dictDir)) { New-Item -ItemType Directory -Path $dictDir | Out-Null }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-Lines([string]$p) {
  if (-not (Test-Path $p)) { return @() }
  return [System.IO.File]::ReadAllLines($p, [System.Text.Encoding]::UTF8)
}

function Write-Lines([string]$p, [string[]]$lines) {
  [System.IO.File]::WriteAllText($p, ($lines -join "`r`n") + "`r`n", $utf8NoBom)
}

# drop trailing empty lines (editors often append one)
function Trim-Tail([string[]]$lines) {
  $c = $lines.Count
  while ($c -gt 0 -and $lines[$c - 1].Trim().Length -eq 0) { $c-- }
  if ($c -eq $lines.Count) { return $lines }
  if ($c -eq 0) { return @() }
  return @($lines[0..($c - 1)])
}

# literal backslash-n -> real newline (matches C# "\n" semantics at runtime)
# XML numeric newline entities are decoded by the XAML parser at runtime -> real newline
function Norm([string]$s) {
  if ($null -eq $s) { return '' }
  $r = $s.Replace('\' + 'n', "`n")
  $r = $r -replace '&#x0[aA];', "`n"
  return $r
}

function Esc([string]$s) {
  $sb = New-Object System.Text.StringBuilder
  foreach ($ch in $s.ToCharArray()) {
    switch ($ch) {
      '\'  { [void]$sb.Append('\\') }
      '"'  { [void]$sb.Append('\"') }
      "`n" { [void]$sb.Append('\n') }
      "`r" { [void]$sb.Append('\r') }
      "`t" { [void]$sb.Append('\t') }
      default {
        if ([int]$ch -lt 32) { [void]$sb.Append('\u' + ([int]$ch).ToString('x4')) }
        else { [void]$sb.Append($ch) }
      }
    }
  }
  return $sb.ToString()
}

if ($Mode -eq 'split') {
  $all = New-Object 'System.Collections.Generic.List[string]'
  foreach ($l in (Read-Lines (Join-Path $PSScriptRoot '..\loc-keys.txt'))) { $all.Add($l) }
  foreach ($l in (Read-Lines (Join-Path $PSScriptRoot 'extra-keys.txt'))) { if ($l.Trim().Length -gt 0) { $all.Add($l) } }

  $uniq = $all | Sort-Object -Unique
  $n = $uniq.Count
  $per = [math]::Ceiling($n / $Parts)
  Write-Host ("TOTAL_UNIQUE=" + $n + " PER_PART=" + $per)

  for ($i = 0; $i -lt $Parts; $i++) {
    $chunk = @($uniq | Select-Object -Skip ($i * $per) -First $per)
    if ($chunk.Count -eq 0) { continue }
    Write-Lines (Join-Path $dictDir ("keys-{0}.txt" -f ($i + 1))) $chunk
    Write-Host ("keys-{0}.txt = {1}" -f ($i + 1), $chunk.Count)
  }
}

if ($Mode -eq 'merge') {
  $pairs = New-Object 'System.Collections.Generic.List[object]'
  $bad = $false
  for ($i = 1; $i -le $Parts; $i++) {
    $kp = Join-Path $dictDir ("keys-{0}.txt" -f $i)
    $tp = Join-Path $dictDir ("trans-{0}.txt" -f $i)
    if (-not (Test-Path $kp)) { continue }
    $ks = Trim-Tail (Read-Lines $kp)
    $ts = Trim-Tail (Read-Lines $tp)
    Write-Host ("part {0}: keys={1} trans={2}" -f $i, $ks.Count, $ts.Count)
    if ($ks.Count -ne $ts.Count) { Write-Host ("MISMATCH in part " + $i); $bad = $true; continue }
    for ($j = 0; $j -lt $ks.Count; $j++) {
      $pairs.Add([pscustomobject]@{ K = (Norm $ks[$j]); V = (Norm $ts[$j]) })
    }
  }
  if ($bad) { Write-Host 'ABORT: part line count mismatch'; exit 2 }

  # Core-produced display text (resolved in app by Loc.TCore via placeholders),
  # maintained separately in core-strings.json.
  $corePath = Join-Path $PSScriptRoot 'core-strings.json'
  if (Test-Path $corePath) {
    $coreText = [System.IO.File]::ReadAllText($corePath, [System.Text.Encoding]::UTF8)
    $core = $coreText | ConvertFrom-Json
    $coreCount = 0
    foreach ($p in $core.PSObject.Properties) {
      $pairs.Add([pscustomobject]@{ K = (Norm $p.Name); V = (Norm $p.Value) })
      $coreCount++
    }
    Write-Host ("CORE_STRINGS=" + $coreCount)
  }

  $map = New-Object 'System.Collections.Specialized.OrderedDictionary'
  foreach ($p in ($pairs | Sort-Object -Property K)) {
    if (-not $map.Contains($p.K)) { $map[$p.K] = $p.V }
  }

  $sb = New-Object System.Text.StringBuilder
  [void]$sb.Append("{`n")
  $idx = 0
  foreach ($k in $map.Keys) {
    $idx++
    [void]$sb.Append('  "' + (Esc $k) + '": "' + (Esc $map[$k]) + '"')
    if ($idx -lt $map.Count) { [void]$sb.Append(',') }
    [void]$sb.Append("`n")
  }
  [void]$sb.Append("}`n")

  $out = Join-Path $root 'dotnet\src\Starcloud.App\Strings\en-US.json'
  [System.IO.File]::WriteAllText($out, $sb.ToString(), $utf8NoBom)
  Write-Host ("WROTE " + $out + " ENTRIES=" + $map.Count)
}
