; Starcloud Minecraft Launcher - 自定义单窗口安装器
;
; 形态：一个无边框窗口，三态切换（安装前 → 安装中 → 完成），不再使用 MUI 向导。
; 界面由 tools/gen-installer-ui.py 生成的三档 DPI 位图打底，交互控件叠在上面：
;   - 标题栏 / 关闭 / 最小化 / 拖拽：透明 STATIC 热区
;   - 安装位置：无边框 EDIT + 自绘文件夹按钮
;   - 复选框：自绘位图 STATIC（勾选态换图）
;   - 进度条：原生 msctls_progress32（品牌黄）
; 安装动作在本页的点击回调里直接完成，所以没有独立的 instfiles 页。
;
; 编译：makensis /INPUTCHARSET UTF8 /DSRCDIR=... /DOUTFILE=...
; 日常用 tools\build-installer.ps1（会先生成 installer-payload.nsh）。

Unicode true
ManifestDPIAware true
ManifestDPIAwareness PerMonitorV2
SetCompressor /SOLID lzma

; ── 编译期入参 ──
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
!define DATA_DIR "$APPDATA\starcloud-launcher"
!define LOG_DIR "$LOCALAPPDATA\Starcloud"

!include LogicLib.nsh
!include WinMessages.nsh
!include nsDialogs.nsh
!include FileFunc.nsh
!include x64.nsh
!include "${__FILEDIR__}\installer-assets\ui\layout.nsh"

Name "${PRODUCT_NAME}"
OutFile "${OUTFILE}"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\Starcloud"
XPStyle on
ShowInstDetails nevershow
BrandingText " "

VIProductVersion "${PRODUCT_VERSION4}"
VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey "CompanyName" "${MANUFACTURER}"
VIAddVersionKey "FileDescription" "${PRODUCT_NAME} 安装程序"
VIAddVersionKey "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey "LegalCopyright" "${MANUFACTURER}"

Icon "${APPICON}"
UninstallIcon "${APPICON}"

Var Scale
Var WinW
Var WinH
Var FontH

Var hPage
Var hBg
Var hBmpInstall
Var hBmpProgress
Var hBmpDone
Var hBmpUninstall
Var hBmpUninstalling
Var hBmpChkOn
Var hBmpChkOff
Var BmpFile
Var BmpW
Var BmpH
Var BmpOut
Var Tier

Var hDrag
Var hMin
Var hClose
Var hBmpTitle
Var hBmpMin
Var hBmpClose
Var hBmpFolder
Var hBmpBtnInstall
Var hBmpBtnDone
Var hBmpBtnUninstall
Var hEdit
Var hBtnPath
Var hLinkTerms
Var hLinkPrivacy
Var hBmpLinkTerms
Var hBmpLinkPrivacy
Var hBtnMain
Var hChkAgree
Var hChkCustom
Var hChkRun
Var hChkData
Var hProgress

Var State
Var Agree
Var WantCustom
Var WantRun
Var DelData
Var DoneBytes

; 载荷清单必须在 Var 之后包含：生成的文件里用到 $DoneBytes
!if /FileExists "${__FILEDIR__}\installer-payload.nsh"
  !include "${__FILEDIR__}\installer-payload.nsh"
!else
  !error "installer-payload.nsh 未生成：请用 tools\build-installer.ps1 打包"
!endif

Page custom UiPage UiPageLeave
Page instfiles

UninstPage custom un.UiPage un.UiPageLeave
UninstPage instfiles

; 静默安装（setup.exe /S）：页面不会创建，实际安装动作由 Section 兜底
Function .onInit
  SetShellVarContext current
  ${If} ${Silent}
    StrCpy $State "silent"
  ${EndIf}
  ; 已装过就沿用原安装目录，升级不会装成第二份
  ReadRegStr $0 HKCU "${APP_KEY}" ""
  ${If} $0 != ""
    StrCpy $INSTDIR $0
  ${EndIf}
FunctionEnd

; ── 通用：窗口与缩放 ──

Function PrepWindow
  System::Call 'user32::GetDpiForWindow(i $HWNDPARENT) i .r0'
  ${If} $0 < 48
    StrCpy $0 96
  ${EndIf}
  IntOp $Scale $0 * 100
  IntOp $Scale $Scale / 96

  ; 位图只有 100/150/200 三档：先选「不小于当前缩放的最小档」，再让窗口与所有
  ; 控件坐标都用这个档位 —— 位图 1:1 贴上，底图和控件永远是同一个比例。
  ; 直接按真实 DPI 摆位、位图另按档位缩放的话，125% 这类非整档就会出现
  ; 图标/路径框与底图对不上、下方留白。
  ${If} $Scale <= 100
    StrCpy $Tier 100
  ${ElseIf} $Scale <= 150
    StrCpy $Tier 150
  ${Else}
    StrCpy $Tier 200
  ${EndIf}
  ; 窗口不能高过屏幕：放不下就退一档，宁可用大字号也不让按钮跑出屏幕
  System::Call 'user32::GetSystemMetrics(i 1) i .r3'
  IntOp $8 ${UI_H} * $Tier
  IntOp $8 $8 / 100
  IntOp $9 $3 * 92
  IntOp $9 $9 / 100
  ${If} $8 > $9
    ${If} $Tier == 200
      StrCpy $Tier 150
    ${ElseIf} $Tier == 150
      StrCpy $Tier 100
    ${EndIf}
  ${EndIf}
  StrCpy $Scale $Tier

  IntOp $WinW ${UI_W} * $Scale
  IntOp $WinW $WinW / 100
  IntOp $WinH ${UI_H} * $Scale
  IntOp $WinH $WinH / 100
  IntOp $FontH 15 * $Scale
  IntOp $FontH $FontH / 100

  ; 去掉标题栏：CAPTION|SYSMENU|THICKFRAME|MIN|MAX，再补 WS_POPUP
  System::Call 'user32::GetWindowLong(i $HWNDPARENT, i -16) i .r1'
  IntOp $0 $0 & 0xFF30FFFF
  IntOp $0 $0 | 0x80000000
  System::Call 'user32::SetWindowLong(i $HWNDPARENT, i -16, i r0)'
  System::Call 'user32::SetWindowPos(i $HWNDPARENT, i 0, i 0, i 0, i 0, i 0, i 0x0002|0x0004|0x0020)'
  System::Call 'user32::SetClassLongW(i $HWNDPARENT, i -26, i 0x00020000)'  ; CS_DROPSHADOW

  ; 居中
  System::Call 'user32::GetSystemMetrics(i 0) i .r2'
  System::Call 'user32::GetSystemMetrics(i 1) i .r3'
  IntOp $4 $2 - $WinW
  IntOp $4 $4 / 2
  IntOp $5 $3 - $WinH
  IntOp $5 $5 / 2
  System::Call 'user32::SetWindowPos(i $HWNDPARENT, i 0, i r4, i r5, i $WinW, i $WinH, i 0x0004|0x0010)'

  ; 圆角窗口（半径 18 逻辑像素）
  IntOp $6 $WinW + 1
  IntOp $7 $WinH + 1
  IntOp $8 18 * $Scale
  IntOp $8 $8 / 100
  System::Call 'gdi32::CreateRoundRectRgn(i 0, i 0, i r6, i r7, i r8, i r8) i .r9'
  System::Call 'user32::SetWindowRgn(i $HWNDPARENT, i r9, i 1)'
FunctionEnd

Function FillPage
  ; 隐藏向导自带的按钮条，并让页面铺满整个客户区
  GetDlgItem $0 $HWNDPARENT 1
  ${If} $0 != 0
    ShowWindow $0 ${SW_HIDE}
  ${EndIf}
  GetDlgItem $0 $HWNDPARENT 2
  ${If} $0 != 0
    ShowWindow $0 ${SW_HIDE}
  ${EndIf}
  GetDlgItem $0 $HWNDPARENT 3
  ${If} $0 != 0
    ShowWindow $0 ${SW_HIDE}
  ${EndIf}
  System::Call 'user32::SetWindowPos(i $hPage, i 0, i 0, i 0, i $WinW, i $WinH, i 0x0004)'
FunctionEnd

; 热区 = 背景同位置裁剪出来的位图：STATIC 自带底色，只有贴图才能和底图一致
!macro HitArea hwnd bmp
  Pop ${hwnd}
  SendMessage ${hwnd} 0x172 0 ${bmp}
!macroend

!macro Sc x y w h
  IntOp $R0 ${x} * $Scale
  IntOp $R0 $R0 / 100
  IntOp $R1 ${y} * $Scale
  IntOp $R1 $R1 / 100
  IntOp $R2 ${w} * $Scale
  IntOp $R2 $R2 / 100
  IntOp $R3 ${h} * $Scale
  IntOp $R3 $R3 / 100
!macroend

; 位图按「目标尺寸」加载：GetDpiForWindow 只报整档 DPI，用户的缩放却可能是
; 125% / 175% 这类中间值 —— 只准备 100/150/200 三档素材再 1:1 贴上去的话，
; 非整档机器会拿到偏小的底图贴在偏大的窗口里，控件却按缩放摆位，于是整个界面错位。
; LoadImage 传非零宽高在 NSIS 里会失败（实测返回 0），所以用 GDI StretchBlt 自己缩：
;   目标尺寸 = 当前 DPI 下的实际像素（$BmpW/$BmpH）
Function LoadBmpScaled
  ; 档位与窗口尺寸已经一致（PrepWindow 里把 Scale 对齐到 Tier），直接 1:1 加载。
  ; 路径必须加引号：w $BmpFile 这种写法会让 LoadImage 静默返回 0。
  System::Call 'user32::LoadImageW(i 0, w "$BmpFile", i 0, i 0, i 0, i 0x2010) i .r0'
  StrCpy $BmpOut $0
FunctionEnd

Function LoadUiAssets
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  ${If} $Scale <= 100
    File /r "${__FILEDIR__}\installer-assets\ui\100\*.*"
    StrCpy $Tier 100
  ${ElseIf} $Scale <= 150
    File /r "${__FILEDIR__}\installer-assets\ui\150\*.*"
    StrCpy $Tier 150
  ${Else}
    File /r "${__FILEDIR__}\installer-assets\ui\200\*.*"
    StrCpy $Tier 200
  ${EndIf}

  StrCpy $BmpFile "$PLUGINSDIR\bg-install.bmp"
  StrCpy $BmpW $WinW
  StrCpy $BmpH $WinH
  Call LoadBmpScaled
  StrCpy $hBmpInstall $BmpOut
  StrCpy $BmpFile "$PLUGINSDIR\bg-progress.bmp"
  Call LoadBmpScaled
  StrCpy $hBmpProgress $BmpOut
  StrCpy $BmpFile "$PLUGINSDIR\bg-done.bmp"
  Call LoadBmpScaled
  StrCpy $hBmpDone $BmpOut

  !insertmacro Sc 0 0 18 18
  StrCpy $BmpFile "$PLUGINSDIR\chk-on.bmp"
  StrCpy $BmpW $R2
  StrCpy $BmpH $R3
  Call LoadBmpScaled
  StrCpy $hBmpChkOn $BmpOut
  StrCpy $BmpFile "$PLUGINSDIR\chk-off.bmp"
  Call LoadBmpScaled
  StrCpy $hBmpChkOff $BmpOut

  !insertmacro Sc ${UI_DRAG_X} ${UI_DRAG_Y} ${UI_DRAG_W} ${UI_DRAG_H}
  StrCpy $BmpFile "$PLUGINSDIR\hit-title.bmp"
  StrCpy $BmpW $R2
  StrCpy $BmpH $R3
  Call LoadBmpScaled
  StrCpy $hBmpTitle $BmpOut
  !insertmacro Sc ${UI_MIN_X} ${UI_MIN_Y} ${UI_MIN_W} ${UI_MIN_H}
  StrCpy $BmpFile "$PLUGINSDIR\hit-min.bmp"
  StrCpy $BmpW $R2
  StrCpy $BmpH $R3
  Call LoadBmpScaled
  StrCpy $hBmpMin $BmpOut
  !insertmacro Sc ${UI_CLOSE_X} ${UI_CLOSE_Y} ${UI_CLOSE_W} ${UI_CLOSE_H}
  StrCpy $BmpFile "$PLUGINSDIR\hit-close.bmp"
  StrCpy $BmpW $R2
  StrCpy $BmpH $R3
  Call LoadBmpScaled
  StrCpy $hBmpClose $BmpOut
  !insertmacro Sc ${UI_PATH_BTN_X} ${UI_PATH_BTN_Y} ${UI_PATH_BTN_W} ${UI_PATH_BTN_H}
  StrCpy $BmpFile "$PLUGINSDIR\hit-folder.bmp"
  StrCpy $BmpW $R2
  StrCpy $BmpH $R3
  Call LoadBmpScaled
  StrCpy $hBmpFolder $BmpOut
  !insertmacro Sc ${UI_BTN_X} ${UI_BTN_Y} ${UI_BTN_W} ${UI_BTN_H}
  StrCpy $BmpFile "$PLUGINSDIR\hit-btn-install.bmp"
  StrCpy $BmpW $R2
  StrCpy $BmpH $R3
  Call LoadBmpScaled
  StrCpy $hBmpBtnInstall $BmpOut
  StrCpy $BmpFile "$PLUGINSDIR\hit-btn-done.bmp"
  Call LoadBmpScaled
  StrCpy $hBmpBtnDone $BmpOut
  StrCpy $BmpFile "$PLUGINSDIR\hit-link-terms.bmp"
  Call LoadBmpScaled
  StrCpy $hBmpLinkTerms $BmpOut
  StrCpy $BmpFile "$PLUGINSDIR\hit-link-privacy.bmp"
  Call LoadBmpScaled
  StrCpy $hBmpLinkPrivacy $BmpOut
FunctionEnd

Function un.LoadBmpScaled
  ; 档位与窗口尺寸已经一致（PrepWindow 里把 Scale 对齐到 Tier），直接 1:1 加载。
  ; 路径必须加引号：w $BmpFile 这种写法会让 LoadImage 静默返回 0。
  System::Call 'user32::LoadImageW(i 0, w "$BmpFile", i 0, i 0, i 0, i 0x2010) i .r0'
  StrCpy $BmpOut $0
FunctionEnd

Function un.LoadUninstallAssets
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  ${If} $Scale <= 100
    File /r "${__FILEDIR__}\installer-assets\ui\100\*.*"
    StrCpy $Tier 100
  ${ElseIf} $Scale <= 150
    File /r "${__FILEDIR__}\installer-assets\ui\150\*.*"
    StrCpy $Tier 150
  ${Else}
    File /r "${__FILEDIR__}\installer-assets\ui\200\*.*"
    StrCpy $Tier 200
  ${EndIf}

  StrCpy $BmpFile "$PLUGINSDIR\bg-uninstall.bmp"
  StrCpy $BmpW $WinW
  StrCpy $BmpH $WinH
  Call un.LoadBmpScaled
  StrCpy $hBmpUninstall $BmpOut
  StrCpy $BmpFile "$PLUGINSDIR\bg-uninstalling.bmp"
  Call un.LoadBmpScaled
  StrCpy $hBmpUninstalling $BmpOut

  !insertmacro Sc 0 0 18 18
  StrCpy $BmpFile "$PLUGINSDIR\chk-on.bmp"
  StrCpy $BmpW $R2
  StrCpy $BmpH $R3
  Call un.LoadBmpScaled
  StrCpy $hBmpChkOn $BmpOut
  StrCpy $BmpFile "$PLUGINSDIR\chk-off.bmp"
  Call un.LoadBmpScaled
  StrCpy $hBmpChkOff $BmpOut

  !insertmacro Sc ${UI_DRAG_X} ${UI_DRAG_Y} ${UI_DRAG_W} ${UI_DRAG_H}
  StrCpy $BmpFile "$PLUGINSDIR\hit-title.bmp"
  StrCpy $BmpW $R2
  StrCpy $BmpH $R3
  Call un.LoadBmpScaled
  StrCpy $hBmpTitle $BmpOut
  !insertmacro Sc ${UI_MIN_X} ${UI_MIN_Y} ${UI_MIN_W} ${UI_MIN_H}
  StrCpy $BmpFile "$PLUGINSDIR\hit-min.bmp"
  StrCpy $BmpW $R2
  StrCpy $BmpH $R3
  Call un.LoadBmpScaled
  StrCpy $hBmpMin $BmpOut
  !insertmacro Sc ${UI_CLOSE_X} ${UI_CLOSE_Y} ${UI_CLOSE_W} ${UI_CLOSE_H}
  StrCpy $BmpFile "$PLUGINSDIR\hit-close.bmp"
  StrCpy $BmpW $R2
  StrCpy $BmpH $R3
  Call un.LoadBmpScaled
  StrCpy $hBmpClose $BmpOut
  !insertmacro Sc ${UI_BTN_X} ${UI_BTN_Y} ${UI_BTN_W} ${UI_BTN_H}
  StrCpy $BmpFile "$PLUGINSDIR\hit-btn-uninstall.bmp"
  StrCpy $BmpW $R2
  StrCpy $BmpH $R3
  Call un.LoadBmpScaled
  StrCpy $hBmpBtnUninstall $BmpOut
FunctionEnd
; ── 控件创建 ──

Function CreateChrome
  ; 背景（最底层）
  nsDialogs::CreateControl STATIC 0x5000000E 0 0 0 $WinW $WinH ""
  Pop $hBg

  ; 标题栏拖拽区
  !insertmacro Sc ${UI_DRAG_X} ${UI_DRAG_Y} ${UI_DRAG_W} ${UI_DRAG_H}
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  !insertmacro HitArea $hDrag $hBmpTitle
  ${NSD_OnClick} $hDrag OnDrag

  !insertmacro Sc ${UI_MIN_X} ${UI_MIN_Y} ${UI_MIN_W} ${UI_MIN_H}
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  !insertmacro HitArea $hMin $hBmpMin
  ${NSD_OnClick} $hMin OnMinimize

  !insertmacro Sc ${UI_CLOSE_X} ${UI_CLOSE_Y} ${UI_CLOSE_W} ${UI_CLOSE_H}
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  !insertmacro HitArea $hClose $hBmpClose
  ${NSD_OnClick} $hClose OnClose
FunctionEnd

Function CreateCheckbox
  ; 入参：$R0..$R3 位置，$R4 位图句柄；返回 $R5
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  Pop $R5
  SendMessage $R5 0x172 0 $R4
FunctionEnd

; ── 安装页 ──

Function UiPage
  StrCpy $State "install"
  StrCpy $Agree 0
  StrCpy $WantCustom 0
  StrCpy $WantRun 0

  Call PrepWindow

  nsDialogs::Create 0
  Pop $hPage
  ${If} $hPage == error
    Abort
  ${EndIf}
  Call FillPage
  Call LoadUiAssets
  Call CreateChrome

  ; 背景位图放到最底层（先创建即可）
  SendMessage $hBg 0x172 0 $hBmpInstall

  ; 安装位置
  !insertmacro Sc ${UI_PATH_EDIT_X} ${UI_PATH_EDIT_Y} ${UI_PATH_EDIT_W} ${UI_PATH_EDIT_H}
  nsDialogs::CreateControl EDIT 0x50000080 0 $R0 $R1 $R2 $R3 ""
  Pop $hEdit
  SetCtlColors $hEdit 0x00181310 0x00F8F6F5
  System::Call 'gdi32::CreateFontW(i -$FontH, i 0, i 0, i 0, i 400, i 0, i 0, i 0, i 134, i 0, i 0, i 5, i 0, w "Microsoft YaHei UI") i .r0'
  SendMessage $hEdit ${WM_SETFONT} $0 1
  SendMessage $hEdit ${EM_SETREADONLY} 1 0
  ${NSD_SetText} $hEdit "$INSTDIR"

  !insertmacro Sc ${UI_PATH_BTN_X} ${UI_PATH_BTN_Y} ${UI_PATH_BTN_W} ${UI_PATH_BTN_H}
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  !insertmacro HitArea $hBtnPath $hBmpFolder
  ${NSD_OnClick} $hBtnPath OnBrowse

  ; 复选框
  !insertmacro Sc ${UI_CHK_AGREE_X} ${UI_CHK_AGREE_Y} ${UI_CHK_AGREE_W} ${UI_CHK_AGREE_H}
  StrCpy $R4 $hBmpChkOff
  Call CreateCheckbox
  StrCpy $hChkAgree $R5
  ${NSD_OnClick} $hChkAgree OnToggleAgree

  !insertmacro Sc ${UI_CHK_CUSTOM_X} ${UI_CHK_CUSTOM_Y} ${UI_CHK_CUSTOM_W} ${UI_CHK_CUSTOM_H}
  StrCpy $R4 $hBmpChkOff
  Call CreateCheckbox
  StrCpy $hChkCustom $R5
  ${NSD_OnClick} $hChkCustom OnToggleCustom

  !insertmacro Sc ${UI_CHK_RUN_X} ${UI_CHK_RUN_Y} ${UI_CHK_RUN_W} ${UI_CHK_RUN_H}
  StrCpy $R4 $hBmpChkOff
  Call CreateCheckbox
  StrCpy $hChkRun $R5
  ${NSD_OnClick} $hChkRun OnToggleRun

  ; 主按钮
  !insertmacro Sc ${UI_BTN_X} ${UI_BTN_Y} ${UI_BTN_W} ${UI_BTN_H}
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  !insertmacro HitArea $hBtnMain $hBmpBtnInstall
  ${NSD_OnClick} $hBtnMain OnPrimary

  ; 协议 / 隐私政策：点文字打开官网
  !insertmacro Sc ${UI_LINK_TERMS_X} ${UI_LINK_TERMS_Y} ${UI_LINK_TERMS_W} ${UI_LINK_TERMS_H}
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  Pop $hLinkTerms
  SendMessage $hLinkTerms 0x172 0 $hBmpLinkTerms
  ${NSD_OnClick} $hLinkTerms OnOpenTerms
  !insertmacro Sc ${UI_LINK_PRIVACY_X} ${UI_LINK_PRIVACY_Y} ${UI_LINK_PRIVACY_W} ${UI_LINK_PRIVACY_H}
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  Pop $hLinkPrivacy
  SendMessage $hLinkPrivacy 0x172 0 $hBmpLinkPrivacy
  ${NSD_OnClick} $hLinkPrivacy OnOpenPrivacy

  ; 进度条
  !insertmacro Sc ${UI_PROGRESS_X} ${UI_PROGRESS_Y} ${UI_PROGRESS_W} ${UI_PROGRESS_H}
  nsDialogs::CreateControl "msctls_progress32" 0x50000001 0 $R0 $R1 $R2 $R3 ""
  Pop $hProgress
  SendMessage $hProgress 0x409 0 0x0022D3FF   ; PBM_SETBARCOLOR（品牌黄）
  SendMessage $hProgress 0x2001 0 0x00F4F1F0   ; PBM_SETBKCOLOR

  Call ApplyState
  nsDialogs::Show
FunctionEnd

Function ApplyState
  ${If} $State == "install"
    SendMessage $hBg 0x172 0 $hBmpInstall
    ${NSD_SetText} $hEdit "$INSTDIR"
    ShowWindow $hDrag ${SW_SHOW}
    ShowWindow $hMin ${SW_SHOW}
    ShowWindow $hClose ${SW_SHOW}
    ShowWindow $hEdit ${SW_SHOW}
    ShowWindow $hBtnPath ${SW_SHOW}
    ShowWindow $hChkAgree ${SW_SHOW}
    ShowWindow $hChkCustom ${SW_SHOW}
    SendMessage $hBtnMain 0x172 0 $hBmpBtnInstall
    ShowWindow $hBtnMain ${SW_SHOW}
    ShowWindow $hLinkTerms ${SW_SHOW}
    ShowWindow $hLinkPrivacy ${SW_SHOW}
    ShowWindow $hChkRun ${SW_HIDE}
    ShowWindow $hProgress ${SW_HIDE}
  ${ElseIf} $State == "progress"
    SendMessage $hBg 0x172 0 $hBmpProgress
    ShowWindow $hDrag ${SW_HIDE}
    ShowWindow $hMin ${SW_HIDE}
    ShowWindow $hClose ${SW_HIDE}
    ShowWindow $hEdit ${SW_HIDE}
    ShowWindow $hBtnPath ${SW_HIDE}
    ShowWindow $hChkAgree ${SW_HIDE}
    ShowWindow $hChkCustom ${SW_HIDE}
    ShowWindow $hChkRun ${SW_HIDE}
    ShowWindow $hBtnMain ${SW_HIDE}
    ShowWindow $hLinkTerms ${SW_HIDE}
    ShowWindow $hLinkPrivacy ${SW_HIDE}
    ShowWindow $hProgress ${SW_SHOW}
  ${ElseIf} $State == "done"
    SendMessage $hBg 0x172 0 $hBmpDone
    ShowWindow $hDrag ${SW_SHOW}
    ShowWindow $hMin ${SW_SHOW}
    ShowWindow $hClose ${SW_SHOW}
    ShowWindow $hEdit ${SW_HIDE}
    ShowWindow $hBtnPath ${SW_HIDE}
    ShowWindow $hChkAgree ${SW_HIDE}
    ShowWindow $hChkCustom ${SW_HIDE}
    SendMessage $hBtnMain 0x172 0 $hBmpBtnDone
    ShowWindow $hChkRun ${SW_SHOW}
    ShowWindow $hBtnMain ${SW_SHOW}
    ShowWindow $hLinkTerms ${SW_HIDE}
    ShowWindow $hLinkPrivacy ${SW_HIDE}
    ShowWindow $hProgress ${SW_HIDE}
  ${EndIf}
FunctionEnd

; ── 交互 ──

Function OnDrag
  Pop $0
  System::Call 'user32::ReleaseCapture()'
  SendMessage $HWNDPARENT 0x00A1 2 0
FunctionEnd

Function OnMinimize
  Pop $0
  System::Call 'user32::ShowWindow(i $HWNDPARENT, i 6)'
FunctionEnd

Function OnClose
  Pop $0
  ${If} $State == "progress"
    Return
  ${EndIf}
  System::Call 'user32::PostMessage(i $HWNDPARENT, i 0x0010, i 0, i 0)'
  Quit
FunctionEnd

Function OnToggleAgree
  Pop $0
  ${If} $Agree == 1
    StrCpy $Agree 0
    SendMessage $hChkAgree 0x172 0 $hBmpChkOff
  ${Else}
    StrCpy $Agree 1
    SendMessage $hChkAgree 0x172 0 $hBmpChkOn
  ${EndIf}
FunctionEnd

Function OnToggleCustom
  Pop $0
  ${If} $WantCustom == 1
    StrCpy $WantCustom 0
    SendMessage $hChkCustom 0x172 0 $hBmpChkOff
    SendMessage $hEdit ${EM_SETREADONLY} 1 0
  ${Else}
    StrCpy $WantCustom 1
    SendMessage $hChkCustom 0x172 0 $hBmpChkOn
    SendMessage $hEdit ${EM_SETREADONLY} 0 0
  ${EndIf}
FunctionEnd

Function OnToggleRun
  Pop $0
  ${If} $WantRun == 1
    StrCpy $WantRun 0
    SendMessage $hChkRun 0x172 0 $hBmpChkOff
  ${Else}
    StrCpy $WantRun 1
    SendMessage $hChkRun 0x172 0 $hBmpChkOn
  ${EndIf}
FunctionEnd

Function OnOpenTerms
  Pop $0
  ExecShell "open" "https://starcloudfur.cn/terms"
FunctionEnd

Function OnOpenPrivacy
  Pop $0
  ExecShell "open" "https://starcloudfur.cn/privacy"
FunctionEnd

Function OnBrowse
  Pop $0
  nsDialogs::SelectFolderDialog "选择 Starcloud 的安装位置" "$INSTDIR"
  Pop $1
  ${If} $1 != error
    StrCpy $INSTDIR $1
    ${NSD_SetText} $hEdit "$INSTDIR"
    ${If} $WantCustom != 1
      StrCpy $WantCustom 1
      SendMessage $hChkCustom 0x172 0 $hBmpChkOn
      SendMessage $hEdit ${EM_SETREADONLY} 0 0
    ${EndIf}
  ${EndIf}
FunctionEnd

Function OnPrimary
  Pop $0
  ${If} $State == "done"
    ${If} $WantRun == 1
      ; 动作必须写 "open"：空字符串会让 ShellExecuteEx 失败并弹出错误框，
      ; 表现就是「启动器打开了但安装程序不关」。
      ExecShell "open" "$INSTDIR\${MAIN_EXE}"
    ${EndIf}
    System::Call 'user32::PostMessage(i $HWNDPARENT, i 0x0010, i 0, i 0)'
    Quit
    Return
  ${EndIf}

  ${If} $Agree != 1
    MessageBox MB_OK|MB_ICONINFORMATION "请先阅读并同意《Starcloud 用户服务协议》和《隐私政策》。"
    Return
  ${EndIf}

  ; 从 EDIT 里取用户手填的路径
  ${NSD_GetText} $hEdit $2
  ${If} $2 != ""
    StrCpy $INSTDIR $2
  ${EndIf}

  StrCpy $State "progress"
  Call ApplyState

  Call CheckAppRunning
  ; 升级：只在目录确实属于本产品时清空，避免误删用户自选目录
  ${If} ${FileExists} "$INSTDIR\uninstall.exe"
  ${OrIf} ${FileExists} "$INSTDIR\${MAIN_EXE}"
  ${OrIf} ${FileExists} "$INSTDIR\starcloud-launcher.exe"
    RMDir /r "$INSTDIR"
  ${EndIf}
  CreateDirectory "$INSTDIR"

  StrCpy $DoneBytes 0
  SendMessage $hProgress 0x402 0 0
  Call ExtractPayload

  Call RegisterInstall

  StrCpy $State "done"
  Call ApplyState
FunctionEnd

Function RegisterInstall
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\uninstall.exe"

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

  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXE}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\卸载 ${PRODUCT_NAME}.lnk" "$INSTDIR\uninstall.exe"
  ${If} $WantCustom == 1
    CreateShortcut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${MAIN_EXE}"
  ${EndIf}
FunctionEnd

Function ProgressStep
  ; 先除后乘：字节数 x100 会超过 32 位整数（220MB*100 > 2^31）直接溢出
  IntOp $0 $DoneBytes / 1024
  IntOp $1 ${SC_TOTAL_BYTES} / 1024
  IntOp $0 $0 * 100
  IntOp $0 $0 / $1
  SendMessage $hProgress 0x402 $0 0
  System::Call 'user32::UpdateWindow(i $hProgress)'
FunctionEnd

Function CheckAppRunning
  retry:
  FindWindow $0 "" "${PRODUCT_NAME}"
  StrCmp $0 0 done
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION \
    "检测到 Starcloud 启动器正在运行。$\r$\n请先关闭它（托盘图标也要退出）后点「重试」，或点「取消」放弃本次安装。" \
    IDRETRY retry
  Quit
  done:
FunctionEnd

Function UiPageLeave
  ${If} $State != "done"
    Abort
  ${EndIf}
FunctionEnd

; ── 卸载 ──

Section "Starcloud" SecInstall
  ; 带界面时安装动作在安装页的回调里完成（instfiles 页走不到）；
  ; 静默安装（/S）没有页面，这里把同样的动作做一遍。
  ${If} $State == "silent"
    Call CheckAppRunning
    ${If} ${FileExists} "$INSTDIR\uninstall.exe"
    ${OrIf} ${FileExists} "$INSTDIR\${MAIN_EXE}"
    ${OrIf} ${FileExists} "$INSTDIR\starcloud-launcher.exe"
      RMDir /r "$INSTDIR"
    ${EndIf}
    CreateDirectory "$INSTDIR"
    StrCpy $DoneBytes 0
    Call ExtractPayload
    Call RegisterInstall
  ${EndIf}
SectionEnd

Function un.onInit
  SetShellVarContext current
  ; 关键：卸载器工作目录必须移出安装目录。cwd 占住 $INSTDIR 时，
  ; 后面 RMDir /r 删不掉目录，NSIS 会一直卡在删除阶段（表现为「卸载完全卡死」）
  SetOutPath "$TEMP"
FunctionEnd

Function un.FillPage
  GetDlgItem $0 $HWNDPARENT 1
  ${If} $0 != 0
    ShowWindow $0 ${SW_HIDE}
  ${EndIf}
  GetDlgItem $0 $HWNDPARENT 2
  ${If} $0 != 0
    ShowWindow $0 ${SW_HIDE}
  ${EndIf}
  GetDlgItem $0 $HWNDPARENT 3
  ${If} $0 != 0
    ShowWindow $0 ${SW_HIDE}
  ${EndIf}
  System::Call 'user32::SetWindowPos(i $hPage, i 0, i 0, i 0, i $WinW, i $WinH, i 0x0004)'
FunctionEnd

Function un.CreateCheckbox
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  Pop $R5
  SendMessage $R5 0x172 0 $R4
FunctionEnd

Function un.CreateChrome
  nsDialogs::CreateControl STATIC 0x5000000E 0 0 0 $WinW $WinH ""
  Pop $hBg

  !insertmacro Sc ${UI_DRAG_X} ${UI_DRAG_Y} ${UI_DRAG_W} ${UI_DRAG_H}
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  !insertmacro HitArea $hDrag $hBmpTitle
  ${NSD_OnClick} $hDrag un.OnDrag

  !insertmacro Sc ${UI_MIN_X} ${UI_MIN_Y} ${UI_MIN_W} ${UI_MIN_H}
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  !insertmacro HitArea $hMin $hBmpMin
  ${NSD_OnClick} $hMin un.OnMinimize

  !insertmacro Sc ${UI_CLOSE_X} ${UI_CLOSE_Y} ${UI_CLOSE_W} ${UI_CLOSE_H}
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  !insertmacro HitArea $hClose $hBmpClose
  ${NSD_OnClick} $hClose un.OnClose
FunctionEnd

Function un.UiPage
  StrCpy $State "install"
  StrCpy $DelData 0

  System::Call 'user32::GetDpiForWindow(i $HWNDPARENT) i .r0'
  ${If} $0 < 48
    StrCpy $0 96
  ${EndIf}
  IntOp $Scale $0 * 100
  IntOp $Scale $Scale / 96

  ; 位图只有 100/150/200 三档：先选「不小于当前缩放的最小档」，再让窗口与所有
  ; 控件坐标都用这个档位 —— 位图 1:1 贴上，底图和控件永远是同一个比例。
  ; 直接按真实 DPI 摆位、位图另按档位缩放的话，125% 这类非整档就会出现
  ; 图标/路径框与底图对不上、下方留白。
  ${If} $Scale <= 100
    StrCpy $Tier 100
  ${ElseIf} $Scale <= 150
    StrCpy $Tier 150
  ${Else}
    StrCpy $Tier 200
  ${EndIf}
  ; 窗口不能高过屏幕：放不下就退一档，宁可用大字号也不让按钮跑出屏幕
  System::Call 'user32::GetSystemMetrics(i 1) i .r3'
  IntOp $8 ${UI_H} * $Tier
  IntOp $8 $8 / 100
  IntOp $9 $3 * 92
  IntOp $9 $9 / 100
  ${If} $8 > $9
    ${If} $Tier == 200
      StrCpy $Tier 150
    ${ElseIf} $Tier == 150
      StrCpy $Tier 100
    ${EndIf}
  ${EndIf}
  StrCpy $Scale $Tier

  IntOp $WinW ${UI_W} * $Scale
  IntOp $WinW $WinW / 100
  IntOp $WinH ${UI_H} * $Scale
  IntOp $WinH $WinH / 100

  System::Call 'user32::GetWindowLong(i $HWNDPARENT, i -16) i .r1'
  IntOp $0 $0 & 0xFF30FFFF
  IntOp $0 $0 | 0x80000000
  System::Call 'user32::SetWindowLong(i $HWNDPARENT, i -16, i r0)'
  System::Call 'user32::SetWindowPos(i $HWNDPARENT, i 0, i 0, i 0, i 0, i 0, i 0x0002|0x0004|0x0020)'
  System::Call 'user32::SetClassLongW(i $HWNDPARENT, i -26, i 0x00020000)'

  System::Call 'user32::GetSystemMetrics(i 0) i .r2'
  System::Call 'user32::GetSystemMetrics(i 1) i .r3'
  IntOp $4 $2 - $WinW
  IntOp $4 $4 / 2
  IntOp $5 $3 - $WinH
  IntOp $5 $5 / 2
  System::Call 'user32::SetWindowPos(i $HWNDPARENT, i 0, i r4, i r5, i $WinW, i $WinH, i 0x0004|0x0010)'

  IntOp $6 $WinW + 1
  IntOp $7 $WinH + 1
  IntOp $8 18 * $Scale
  IntOp $8 $8 / 100
  System::Call 'gdi32::CreateRoundRectRgn(i 0, i 0, i r6, i r7, i r8, i r8) i .r9'
  System::Call 'user32::SetWindowRgn(i $HWNDPARENT, i r9, i 1)'

  nsDialogs::Create 0
  Pop $hPage
  ${If} $hPage == error
    Abort
  ${EndIf}
  Call un.FillPage
  Call un.LoadUninstallAssets
  Call un.CreateChrome

  SendMessage $hBg 0x172 0 $hBmpUninstall

  !insertmacro Sc ${UI_CHK_DATA_X} ${UI_CHK_DATA_Y} ${UI_CHK_DATA_W} ${UI_CHK_DATA_H}
  StrCpy $R4 $hBmpChkOff
  Call un.CreateCheckbox
  StrCpy $hChkData $R5
  ${NSD_OnClick} $hChkData un.OnToggleData

  !insertmacro Sc ${UI_BTN_X} ${UI_BTN_Y} ${UI_BTN_W} ${UI_BTN_H}
  nsDialogs::CreateControl STATIC 0x5000010E 0 $R0 $R1 $R2 $R3 ""
  !insertmacro HitArea $hBtnMain $hBmpBtnUninstall
  ${NSD_OnClick} $hBtnMain un.OnUninstall

  nsDialogs::Show
FunctionEnd

Function un.OnToggleData
  Pop $0
  ${If} $DelData == 1
    StrCpy $DelData 0
    SendMessage $hChkData 0x172 0 $hBmpChkOff
  ${Else}
    StrCpy $DelData 1
    SendMessage $hChkData 0x172 0 $hBmpChkOn
  ${EndIf}
FunctionEnd

Function un.OnUninstall
  Pop $0
  SendMessage $hBg 0x172 0 $hBmpUninstalling
  ShowWindow $hChkData ${SW_HIDE}
  ShowWindow $hBtnMain ${SW_HIDE}
  ShowWindow $hClose ${SW_HIDE}
  ShowWindow $hMin ${SW_HIDE}
  ShowWindow $hDrag ${SW_HIDE}
  System::Call 'user32::UpdateWindow(i $HWNDPARENT)'
  ${If} $hBg != 0
    System::Call 'user32::UpdateWindow(i $hBg)'
  ${EndIf}

  Call un.DoUninstall

  SetAutoClose true
  Quit
FunctionEnd

; 真正删东西的那一段：卸载页与静默卸载（/S）共用
Function un.DoUninstall
  ; 静默卸载不经过卸载页，这里再兜一次 cwd
  SetOutPath "$TEMP"
  Call un.CheckAppRunning

  Delete "$INSTDIR\uninstall.exe"
  ; 大目录删除偶发被占用（杀毒扫描 / 资源管理器缩略图）：失败就稍等重试一次，
  ; 不再无限卡在删除阶段
  ClearErrors
  RMDir /r "$INSTDIR"
  IfErrors 0 un_rm_ok
    Sleep 800
    RMDir /r "$INSTDIR"
  un_rm_ok:
  RMDir "$INSTDIR"

  Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\卸载 ${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"

  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "${APP_KEY}"

  ${If} $DelData == 1
    RMDir /r "${DATA_DIR}"
    RMDir /r "${LOG_DIR}"
  ${EndIf}
FunctionEnd

Function un.OnDrag
  Pop $0
  System::Call 'user32::ReleaseCapture()'
  SendMessage $HWNDPARENT 0x00A1 2 0
FunctionEnd

Function un.OnMinimize
  Pop $0
  System::Call 'user32::ShowWindow(i $HWNDPARENT, i 6)'
FunctionEnd

Function un.OnClose
  Pop $0
  Quit
FunctionEnd

Function un.UiPageLeave
  Abort
FunctionEnd

Function un.CheckAppRunning
  ; 旧实现：FindWindow("", "${PRODUCT_NAME}") + MessageBox 重试。卸载器窗口标题本身就是
  ; ${PRODUCT_NAME}，FindWindow 命中卸载器自己 → 点「重试」再次命中 → 死循环卡死。
  ; 改为直接结束启动器与桥进程（没在跑时失败无害）。
  nsExec::Exec '"$SYSDIR\taskkill.exe" /F /T /IM "${MAIN_EXE}"'
  Pop $0
  nsExec::Exec '"$SYSDIR\taskkill.exe" /F /T /IM "Starcloud.Bridge.exe"'
  Pop $0
  Sleep 500
FunctionEnd

Section "Uninstall"
  ; 带界面时在卸载页回调里完成；静默卸载（uninstall.exe /S）没有页面，这里兜底
  IfSilent 0 +2
    Call un.DoUninstall
SectionEnd
