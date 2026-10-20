# 运行组件打包脚本：从 FCL APK（同源组件，哈希已核对）提取运行时组件，
# 从 Zalith2 APK 补充 jre25（FCL 1.3.3.6 只带 jre8/17/21），
# 打成 9 个组件包并生成 out/manifest.json（asset: 伪协议，宿主 APK 内置分发）。
# 用法：pwsh -File fetch-components.ps1（需 PATH 中有 tar；输出在 android/components/out/）

$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Apk = Join-Path $Root 'pkgs\fcl-1.3.3.6-arm64.apk'
$ZalithApk = Join-Path $Root 'pkgs\zalith2-2.6.1-arm64.apk'
$Out = Join-Path $Root 'out'
$Tmp = Join-Path $env:TEMP 'starcloud-fetch-components'

if (-not (Test-Path $Apk)) { throw "找不到 FCL APK：$Apk" }
if (-not (Test-Path $ZalithApk)) { throw "找不到 Zalith2 APK：$ZalithApk（jre25 组件来源）" }

# ---------------------------------------------------------------- 工具函数

function Get-PkgSha256([string]$path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

function New-NativeZip([string]$zipPath, [hashtable]$entries) {
    # entries: zip 条目名 -> 源文件
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $entries.Keys) {
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $src = [IO.File]::OpenRead($entries[$name])
            try {
                $dst = $entry.Open()
                try { $src.CopyTo($dst) } finally { $dst.Dispose() }
            } finally { $src.Dispose() }
        }
    } finally { $zip.Dispose() }
}

function Get-JreVersion([string]$stageDir, [int]$major) {
    # FCL 裁剪掉了 JRE 的 release 文件，真实版本号从 libjvm.so 内嵌版本串解析
    $jvm = if ($major -eq 8) { Join-Path $stageDir 'lib\aarch64\server\libjvm.so' }
           else { Join-Path $stageDir 'lib\server\libjvm.so' }
    if (-not (Test-Path $jvm)) { throw "找不到 libjvm.so：$jvm" }
    $text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($jvm))
    if ($major -eq 8) {
        $m = [regex]::Match($text, '1\.8\.0_\d+')
    } else {
        $m = [regex]::Match($text, 'JDK 64-Bit Server VM \((\d+\.\d+\.\d+)')
        if ($m.Success) { $m = [regex]::Match($m.Value, '\d+\.\d+\.\d+') }
    }
    if (-not $m.Success) { throw "无法从 libjvm.so 解析 JRE $major 版本号" }
    return $m.Value
}

function Invoke-Tar {
    # tar 失败必须中止：半截产物会让部署端哈希/解压连环失败
    & tar @args
    if ($LASTEXITCODE -ne 0) { throw "tar 执行失败（exit $LASTEXITCODE）：tar $($args -join ' ')" }
}

function New-JrePackage([string]$name, [int]$major, [string]$javaDir, [string]$workDir) {
    # universal（跨架构 class/资源）+ bin-arm64（arm64 原生库）解到同一 staging 合并成完整 JRE，
    # 再以无顶层根目录打 tar.xz（部署器 SafeJoin 忽略 "./" 段）。
    # 两类条目 Windows bsdtar 无法落地且运行时不需要，排除：
    #   legal/*     —— 许可证符号链接（manifest 已带 license 字段）
    #   libjsig.so 的 server 目录别名 —— jre8 唯一链接条目，实体文件仍在 lib/aarch64/ 下
    $stage = Join-Path $workDir "$name-stage"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Invoke-Tar -xf (Join-Path $javaDir "jre$major\universal.tar.xz") -C $stage --exclude 'legal/*'
    Invoke-Tar -xf (Join-Path $javaDir "jre$major\bin-arm64.tar.xz") -C $stage `
        --exclude './lib/aarch64/server/libjsig.so'
    $outFile = Join-Path $Out "$name.tar.xz"
    Invoke-Tar -caf $outFile -C $stage .
    [pscustomobject]@{ Version = Get-JreVersion $stage $major }
}

function New-Jre25Package([string]$zalithApk, [string]$workDir) {
    # jre25 来源：Zalith2 APK 的 assets/runtimes/jre-25（FCL 1.3.3.6 只带 8/17/21）。
    # 同为 Pojav 血统 JRE 裁剪包，结构一致（universal + bin-arm64，lib/server/libjvm.so）；
    # 版本号直接读 APK 内置 version 文件，缺文件再回退 libjvm.so 扫描
    $stage = Join-Path $workDir 'jre25-stage'
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Invoke-Tar -xf $zalithApk -C $workDir `
        assets/runtimes/jre-25/universal.tar.xz assets/runtimes/jre-25/bin-arm64.tar.xz `
        assets/runtimes/jre-25/version
    Invoke-Tar -xf (Join-Path $workDir 'assets\runtimes\jre-25\universal.tar.xz') -C $stage --exclude 'legal/*'
    Invoke-Tar -xf (Join-Path $workDir 'assets\runtimes\jre-25\bin-arm64.tar.xz') -C $stage
    if (-not (Test-Path (Join-Path $stage 'lib\server\libjvm.so'))) {
        throw "Zalith jre-25 结构异常：缺少 lib/server/libjvm.so"
    }
    # 版本号优先扫 libjvm.so（Zalith 的 version 文件内容是 git hash，不适合展示），
    # 扫描失败再回退 version 文件
    $versionFile = Join-Path $workDir 'assets\runtimes\jre-25\version'
    $version = try { Get-JreVersion $stage 25 } catch {
        if (Test-Path $versionFile) { (Get-Content $versionFile -TotalCount 1).Trim() }
        else { throw }
    }
    $outFile = Join-Path $Out 'jre25.tar.xz'
    Invoke-Tar -caf $outFile -C $stage .
    [pscustomobject]@{ Version = $version }
}

# ---------------------------------------------------------------- 准备

Remove-Item -LiteralPath $Out -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $Tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Out, $Tmp | Out-Null

Write-Host '== 1/4 提取 APK 条目 =='
Invoke-Tar -xf $Apk -C $Tmp `
    assets/app_runtime/java/jre8/universal.tar.xz  assets/app_runtime/java/jre8/bin-arm64.tar.xz `
    assets/app_runtime/java/jre17/universal.tar.xz assets/app_runtime/java/jre17/bin-arm64.tar.xz `
    assets/app_runtime/java/jre21/universal.tar.xz assets/app_runtime/java/jre21/bin-arm64.tar.xz
Invoke-Tar -xf $Apk -C $Tmp assets/app_runtime/lwjgl/3.3.3 assets/app_runtime/lwjgl/3.4.1 assets/app_runtime/jna
Invoke-Tar -xf $Apk -C $Tmp `
    lib/arm64-v8a/libOSMesa_81.so lib/arm64-v8a/libOSMesa_8.so lib/arm64-v8a/libglapi.so `
    lib/arm64-v8a/libEGL_mesa.so lib/arm64-v8a/libzink_dri.so lib/arm64-v8a/libvulkan_freedreno.so `
    lib/arm64-v8a/libVkLayer_khronos_timeline_semaphore.so lib/arm64-v8a/libglxshim.so `
    lib/arm64-v8a/libspirv-cross-c-shared.so lib/arm64-v8a/libgl4es_114.so lib/arm64-v8a/libopenal.so lib/arm64-v8a/libpojavexec.so lib/arm64-v8a/libpojavexec_awt.so `
    lib/arm64-v8a/libSDL3.so `
    lib/arm64-v8a/libc++_shared.so `
    lib/arm64-v8a/libfcl.so lib/arm64-v8a/libandroidnsbypass.so lib/arm64-v8a/libbytehook.so `
    lib/arm64-v8a/liblinkerhook.so

$JavaDir = Join-Path $Tmp 'assets\app_runtime\java'
$LwjglDir = Join-Path $Tmp 'assets\app_runtime\lwjgl\3.3.3'
$LibDir = Join-Path $Tmp 'lib\arm64-v8a'

Write-Host '== 2/4 打 JRE 包 =='
$jre8  = New-JrePackage 'jre8'  8  $JavaDir $Tmp
$jre17 = New-JrePackage 'jre17' 17 $JavaDir $Tmp
$jre21 = New-JrePackage 'jre21' 21 $JavaDir $Tmp
$jre25 = New-Jre25Package $ZalithApk $Tmp
Write-Host "jre8=$($jre8.Version) jre17=$($jre17.Version) jre21=$($jre21.Version) jre25=$($jre25.Version)"

Write-Host '== 3/4 打 lwjgl3 与 native 组件包 =='
# lwjgl：全部 jar 在 zip 根 + natives/arm64-v8a 保结构（LaunchSpecBuilder 按 glob 递归收 jar）。
# 两套并存：3.3.3 服务 1.x 老版本；3.4.1（FCL 定制、含 SDL-over-GLFW 桥接类）服务 26.x 新版本
function New-LwjglPackage([string]$lwjglDir, [string]$name, [int]$minJars, [hashtable]$extraEntries = @{}) {
    $jars = Get-ChildItem -LiteralPath $lwjglDir -Filter '*.jar' | Sort-Object Name
    if ($jars.Count -lt $minJars) { throw "$name jar 数量异常：$($jars.Count)（期望不少于 $minJars）" }
    $entries = @{}
    foreach ($jar in $jars) { $entries[$jar.Name] = $jar.FullName }
    foreach ($so in (Get-ChildItem -LiteralPath (Join-Path $lwjglDir 'natives\arm64-v8a') -Filter '*.so')) {
        $entries["natives/arm64-v8a/$($so.Name)"] = $so.FullName
    }
    foreach ($k in $extraEntries.Keys) { $entries[$k] = $extraEntries[$k] }
    New-NativeZip (Join-Path $Out "$name.zip") $entries
}

New-LwjglPackage (Join-Path $Tmp 'assets\app_runtime\lwjgl\3.3.3') 'lwjgl3' 12
# LWJGL 3.4 时代补名/补库注入（MC 26.x 的 NativeLibrariesBootstrap 引导必需）：
#   libspirv-cross.so —— spvc 绑定改挂独立共享库，FCL 只有上游 c-shared 命名产物，以 C API 同源实现补名
#   libSDL3.so        —— SDL 窗口/输入后端，FCL 放在 lib 根而非 lwjgl natives，需随包分发
New-LwjglPackage (Join-Path $Tmp 'assets\app_runtime\lwjgl\3.4.1') 'lwjgl341' 12 @{
    'natives/arm64-v8a/libspirv-cross.so' = Join-Path $LibDir 'libspirv-cross-c-shared.so'
    'natives/arm64-v8a/libSDL3.so'        = Join-Path $LibDir 'libSDL3.so'
}

# 纯 native 组件：so 平铺 zip 根（FCL 原始产物名，LaunchSpecBuilder 按名注入）
New-NativeZip (Join-Path $Out 'openal.zip') @{
    'libopenal.so' = Join-Path $LibDir 'libopenal.so'
}
New-NativeZip (Join-Path $Out 'gl4es.zip') @{
    'libgl4es_114.so' = Join-Path $LibDir 'libgl4es_114.so'
}
New-NativeZip (Join-Path $Out 'mesa-zink.zip') @{
    'libOSMesa_81.so'                            = Join-Path $LibDir 'libOSMesa_81.so'
    'libOSMesa_8.so'                             = Join-Path $LibDir 'libOSMesa_8.so'
    'libglapi.so'                                = Join-Path $LibDir 'libglapi.so'
    'libEGL_mesa.so'                             = Join-Path $LibDir 'libEGL_mesa.so'
    'libzink_dri.so'                             = Join-Path $LibDir 'libzink_dri.so'
    'libvulkan_freedreno.so'                     = Join-Path $LibDir 'libvulkan_freedreno.so'
    'libVkLayer_khronos_timeline_semaphore.so'   = Join-Path $LibDir 'libVkLayer_khronos_timeline_semaphore.so'
    'libglxshim.so'                              = Join-Path $LibDir 'libglxshim.so'
    'libspirv-cross-c-shared.so'                 = Join-Path $LibDir 'libspirv-cross-c-shared.so'
}
# glfw-stub：Pojav 血统 GLFW 实现桥（libglfw.so 在 FCL 中不存在，GLFW 符号由 libpojavexec.so 提供）。
# libfcl/libandroidnsbypass/libbytehook 是 libpojavexec 的 DT_NEEDED 依赖（ELF 动态段取证），
# 缺一即 classloader namespace 内 dlopen 失败（崩溃 #11 根因）；
# liblinkerhook.so（-z global）全局拦截 android_dlopen_ext，把 Vulkan 驱动加载转往
# DRIVER_PATH（FCL turnip 链路），MC 26.x Vulkan 后端需要
New-NativeZip (Join-Path $Out 'glfw-stub.zip') @{
    'libpojavexec.so'       = Join-Path $LibDir 'libpojavexec.so'
    'libpojavexec_awt.so'   = Join-Path $LibDir 'libpojavexec_awt.so'
    'libc++_shared.so'      = Join-Path $LibDir 'libc++_shared.so'
    'libfcl.so'             = Join-Path $LibDir 'libfcl.so'
    'libandroidnsbypass.so' = Join-Path $LibDir 'libandroidnsbypass.so'
    'libbytehook.so'        = Join-Path $LibDir 'libbytehook.so'
    'liblinkerhook.so'      = Join-Path $LibDir 'liblinkerhook.so'
}

# jna：MC 26.x 引导期（NativeLibrariesBootstrap / oshi SystemReport）依赖 JNA，
# 版本自带 jna.jar 里的 jnidispatch 是桌面 glibc 构建，Android 上 dlopen 报 libc.so.6 缺失；
# 用 FCL 预置的 bionic 版 libjnidispatch.so 按 JNA 版本分目录打包，
# 宿主启动时以 -Djna.boot.library.path 注入对应版本目录（JNA 优先于 jar 内解压）
$jnaEntries = @{}
foreach ($ver in (Get-ChildItem -LiteralPath (Join-Path $Tmp 'assets\app_runtime\jna') -Directory | Sort-Object Name)) {
    $so = Join-Path $ver.FullName 'natives\arm64-v8a\libjnidispatch.so'
    if (-not (Test-Path $so)) { throw "JNA $($ver.Name) 结构异常：缺少 natives/arm64-v8a/libjnidispatch.so" }
    $jnaEntries["$($ver.Name)/libjnidispatch.so"] = $so
}
if ($jnaEntries.Count -eq 0) { throw 'FCL APK 内未找到任何 JNA natives' }
New-NativeZip (Join-Path $Out 'jna.zip') $jnaEntries

Write-Host '== 4/4 生成 manifest.json =='
# 组件版本与许可证（jre 真实版本来自 libjvm.so 扫描；native 组件版本沿用 FCL 产物命名）
$components = @(
    @{ name = 'jre8';      version = $jre8.Version;  file = 'jre8.tar.xz';      archive = 'TarXz'; license = 'GPL-2.0-with-classpath-exception' },
    @{ name = 'jre17';     version = $jre17.Version; file = 'jre17.tar.xz';     archive = 'TarXz'; license = 'GPL-2.0-with-classpath-exception' },
    @{ name = 'jre21';     version = $jre21.Version; file = 'jre21.tar.xz';     archive = 'TarXz'; license = 'GPL-2.0-with-classpath-exception' },
    @{ name = 'jre25';     version = $jre25.Version; file = 'jre25.tar.xz';     archive = 'TarXz'; license = 'GPL-2.0-with-classpath-exception' },
    @{ name = 'lwjgl3';    version = '3.3.3';        file = 'lwjgl3.zip';       archive = 'Zip';   license = 'BSD-3-Clause' },
    @{ name = 'lwjgl341';  version = '3.4.1-r2';      file = 'lwjgl341.zip';     archive = 'Zip';   license = 'BSD-3-Clause' },
    @{ name = 'openal';    version = 'fcl-1.3.3.6';  file = 'openal.zip';       archive = 'Zip';   license = 'LGPL-2.1' },
    @{ name = 'gl4es';     version = '114';          file = 'gl4es.zip';        archive = 'Zip';   license = 'MIT' },
    @{ name = 'mesa-zink'; version = '81';           file = 'mesa-zink.zip';    archive = 'Zip';   license = 'MIT' },
    @{ name = 'glfw-stub'; version = 'fcl-1.3.3.6-r2';  file = 'glfw-stub.zip';    archive = 'Zip';   license = 'GPL-3.0' },
    @{ name = 'jna';       version = 'fcl-1.3.3.6';  file = 'jna.zip';          archive = 'Zip';   license = 'Apache-2.0 OR LGPL-2.1' }
)
$json = [Text.StringBuilder]::new()
[void]$json.AppendLine('{')
[void]$json.AppendLine('    "manifest_version": 1,')
[void]$json.AppendLine('    "components": [')
for ($i = 0; $i -lt $components.Count; $i++) {
    $c = $components[$i]
    $sha = Get-PkgSha256 (Join-Path $Out $c.file)
    $comma = if ($i -lt $components.Count - 1) { ',' } else { '' }
    [void]$json.AppendLine(
        "        { ""name"": ""$($c.name)"", ""version"": ""$($c.version)"", ""url"": ""asset://components/$($c.file)""," )
    [void]$json.AppendLine(
        "          ""sha256"": ""$sha"", ""archive"": ""$($c.archive)"", ""license"": ""$($c.license)"" }$comma")
}
[void]$json.AppendLine('    ]')
[void]$json.AppendLine('}')
[IO.File]::WriteAllText((Join-Path $Out 'manifest.json'), $json.ToString(), [Text.UTF8Encoding]::new($false))

Remove-Item -LiteralPath $Tmp -Recurse -Force
Write-Host ''
Write-Host '== 产物清单 =='
foreach ($c in $components) {
    $file = Get-Item (Join-Path $Out $c.file)
    '{0,-14} {1,-12} {2,12:N0} 字节  {3}' -f $c.name, $c.version, $file.Length, $c.file
}
Write-Host ''
Write-Host "manifest.json 已生成：$(Join-Path $Out 'manifest.json')"
