# 打包与分发（便携版 zip + NSIS 安装器）设计

日期：2026-09-12
状态：待用户确认
所属：PCL2 平替（分发链路）

## 1. 背景与问题

用户诉求：

> 可以，这样完美，导出安装包/EXE

探查结论：

1. **`.NET` 侧没有任何打包工程**。仓库里不存在 MSIX / WiX / 单文件发布配置，
   [Starcloud.App.csproj](../../../dotnet/src/Starcloud.App/Starcloud.App.csproj) 是
   `WindowsPackageType=None`（**非打包 / unpackaged**）+ `SelfContained=true` +
   `WindowsAppSdkSelfContained=true`，即 `dotnet publish -c Release` 直接产出
   「双击即用、零外部依赖」的完整运行目录，**分发链路的主产出路径就是它**。
2. **旧 Rust/Tauri 侧的打包资产还在，但不能直接用**：
   [installer.nsi](../../../target/release/nsis/x64/installer.nsi)（927 行 Tauri 模板）
   的主程序路径指向早已废弃的 `target\release\starcloud-launcher.exe`，且依赖
   `nsis_tauri_utils` 插件；[package-release.mjs](../../../scripts/package-release.mjs) 提供了
   「产物归档到 `releases/` + 时间戳命名」的既有约定。
3. **缺失 `AppIcon.ico`**。`AppWindow.SetIcon` 与安装器都需要图标文件，仓库里没有。
4. 本机 PATH 中**没有 `makensis`**，但 `%LOCALAPPDATA%\tauri\NSIS\makensis.exe`（v3.11，
   旧 Tauri 工具链残留）齐备可用。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| 打包形态 | **两者都出**：同一份 `dotnet publish` 产物，既打 zip 便携版，也编译 NSIS 安装器 exe |
| 缺失的 `AppIcon.ico` | **用占位符**（程序化生成，非真实美术资源），后续可随时替换 |

## 3. 产物与工具链

| 项 | 值 |
|---|---|
| 便携版 | `releases/Starcloud Minecraft Launcher_<version>_x64-portable-<yyyyMMdd-HHmmss>.zip` |
| 安装器 | `releases/Starcloud Minecraft Launcher_<version>_x64-setup-<yyyyMMdd-HHmmss>.exe` |
| 版本号来源 | [Starcloud.App.csproj](../../../dotnet/src/Starcloud.App/Starcloud.App.csproj) 的 `<Version>`（**单一来源**，脚本直接读 XML，不手工同步） |
| 归档目录 | `releases/`（[.gitignore](../../../.gitignore) 已忽略 `/releases/`） |
| 压缩器 | 便携版 `CompressionLevel.Optimal`；安装器 `SetCompressor /SOLID lzma` |
| 安装器编译 | NSIS 3.11（Unicode），`makensis /V2 /INPUTCHARSET UTF8` |

`makensis` 探测顺序：`Get-Command makensis.exe` → `%LOCALAPPDATA%\tauri\NSIS\makensis.exe`
→ 报错并提示 `winget install NSIS.NSIS`。

### 3.1 NSIS 源码字符集（实测坑，必须记）

[installer.nsi](../../../tools/installer.nsi) 是 **UTF-8 无 BOM**。NSIS 3 默认按当前 ANSI
代码页解析源文件，中文串会变成乱码 —— **编译必须显式传 `/INPUTCHARSET UTF8`**。
该结论由逐字节探针实测确认（写入 `out.txt` 后比对 UTF-8 / GBK 期望字节），已固化进
`installer.nsi` 头部注释与 [build-installer.ps1](../../../tools/build-installer.ps1) 的命令行。
验证方式：产出的 `setup.exe` 版本资源里 `FileDescription` 显示为
「Starcloud Minecraft Launcher 安装程序」（中文正常即字符集正确）。

## 4. 组件设计

### 4.1 新增 `tools/gen-app-icon.ps1`（占位符图标生成器）

输出 `dotnet/src/Starcloud.App/Assets/AppIcon.ico`，7 个尺寸（16/24/32/48/64/128/256），
深色圆角底板 + 等距立方体三面，风格对齐既有壁纸与 `MC-Icons` 的低饱和灰蓝。
手写 ICO 容器：每帧 32bpp `BITMAPINFOHEADER` DIB（`biHeight = h*2`、自下而上 XOR 位图 +
全零 AND 掩码，透明由 alpha 驱动），256 尺寸目录项宽高写 `0`。

**ASCII-only**：脚本里不写中文注释（Windows PowerShell 5.1 把无 BOM 的 `.ps1` 按 ANSI 读，
中文注释会吞掉下一行），沿用 [gen-wallpapers.ps1](../../../tools/gen-wallpapers.ps1) 既有约定。

### 4.2 `Starcloud.App.csproj` 接入图标（两处，用途不同）

```xml
<ApplicationIcon>Assets\AppIcon.ico</ApplicationIcon>     <!-- 嵌进 exe 资源：资源管理器 / 安装器 -->
```

```xml
<Content Include="Assets\AppIcon.ico" CopyToOutputDirectory="PreserveNewest" />  <!-- 随输出目录发布：运行时 SetIcon 用 -->
```

两者缺一不可：只嵌资源则运行时读不到文件，只发布文件则 exe 本身没有图标。

### 4.3 `MainWindow.xaml.cs` 图标路径改为绝对

```csharp
// 图标按 AppContext.BaseDirectory 解析：相对路径会跟随快捷方式的「起始位置」，从开始菜单启动时可能落空
AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
```

安装器会创建开始菜单 / 桌面快捷方式，相对路径在「起始位置」不同时会解析失败。

### 4.4 新增 `tools/installer.nsi`

- 编译期必需参数：`/DPRODUCT_VERSION` `/DPRODUCT_VERSION4` `/DSRCDIR` `/DOUTFILE` `/DAPPICON`
  （`SRCDIR` 缺失直接 `!error`）。
- `Unicode true` + `ManifestDPIAware` / `PerMonitorV2`，语言只装 `SimpChinese`。
- `VIProductVersion` + 6 个 `VIAddVersionKey`（含 `LegalCopyright`，否则 warning 9100）。
- 页面：欢迎 → 选择目录 → 安装 → 完成（可勾选创建桌面快捷方式、可勾选立即运行）；
  卸载：确认 → **自定义页「是否删除本地数据」**（默认不勾） → 卸载 → 完成。

### 4.5 新增 `tools/build-installer.ps1`（一键出两种产物）

4 步：`dotnet publish -c Release -o %TEMP%\starcloud-publish` → 便携版 zip →
`makensis` 编译安装器 → 校验两个产物存在并打印体积。

- 支持 `-SkipPublish`：复用上一次 staging（只改了 `.nsi` 时的快速回路）。
- **ASCII-only**，理由同 §4.1。
- 时间戳只取一次，保证同一批 zip / setup 的文件名后缀一致，便于配对。

## 5. 安装语义

| 项 | 取值 | 理由 |
|---|---|---|
| 权限 | `RequestExecutionLevel user` | 免管理员（与旧 Tauri `currentUser` 一致），不触发 UAC |
| 默认目录 | `$LOCALAPPDATA\Programs\Starcloud` | 用户级安装，卸载无需提权 |
| 已装过 | `ReadRegStr $0 HKCU Software\Starcloud\Starcloud Minecraft Launcher` 有值则沿用原目录 | 升级不装成第二份 |
| 铺文件方式 | `SetOutPath $INSTDIR` + `File /r "${SRCDIR}\*.*"` | 自包含运行时都在目录里，少一个 dll 都起不来 |
| 升级清理 | 若 `$INSTDIR` 有 `uninstall.exe` / `Starcloud.App.exe` / `starcloud-launcher.exe` 任一痕迹 → 先 `RMDir /r "$INSTDIR"` 再重铺 | 换版本后新旧 dll 混放会起不来；旧 Tauri 版主程序否则会永久留在目录里。加痕迹判断是为了不误删用户自选目录中的其它文件 |
| 安装前检查 | `FindWindow` 找标题为产品名的主窗口，命中则「重试 / 取消」 | 覆盖写不进被占用的 exe/dll |
| 卸载登记 | `HKCU\...\Uninstall\Starcloud Minecraft Launcher` 9 项（DisplayName / DisplayIcon / DisplayVersion / Publisher / InstallLocation / UninstallString / NoModify / NoRepair / EstimatedSize） | 控制面板「应用」可正常显示与卸载 |
| 快捷方式 | 开始菜单文件夹（主程序 + 卸载）常建；桌面快捷方式在完成页勾选 | 与旧 Tauri 行为对齐 |
| 卸载保留数据 | **默认保留** `%APPDATA%\starcloud-launcher`（账号 / 设置）与 `%LOCALAPPDATA%\Starcloud`（`crash.log`），勾选才删 | 避免误删登录态 |

> **命令行 `/D=` 的优先级（实测）**：NSIS 的 `/D=` 在 `.onInit` **之前**生效，因此机器上
> 已有安装记录时，`.onInit` 里「沿用原安装目录」会把 `/D=` 覆盖掉（实测把 `/D=` 指向新目录，
> 文件仍落回旧目录）。这与「升级不装成第二份」的意图一致，但会让脚本化静默安装无法改目录，
> 见 §8 风险 7。

## 6. 不做的事（YAGNI）

- **不做 MSIX**：应用是 unpackaged + self-contained，改成打包应用要动 `WindowsPackageType`
  与整套部署语义，且与「免安装 zip」互相矛盾。
- **不装 WebView2 运行时**：WinUI 3 + `WindowsAppSDKSelfContained`，非 WebView2 应用。
- **不做代码签名**：暂无证书；SmartScreen 首启提示接受。
- **不做自动更新器**：本轮只交付「装得上、卸得干净」。
- **不改 CI**（[ci.yml](../../../.github/workflows/ci.yml) 仍是旧 Rust 流程）。

## 7. 实机验收（2026-09-12，实测记录）

### 7.1 产物

| 项 | 实测 |
|---|---|
| `dotnet publish` staging | 519 文件 / 213.9 MB |
| 便携版 zip | **83.2 MB**（519 条目；`Starcloud.App.exe` / `Starcloud.App.dll` / `Starcloud.Core.dll` / `Assets\AppIcon.ico` 均在） |
| 安装器 exe | **59.5 MB**（LZMA solid 压 213.9 MB） |
| setup.exe 版本资源 | `ProductName=Starcloud Minecraft Launcher` / `FileDescription=Starcloud Minecraft Launcher 安装程序` / `FileVersion=ProductVersion=0.2.1` / `CompanyName=LegalCopyright=Starcloud` |

### 7.2 安装 / 卸载回环（沙箱化实测）

做法：先备份并临时移除旧 Tauri 版的两处 HKCU 键（保证不碰旧安装目录），
`setup.exe /S /D=%TEMP%\sc-install-test` 静默装到沙箱 → 校验 → 启动主程序 → 静默卸载 →
还原旧键。**全部通过**：

| 检查项 | 结果 |
|---|---|
| 安装器退出码 | 0 |
| 落地文件 | 520 文件 / 214.3 MB（519 + `uninstall.exe`） |
| 关键文件 | `Starcloud.App.exe`、`Starcloud.App.runtimeconfig.json`、`Starcloud.Core.dll`、`Assets\AppIcon.ico`、**`hostfxr.dll`（自包含运行时）**、`uninstall.exe` 全部存在 |
| 快捷方式 | 开始菜单 `Starcloud Minecraft Launcher.lnk` + `卸载 Starcloud Minecraft Launcher.lnk` 均创建 |
| 注册表 | `DisplayVersion=0.2.1` / `Publisher=Starcloud` / `InstallLocation` 与 `UninstallString` 指向实际目录 / `EstimatedSize=219491`(KB) |
| **启动实测** | 从安装目录启动，12 s 后进程仍在、**未生成新的 `crash.log`** —— 自包含部署与 `SetIcon` 绝对路径均成立 |
| 卸载退出码 | 0 |
| 卸载结果 | 安装目录已删、两处 HKCU 键已删、开始菜单文件夹已删 |
| **保留项正确** | `%APPDATA%\starcloud-launcher` 保持存在（默认保留数据）；旧 Tauri 安装目录未被触碰 |

### 7.3 升级清理与误删护栏（沙箱化实测）

`RMDir /r` 是本设计里唯一会删目录的动作，单独做了三轮针对性验证（同一套「备份/移除旧键 →
沙箱安装 → 校验 → 卸载 → 还原旧键」手法）：

| 场景 | 期望 | 实测 |
|---|---|---|
| 空目录全新安装 | 正常铺 520 文件 | 520 文件 / 214.3 MB，退出码 0 |
| 同目录**重复安装**（预埋 `Stale.FromOlderVersion.dll` + 伪造 `starcloud-launcher.exe`） | 陈旧文件被清掉，不叠加 | 埋入后 522 文件 → 重装后回到 **520 文件 / 214.3 MB**；两个预埋文件均消失；主程序完好 |
| 装到**含无关用户文件、且无本产品痕迹**的目录 | **不删**用户文件 | 用户 `user-notes.txt` 与 `user-folder\keep.txt` 均保留，总计 522 文件 = 520 载荷 + 2 用户文件 |
| 卸载 | 目录 / 注册表 / 快捷方式清干净，用户数据保留 | 全部为真（`%APPDATA%\starcloud-launcher` 未被动） |

### 7.4 回归

`dotnet build dotnet/Starcloud.slnx` → **0 警告 0 错误**；
`dotnet test` → **88 passed / 0 failed**。

## 8. 风险与未决

1. **便携版 zip 的条目名用反斜杠**（`Assets\AppIcon.ico`）。这是
   `[System.IO.Compression.ZipFile]::CreateFromDirectory` 在 Windows 上的原生行为，
   ZIP 规范建议用 `/`。Windows 资源管理器 / 7-Zip / WinRAR 均正确处理；只有
   Linux `unzip` 会把它当成含反斜杠的单个文件名。**本产物仅面向 Windows**，暂不处理；
   若将来要跨平台解压，改用 `ZipArchive.CreateEntry` 逐条写入并统一替换分隔符。
2. **未做代码签名**，首次运行安装器会有 SmartScreen 提示。
3. **默认目录 `$LOCALAPPDATA\Programs\Starcloud` 分支未做真实回环**：三轮实测都走
   「临时移除旧键 + `/D=` 指向沙箱」的等效路径，没真的装进默认目录，因此该分支只验证到
   「目录此前不存在 + 逻辑成立」。两条分支共用同一段铺文件代码，风险低。
4. **默认目录与旧 Tauri 版目录不同**：老用户从 0.2.x Tauri 版升级到本版，
   会因沿用旧 `APP_KEY` 而**就地升级到旧目录**（`$LOCALAPPDATA\Starcloud Minecraft Launcher`）
   而非新目录；反向（先装新版再装旧版）不在考虑范围。两条路径都只有一个卸载登记项，
   不会出现「应用列表里两条 Starcloud」。
5. **`releases/` 里的旧 Tauri 0.2.0/0.2.1 安装包未清理**（已被 git 忽略，仅占本地磁盘）。
6. 图标是**占位符**，与最终视觉稿无关，替换时只需覆盖
   `dotnet/src/Starcloud.App/Assets/AppIcon.ico` 并重跑 `tools/build-installer.ps1`。
7. **`/D=` 无法改目录（已被装机时）**：`.onInit` 的「沿用原目录」压过 `/D=`，见 §5 说明。
   若要修，需在 `.onInit` 里用 `${GetOptions} $CMDLINE "/D=" $0` 判断「用户显式指定了目录」
   并跳过沿用逻辑；本轮默认受众是双击安装的玩家，未做。
