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
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"
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
  ${If} $0 != 0
    setupFailed:
    SetErrorLevel 1
    Abort "Nodicarta Edge installation failed. See %TEMP%\Nodicarta-Edge-Setup.log."
  ${EndIf}
  SetErrorLevel 0
SectionEnd

Section "Uninstall"
  InitPluginsDir
  FileOpen $0 "$INSTDIR\active-release" r
  IfErrors uninstallFailed
  FileRead $0 $1
  FileClose $0
  StrLen $0 $1
  StrCmp $0 12 0 uninstallFailed
  StrCpy $2 0
  checkDigit:
    StrCpy $3 $1 1 $2
    StrCpy $4 0
    findDigit:
      StrCpy $5 "0123456789abcdef" 1 $4
      StrCmpS $3 $5 foundDigit
      IntOp $4 $4 + 1
      IntCmp $4 16 uninstallFailed findDigit uninstallFailed
    foundDigit:
    IntOp $2 $2 + 1
    IntCmp $2 12 checkDone checkDigit uninstallFailed
  checkDone:
  SetOutPath "$PLUGINSDIR\bin"
  CopyFiles /SILENT "$INSTDIR\$1\bin\*.*" "$PLUGINSDIR\bin"
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
