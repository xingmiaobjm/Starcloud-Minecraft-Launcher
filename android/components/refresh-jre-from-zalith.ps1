# 用 ZalithLauncher 2.7.2 的 JRE 重新生成 jre17 / jre21 / jre25 组件包。
#
# 背景：设备上（MatePad BTK-W00，Android 12）用 2.6.1 及更早来源的 JRE 时，JVM 初始化阶段
# 会在 libc 的 free() 处 abort（"Pointer tag ... was truncated"）。2.7.2 的上游 JRE 可能已
# 在启动时自行关闭 ARM64 指针标签，故就地替换这三个组件重测。
#   · jre8 仍来自 FCL 1.3.3.6（旧版本才用，本次不动）
#   · 三个包都按「universal + bin-arm64 合并、去掉 legal/ 与 libjsig 别名」的原口径打包
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\refresh-jre-from-zalith.ps1

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Apk = Join-Path $Root 'pkgs\zalith2-2.7.2-arm64.apk'
$Out = Join-Path $Root 'out'
$Tmp = Join-Path $env:TEMP 'starcloud-jre-refresh'

if (-not (Test-Path $Apk)) { throw "找不到 APK：$Apk" }
Remove-Item -LiteralPath $Tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Tmp, $Out | Out-Null

$TarExe = Join-Path $env:SystemRoot 'System32/tar.exe'
function Invoke-Tar { & $TarExe @args; if ($LASTEXITCODE -ne 0) { throw "tar failed" } }

function Get-JreVersion([string]$stage, [int]$major) {
    # 从 libjvm.so 里扫「major.minor.patch」串
    $jvm = Join-Path $stage 'lib\server\libjvm.so'
    if (Test-Path $jvm) {
        $bytes = [IO.File]::ReadAllBytes($jvm)
        $text = [Text.Encoding]::ASCII.GetString($bytes)
        $m = [regex]::Matches($text, "$major\.\d+\.\d+[+\-A-Za-z0-9._]*") | Select-Object -First 1
        if ($m) { return $m.Value }
    }
    return "$major.0.0"
}

$results = @()
foreach ($major in 17, 21, 25) {
    Write-Host "== jre$major =="
    $base = "assets/runtimes/jre-$major"
    Invoke-Tar -xf $Apk -C $Tmp "$base/universal.tar.xz" "$base/bin-arm64.tar.xz"
    $stage = Join-Path $Tmp "jre$major-stage"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Invoke-Tar -xf (Join-Path $Tmp "$base/universal.tar.xz") -C $stage --exclude 'legal/*'
    Invoke-Tar -xf (Join-Path $Tmp "$base/bin-arm64.tar.xz") -C $stage
    if (-not (Test-Path (Join-Path $stage 'lib\server\libjvm.so'))) {
        throw "jre$major 结构异常：缺少 lib/server/libjvm.so"
    }
    $version = Get-JreVersion $stage $major
    $outFile = Join-Path $Out "jre$major.tar.xz"
    Invoke-Tar -caf $outFile -C $stage .
    $size = [math]::Round((Get-Item $outFile).Length / 1MB, 1)
    Write-Host "   version=$version  size=${size}MB"
    $results += [pscustomobject]@{ Name = "jre$major"; Version = $version }
}

# 更新 manifest.json 里三个组件的 version（组件目录名按 version 解析）
$manifestPath = Join-Path $Out 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($r in $results) {
    $c = $manifest.components | Where-Object { $_.name -eq $r.Name }
    if ($c) { $c.version = $r.Version }
    # sha256 也要重算（部署器校验）
    $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Out "$($r.Name).tar.xz")).Hash.ToLowerInvariant()
    if ($c) {
        if ($c.PSObject.Properties.Name -contains 'sha256') { $c.sha256 = $sha }
    }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host '== manifest 已更新 =='
$results | Format-Table -AutoSize
