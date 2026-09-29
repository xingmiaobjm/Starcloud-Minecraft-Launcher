# 插件 2.0 打包工具：生成 integrity.json（逐文件 sha256 + package 摘要），可选 ECDSA P-256 签名，
# 最后打成 .mclplug（zip）。
#
# 与本体严格同构（dotnet/src/Starcloud.Core/Plugins/PluginIntegrity.cs）：
#   · 摘要       每个文件 sha256 的小写十六进制；integrity.json 自身与运行时目录 data/ 不入摘要
#   · package    把 files 按路径升序拼接 "path\0hex\n" 后再 sha256（签名与快速比对都用它）
#   · 签名载荷   规范化 JSON（键升序、无空白）：{"id":...,"package":...,"version":...}
#   · 签名算法   ECDSA P-256 + SHA256（.NET 默认的 IEEE P1363 定长 r||s，与本体 VerifyData 一致）
# 安装时由 Starcloud.App/Services/PluginService.cs 逐文件比对并验签，任一处不符即拒绝安装。
#
# 编码说明：本文件是 UTF-8 **带 BOM**。仓库里其他 .ps1 用「纯 ASCII」来规避
# Windows PowerShell 5.1 把无 BOM 文件按 ANSI 读的问题；这里改用 BOM，效果等价且保留中文注释。
#
# 用法：
#   pwsh -File tools\pack-plugin.ps1 -Source examples\plugins\my-plugin
#   pwsh -File tools\pack-plugin.ps1 -Source .\my-plugin -KeyFile .\official-key.pem -KeyId starcloud-official-2026
param(
  [Parameter(Mandatory = $true)][string]$Source,
  [string]$Output,
  [string]$KeyFile,
  [string]$KeyId = 'starcloud-official-2026'
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$src = (Resolve-Path -LiteralPath $Source).Path
if (-not (Test-Path -LiteralPath (Join-Path $src 'manifest.json'))) {
  throw "源目录里没有 manifest.json：$src"
}

$manifest = ([System.IO.File]::ReadAllText((Join-Path $src 'manifest.json'), [System.Text.Encoding]::UTF8)) | ConvertFrom-Json
$id = [string]$manifest.id
$version = [string]$manifest.version
if ([string]$manifest.apiVersion -ne '2.0') {
  throw "只支持 API 2.0 插件（当前 apiVersion=$($manifest.apiVersion)）；1.x 包不需要 integrity.json"
}
if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($version)) {
  throw 'manifest.json 缺少 id 或 version'
}
if (-not $Output) { $Output = Join-Path $src ("{0}-{1}.mclplug" -f $id, $version) }
# 输出默认落在源目录里：枚举时要把产物自身排除，否则第二次打包会把上一次的 zip 打进包里
$script:OutputFull = [System.IO.Path]::GetFullPath($Output)

# integrity.json 自身不入摘要；data/ 是运行时私有目录，也不属于包内容；
# 源目录里遗留的 .mclplug（改了版本号再打一次就会留下旧的）同样排除，别把上一个包打进来
function Test-Included([string]$relative) {
  if ($relative -ieq 'integrity.json') { return $false }
  if ($relative -ilike 'data/*') { return $false }
  if ($relative -ilike '*.mclplug') { return $false }
  return $true
}

function Test-IsOutput([string]$fullName) {
  return $script:OutputFull -and ($fullName -ieq $script:OutputFull)
}

function Get-Files([string]$root) {
  $files = New-Object System.Collections.Specialized.OrderedDictionary
  foreach ($f in (Get-ChildItem -LiteralPath $root -Recurse -File)) {
    if (Test-IsOutput $f.FullName) { continue }
    $rel = [System.IO.Path]::GetRelativePath($root, $f.FullName).Replace('\', '/')
    if (-not (Test-Included $rel)) { continue }
    $files[$rel] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  }
  # 前置逗号：OrderedDictionary 可枚举，直接 return 会被 PowerShell 拆成 DictionaryEntry 数组
  return ,$files
}

function Get-PackageDigest($files) {
  $sb = New-Object System.Text.StringBuilder
  foreach ($k in @($files.Keys | Sort-Object)) {
    [void]$sb.Append($k).Append([char]0).Append([string]$files[$k]).Append("`n")
  }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try { return ([System.BitConverter]::ToString($sha.ComputeHash($utf8NoBom.GetBytes($sb.ToString())))).Replace('-', '').ToLowerInvariant() }
  finally { $sha.Dispose() }
}

$files = Get-Files $src
if ($files.Count -eq 0) { throw "源目录里没有可打包的文件：$src" }
$package = Get-PackageDigest $files

# 签名：对规范化载荷（键升序、无空白）做 ECDSA P-256 / SHA256
$signatureBlock = ''
if ($KeyFile) {
  $pem = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $KeyFile).Path, [System.Text.Encoding]::UTF8)
  $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
  try {
    $ecdsa.ImportFromPem($pem)
    $payload = '{"id":"' + $id + '","package":"' + $package + '","version":"' + $version + '"}'
    $value = [Convert]::ToBase64String($ecdsa.SignData(
      $utf8NoBom.GetBytes($payload),
      [System.Security.Cryptography.HashAlgorithmName]::SHA256))
  }
  finally { $ecdsa.Dispose() }
  $signatureBlock = ",`n  ""signature"": {`n    ""algorithm"": ""ecdsa-p256-sha256"",`n    ""keyId"": ""$KeyId"",`n    ""value"": ""$value""`n  }"
}

# integrity.json（字段顺序与规范 §8.1 一致）
$integrity = New-Object System.Text.StringBuilder
[void]$integrity.Append("{`n")
[void]$integrity.Append("  ""schema"": ""starcloud-integrity/2"",`n")
[void]$integrity.Append("  ""id"": ""$id"",`n")
[void]$integrity.Append("  ""version"": ""$version"",`n")
[void]$integrity.Append("  ""algorithm"": ""sha256"",`n")
[void]$integrity.Append("  ""files"": {`n")
$keys = @($files.Keys | Sort-Object)
for ($i = 0; $i -lt $keys.Count; $i++) {
  [void]$integrity.Append('    "' + $keys[$i] + '": "' + $files[$keys[$i]] + '"')
  if ($i -lt $keys.Count - 1) { [void]$integrity.Append(',') }
  [void]$integrity.Append("`n")
}
[void]$integrity.Append("  },`n")
[void]$integrity.Append("  ""package"": ""$package""$signatureBlock`n")
[void]$integrity.Append("}`n")

# 先在暂存目录里组装（不污染源目录），再打 zip
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('mclplug-pack-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
  foreach ($f in (Get-ChildItem -LiteralPath $src -Recurse -File)) {
    if (Test-IsOutput $f.FullName) { continue }
    $rel = [System.IO.Path]::GetRelativePath($src, $f.FullName).Replace('\', '/')
    if (-not (Test-Included $rel)) { continue }
    $target = Join-Path $stage $rel.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $f.FullName -Destination $target
  }
  [System.IO.File]::WriteAllText((Join-Path $stage 'integrity.json'), $integrity.ToString(), $utf8NoBom)

  $outDir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Output))
  New-Item -ItemType Directory -Path $outDir -Force | Out-Null
  if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }
  [System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, [System.IO.Path]::GetFullPath($Output),
    [System.IO.Compression.CompressionLevel]::Optimal, $false)

  # 自检：把 zip 里的内容再算一遍，必须与刚写入的 integrity.json 一致
  $checkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('mclplug-check-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
  try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory([System.IO.Path]::GetFullPath($Output), $checkDir)
    $verifyFiles = Get-Files $checkDir
    $verifyPackage = Get-PackageDigest $verifyFiles
    if ($verifyPackage -ne $package -or $verifyFiles.Count -ne $files.Count) {
      throw "自检失败：zip 内容摘要与 integrity.json 不一致（package=$verifyPackage）"
    }
  }
  finally { Remove-Item -LiteralPath $checkDir -Recurse -Force -ErrorAction SilentlyContinue }
}
finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ("已打包 {0} {1}" -f $id, $version)
Write-Host ("  文件数   {0}" -f $files.Count)
Write-Host ("  package  {0}" -f $package)
Write-Host ("  签名     {0}" -f $(if ($KeyFile) { "ECDSA P-256（keyId=$KeyId）" } else { '未签名（安装时按「第三方未验证」处理）' }))
Write-Host ("  输出     {0}" -f [System.IO.Path]::GetFullPath($Output))
