# 拉取 EasyTier v2.6.4 的 linux musl 静态二进制，改名为 jniLibs/<abi>/lib*.so，供安卓 APK 内置。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts/fetch-kernel.ps1
#
# 为什么必须内置（而不是运行时下载）：
#   安卓 10 起 W^X：应用只能执行 nativeLibraryDir 下的 lib*.so，filesDir 挂载 noexec。
# 为什么命名成 lib*.so：
#   这是安卓对可执行二进制的命名约定，打包后随 APK 落在 nativeLibraryDir。
# LGPL-3.0 合规：
#   以「独立可执行 + 可替换」的子进程形态随包分发即满足要求，本脚本不改写二进制内容。
#
# 哈希钉死：与 GitHub Release 实测值不符一律报错，不静默放行。

$ErrorActionPreference = "Stop"

$Version = "v2.6.4"
$Assets = @(
    @{
        Zip         = "easytier-linux-aarch64-$Version.zip"
        Sha256      = "f533ec25a7ea714e09f645615012200278058525795cc3bb690ff011aec1a70f"
        EntryPrefix = "easytier-linux-aarch64"
        Abi         = "arm64-v8a"
    },
    @{
        Zip         = "easytier-linux-x86_64-$Version.zip"
        Sha256      = "61b659eaedba658fa66fe47d17e1426cdd77e5d02fa15fed447bb4357c09dfd6"
        EntryPrefix = "easytier-linux-x86_64"
        Abi         = "x86_64"
    }
)

$solutionRoot = Split-Path -Parent $PSScriptRoot   # starcloud-multiplayer/
$jniLibs = Join-Path $solutionRoot "src/Starcloud.Multiplayer.Client.Android/jniLibs"
$cache = Join-Path ([IO.Path]::GetTempPath()) "easytier-android-fetch"

Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
New-Item -ItemType Directory -Force -Path $cache | Out-Null

foreach ($asset in $Assets) {
    $zipPath = Join-Path $cache $asset.Zip

    if ((Test-Path $zipPath) -and
        ((Get-FileHash $zipPath -Algorithm SHA256).Hash -eq $asset.Sha256)) {
        Write-Host "缓存命中：$($asset.Zip)"
    }
    else {
        $url = "https://github.com/EasyTier/EasyTier/releases/download/$Version/$($asset.Zip)"
        Write-Host "下载 $url"
        Invoke-WebRequest -Uri $url -OutFile $zipPath

        $actual = (Get-FileHash $zipPath -Algorithm SHA256).Hash
        if ($actual -ne $asset.Sha256) {
            throw "哈希不匹配：期望 $($asset.Sha256)，实际 $actual"
        }
    }

    $abiDir = Join-Path $jniLibs $asset.Abi
    New-Item -ItemType Directory -Force -Path $abiDir | Out-Null

    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($name in @("easytier-core", "easytier-cli")) {
            # zip 内容带一层子目录（easytier-linux-aarch64/），按完整 entry 路径取。
            $entry = $zip.Entries | Where-Object { $_.FullName -eq "$($asset.EntryPrefix)/$name" }
            if ($null -eq $entry) {
                throw "压缩包里没找到 $name（entry：$($asset.EntryPrefix)/$name）"
            }

            $target = Join-Path $abiDir ("lib" + $name + ".so")
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            # Windows PowerShell（.NET Framework）没有 Path.GetRelativePath，手工拼相对路径。
            Write-Host "已释放 $($target.Substring($jniLibs.Length).TrimStart('\', '/'))"
        }
    }
    finally {
        $zip.Dispose()
    }
}

Write-Host "完成：$jniLibs"
