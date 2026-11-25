Unicode true
RequestExecutionLevel user
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
Name "Nodicarta Edge"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\Nodicarta\Edge"
!ifndef FIXTURE
InstallDirRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Nodicarta.Edge" "InstallLocation"
!define SCOPE standard
!define LABEL "Nodicarta Edge public installer"
!else
!define SCOPE fixture
!define LABEL "Nodicarta Edge test installer"
!endif
SetCompressor /SOLID zlib
Icon "${APP_ICON}"
UninstallIcon "${APP_ICON}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "Nodicarta Edge"
VIAddVersionKey /LANG=1033 "FileDescription" "Nodicarta Edge Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "Comments" "${LABEL}"
VIAddVersionKey /LANG=1033 "CompanyName" "Nodicarta"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Nodicarta contributors"
ShowInstDetails show
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_UNCONFIRMPAGE_TEXT_TOP "$(UninstallMcpNotice)"
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"
LangString UninstallMcpNotice ${LANG_ENGLISH} "Uninstalling stops this installation's running Edge CLI and MCP sessions. Any requests they are processing will be interrupted. Your Edge device data is kept."
LangString UninstallMcpNotice ${LANG_SIMPCHINESE} "卸载会关闭本安装的 Edge 命令行及 MCP 会话，中断正在处理的请求。Edge 设备数据会保留。"
Var setupMode

Function .onInit
  SetRegView 64
  !ifndef FIXTURE
    ${If} $INSTDIR == "$LOCALAPPDATA\Programs\Nodicarta\Edge"
      ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Nodicarta.Edge" "InstallLocation"
      StrCmp $0 "" +2 0
        StrCpy $INSTDIR "$0"
    ${EndIf}
  !endif
  !ifdef FIXTURE
    ReadEnvStr $0 "NODICARTA_EDGE_SETUP_TEST_HOME"
    StrCmp $0 "" 0 +3
      SetErrorLevel 1
      Abort
    StrCmp $INSTDIR "$LOCALAPPDATA\Programs\Nodicarta\Edge" 0 +2
      StrCpy $INSTDIR "$0\install"
  !endif
  ${IfNot} ${IsNativeAMD64}
    MessageBox MB_ICONSTOP "Nodicarta Edge requires x64 Windows." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  StrCpy $setupMode "interactive"
  IfSilent 0 +2
    StrCpy $setupMode "quiet"
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} "$0" "/MIGRATEDEV" $1
  IfErrors +2 0
    StrCpy $setupMode "migrate"
  ClearErrors
  ${GetOptions} "$0" "/UPDATE" $1
  IfErrors +2 0
    StrCpy $setupMode "update"
FunctionEnd

Section "Nodicarta Edge"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR\bin"
  File /r "${PACKAGE}\bin\*"
  SetOutPath "$PLUGINSDIR"
  File "${PACKAGE}\SHA256SUMS"
  WriteUninstaller "$PLUGINSDIR\Uninstall.exe"
  ExecWait '"$PLUGINSDIR\bin\ProjectHub.Edge.Desktop.exe" --install-release "$INSTDIR" "$setupMode" "${SCOPE}"' $0
  IfErrors setupFailed
  ${If} $0 == 75
    SetErrorLevel 75
    Quit
  ${EndIf}
  ${If} $0 == 1602
    SetErrorLevel 1602
    Quit
  ${EndIf}
  ${If} $0 != 0
    setupFailed:
    SetErrorLevel 1
    Abort "Nodicarta Edge installation failed. See %TEMP%\Nodicarta-Edge-Setup.log."
  ${EndIf}
  SetErrorLevel 0
SectionEnd

Section "Uninstall"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR\bin"
  CopyFiles /SILENT /FILESONLY "$INSTDIR\ProjectHub.Edge.Desktop.exe" "$PLUGINSDIR\bin"
  IfErrors uninstallFailed
  ExecWait '"$PLUGINSDIR\bin\ProjectHub.Edge.Desktop.exe" --install-release "$INSTDIR" "uninstall" "${SCOPE}"' $0
  IfErrors uninstallFailed
  StrCmp $0 0 uninstallDone
  uninstallFailed:
    SetErrorLevel 1
    Abort "Could not uninstall Nodicarta Edge. See %TEMP%\Nodicarta-Edge-Setup.log."
  uninstallDone:
    SetErrorLevel 0
SectionEnd
