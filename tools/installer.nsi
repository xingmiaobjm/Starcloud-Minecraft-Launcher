; Starcloud Minecraft Launcher - NSIS installer script
;
; 编译（源码是 UTF-8 无 BOM，必须显式声明输入字符集，否则中文按 ANSI 误读成乱码）：
;   makensis /INPUTCHARSET UTF8 ^
;     /DPRODUCT_VERSION=0.2.1 /DPRODUCT_VERSION4=0.2.1.0 ^
;     /DSRCDIR=<publish 目录> /DOUTFILE=<输出 exe> /DAPPICON=<AppIcon.ico> ^
;     tools\installer.nsi
; 日常不用手敲：tools\build-installer.ps1 会带上全部参数。
;
; 形态：unpackaged + self-contained（WindowsPackageType=None），
; 因此安装器只做「整目录铺文件 + 快捷方式 + 卸载登记」，不涉及 MSIX / 依赖安装。

Unicode true
ManifestDPIAware true
ManifestDPIAwareness PerMonitorV2
SetCompressor /SOLID lzma

; ── 编译期入参（缺省值仅用于脚本自检，正式打包由 build-installer.ps1 注入）──
!ifndef PRODUCT_VERSION
  !define PRODUCT_VERSION "0.0.0"
!endif
!ifndef PRODUCT_VERSION4
  !define PRODUCT_VERSION4 "${PRODUCT_VERSION}.0"
!endif
!ifndef SRCDIR
  !error "SRCDIR 未定义：请传 /DSRCDIR=<dotnet publish 输出目录>"
!endif
!ifndef OUTFILE
  !define OUTFILE "starcloud-setup.exe"
!endif
!ifndef APPICON
  !define APPICON "${__FILEDIR__}\..\dotnet\src\Starcloud.App\Assets\AppIcon.ico"
!endif

!define PRODUCT_NAME "Starcloud Minecraft Launcher"
!define MANUFACTURER "Starcloud"
!define MAIN_EXE "Starcloud.App.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"
!define APP_KEY "Software\${MANUFACTURER}\${PRODUCT_NAME}"
; 用户数据（卸载时可选删除，见 un.PageDeleteData）
!define DATA_DIR "$APPDATA\starcloud-launcher"
!define LOG_DIR "$LOCALAPPDATA\Starcloud"

; ── MUI 外观 ──
!define MUI_ICON "${APPICON}"
!define MUI_UNICON "${APPICON}"
!define MUI_ABORTWARNING

; ── 品牌安装界面（位图由 tools/gen-installer-assets.py 生成） ──
!define MUI_WELCOMEFINISHPAGE_BITMAP "${__FILEDIR__}\installer-assets\welcome.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_BITMAP "${__FILEDIR__}\installer-assets\header.bmp"
!define MUI_WELCOMEPAGE_TITLE "欢迎安装 Starcloud"
!define MUI_WELCOMEPAGE_TEXT "本向导将把 Starcloud Minecraft Launcher 安装到你的电脑。$\r$\n这是开发版 ${PRODUCT_VERSION}：安装不需要管理员权限，也不会修改系统设置。"
!define MUI_FINISHPAGE_TITLE "Starcloud 已就绪"
!define MUI_FINISHPAGE_TEXT "安装完成，开始菜单里已经放好快捷方式。$\r$\n开发版 ${PRODUCT_VERSION}"
!define MUI_FINISHPAGE_NOAUTOCLOSE
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION RunApp
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 Starcloud"
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_TEXT "创建桌面快捷方式"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION CreateDesktopShortcut

; 安装引导动画：品牌画面淡入 → 停留 → 淡出（NSIS 原生只能是静态帧 + 淡入淡出）。
; MUI2 自己已经占用了 .onGUIInit，必须走它的挂钩，不能自己再定义同名函数。
!define MUI_CUSTOMFUNCTION_GUIINIT StarcloudGuiInit

!include MUI2.nsh
!include FileFunc.nsh
!include x64.nsh

Name "${PRODUCT_NAME}"
BrandingText "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${OUTFILE}"
; 免管理员：整个安装过程只写当前用户的目录与注册表
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\Starcloud"

VIProductVersion "${PRODUCT_VERSION4}"
VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey "CompanyName" "${MANUFACTURER}"
VIAddVersionKey "FileDescription" "${PRODUCT_NAME} 安装程序"
VIAddVersionKey "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "LegalCopyright" "${MANUFACTURER}"

; ── 页面 ──
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
UninstPage custom un.PageDeleteData un.PageDeleteDataLeave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"

Var AppRunningDialogTitle
Var DeleteAppDataCheckbox
Var DeleteAppDataState

Function StarcloudGuiInit
  ; advsplash 在运行期才读图片文件，必须先把位图释放到 $PLUGINSDIR：
  ; 直接写编译机上的绝对路径只有开发机能看到，装到别人机器上就是白屏。
  ; 另注意它要求传「不带 .bmp 后缀」的路径，带了会直接返回 -1（错误）。
  InitPluginsDir
  File /oname=$PLUGINSDIR\splash.bmp "${__FILEDIR__}\installer-assets\splash.bmp"
  advsplash::show 1800 400 500 -1 "$PLUGINSDIR\splash"
  Pop $0
FunctionEnd

Function .onInit
  ; 产物是 win-x64 自包含，32 位系统直接拒绝而不是装出一堆跑不起来的文件
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Starcloud 仅支持 64 位 Windows。"
    Abort
  ${EndIf}

  SetShellVarContext current
  ; 已装过就沿用原安装目录，避免升级时装成第二份
  ReadRegStr $0 HKCU "${APP_KEY}" ""
  ${If} $0 != ""
    StrCpy $INSTDIR $0
  ${EndIf}
FunctionEnd

Function RunApp
  ; 从安装目录启动，工作目录一并给对，保证 Assets 与运行时能被找到
  ExecShell "" "$INSTDIR\${MAIN_EXE}"
FunctionEnd

Function CreateDesktopShortcut
  SetShellVarContext current
  CreateShortcut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXE}"
FunctionEnd

; 安装前确认旧实例已退出：自包含目录的整体覆盖写不进去被占用的 dll/exe
Function CheckAppRunning
  StrCpy $AppRunningDialogTitle "${PRODUCT_NAME} 正在运行"
  retry:
  FindWindow $0 "" "${PRODUCT_NAME}"
  StrCmp $0 0 done
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION \
    "检测到 Starcloud 启动器正在运行。$\r$\n请先关闭它（托盘图标也要退出）后点「重试」，或点「取消」放弃本次安装。" \
    IDRETRY retry
  Quit
  done:
FunctionEnd

Section "Starcloud" SecInstall
  SetShellVarContext current
  Call CheckAppRunning

  ; 升级安装时先清空上一版：自包含发布目录里换了版本的 dll 混着旧的会起不来，
  ; 旧 Tauri 版的 starcloud-launcher.exe 也会一直留在里面。只在目录确实是本产品
  ; 的安装目录（有安装器/主程序痕迹）时才清，避免误删用户自选目录里的其它文件。
  ${If} ${FileExists} "$INSTDIR\uninstall.exe"
  ${OrIf} ${FileExists} "$INSTDIR\${MAIN_EXE}"
  ${OrIf} ${FileExists} "$INSTDIR\starcloud-launcher.exe"
    RMDir /r "$INSTDIR"
  ${EndIf}
  CreateDirectory "$INSTDIR"

  SetOutPath "$INSTDIR"
  DetailPrint "正在部署运行环境（约 220 MB）…"
  ; 整个 publish 目录原样铺开：自包含应用的运行时都在里面，少一个 dll 都起不来
  File /r "${SRCDIR}\*.*"

  DetailPrint "正在登记卸载信息…"

  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; 卸载登记（控制面板 / 设置-应用）
  WriteRegStr HKCU "${APP_KEY}" "" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${MAIN_EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${MANUFACTURER}"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" "$INSTDIR\uninstall.exe"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"

  ; 开始菜单（当前用户）
  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXE}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\卸载 ${PRODUCT_NAME}.lnk" "$INSTDIR\uninstall.exe"
SectionEnd

Function un.onInit
  SetShellVarContext current
FunctionEnd

; 卸载前的二次确认：是否连配置与账号一起删（默认保留，避免误删登录态）
Function un.PageDeleteData
  !insertmacro MUI_HEADER_TEXT "卸载 Starcloud" "选择是否保留本地数据"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 28u "卸载会删除程序本体。$\r$\n下面的数据可以保留，重新安装后会继续沿用："
  Pop $0
  ${NSD_CreateCheckbox} 0 40u 100% 12u "同时删除配置、账号与崩溃日志（%APPDATA%\starcloud-launcher、%LOCALAPPDATA%\Starcloud）"
  Pop $DeleteAppDataCheckbox
  ${NSD_SetState} $DeleteAppDataCheckbox ${BST_UNCHECKED}
  nsDialogs::Show
FunctionEnd

Function un.PageDeleteDataLeave
  ${NSD_GetState} $DeleteAppDataCheckbox $DeleteAppDataState
FunctionEnd

Function un.CheckAppRunning
  retry:
  FindWindow $0 "" "${PRODUCT_NAME}"
  StrCmp $0 0 done
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION \
    "检测到 Starcloud 启动器正在运行。$\r$\n请先关闭它后点「重试」，或点「取消」放弃卸载。" \
    IDRETRY retry
  Quit
  done:
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  Call un.CheckAppRunning

  ; 先删 UninstallString 指向的自己，再整目录清空：自包含目录里没有用户数据
  Delete "$INSTDIR\uninstall.exe"
  RMDir /r "$INSTDIR"
  ; 若用户改过安装目录、留下过空壳目录，一并收掉
  RMDir "$INSTDIR"

  Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\卸载 ${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"

  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "${APP_KEY}"

  ; 勾选了才动用户数据（登录态、实例设置、崩溃日志）
  ${If} $DeleteAppDataState = 1
    RMDir /r "${DATA_DIR}"
    RMDir /r "${LOG_DIR}"
  ${EndIf}
SectionEnd
