Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!ifndef VERSION
!define VERSION "0.4.1"
!endif
!ifndef PAYLOAD
!error "Pass /DPAYLOAD=published-directory"
!endif
!ifndef OUTPUT
!define OUTPUT "Kot-Setup-${VERSION}-Windows-x64.exe"
!endif
Name "kot."
OutFile "${OUTPUT}"
InstallDir "$PROGRAMFILES64\Kot"
InstallDirRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "InstallLocation"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "kot."
VIAddVersionKey /LANG=1033 "FileDescription" "kot. Windows x64 installer"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Kot contributors"
!define MUI_ICON "${PAYLOAD}/kot.ico"
!define MUI_UNICON "${PAYLOAD}/kot.ico"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${PAYLOAD}/LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Kot.exe"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "kot. требует Windows x64."
    Abort
  ${EndIf}
  SetRegView 64
  SetShellVarContext all
FunctionEnd
Section "kot." SEC_MAIN
  SetRegView 64
  SetShellVarContext all
  ${If} ${FileExists} "$INSTDIR\Kot.exe"
    MessageBox MB_OKCANCEL "Закройте предыдущую версию kot. и отключите VPN перед установкой." IDOK +2
    Abort
  ${EndIf}
  SetOutPath "$INSTDIR"
  !ifdef NSIS_WIN32_MAKENSIS
    File /r "${PAYLOAD}\*"
  !else
    File /r "${PAYLOAD}/*"
  !endif
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\kot."
  CreateShortcut "$SMPROGRAMS\kot.\kot..lnk" "$INSTDIR\Kot.exe"
  CreateShortcut "$SMPROGRAMS\kot.\Удалить kot..lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\kot..lnk" "$INSTDIR\Kot.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "DisplayName" "kot."
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "DisplayIcon" "$INSTDIR\Kot.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "NoRepair" 1
SectionEnd
Section "Uninstall"
  SetRegView 64
  SetShellVarContext all
  ExecWait '$\"$INSTDIR\Kot.exe$\" --shutdown' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "Не удалось закрыть kot. Закройте приложение и повторите удаление."
    Abort
  ${EndIf}
  ExecWait '$\"$INSTDIR\Kot.exe$\" --disable-startup' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "Не удалось отключить автозапуск kot. Удаление остановлено."
    Abort
  ${EndIf}
  !include "uninstall-files.nsh"
  Delete "$SMPROGRAMS\kot.\kot..lnk"
  Delete "$SMPROGRAMS\kot.\Удалить kot..lnk"
  RMDir "$SMPROGRAMS\kot."
  Delete "$DESKTOP\kot..lnk"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  ; Profiles, subscription secrets and HWID live in LocalAppData and are preserved.
SectionEnd
