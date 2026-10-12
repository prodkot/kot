Unicode true
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!ifndef VERSION
!define VERSION "0.7.0"
!endif
!ifndef PAYLOAD
!error "Pass /DPAYLOAD=published-directory"
!endif
!ifndef EXTRACTOR
!error "Pass /DEXTRACTOR=native-extraction-helper"
!endif
!ifndef PAYLOAD_KB
!error "Pass /DPAYLOAD_KB=installed-payload-size-in-KB"
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
Icon "${PAYLOAD}/kot.ico"
UninstallIcon "${PAYLOAD}/kot.ico"
BrandingText " "
ShowInstDetails show
ShowUninstDetails show
InstallColors f2f4ed 151718
LoadLanguageFile "${NSISDIR}/Contrib/Language files/Russian.nlf"
!include "UI/pages.nsh"
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "kot. требует Windows x64."
    Abort
  ${EndIf}
  SetRegView 64
  SetShellVarContext all
  StrCpy $DesktopShortcut ${BST_CHECKED}
FunctionEnd
Section "kot." SEC_MAIN
  ; File no longer accounts for the application files, which live in the ZIP.
  AddSize ${PAYLOAD_KB}
  SetRegView 64
  SetShellVarContext all
  ${If} ${FileExists} "$INSTDIR\Kot.exe"
    ExecWait '$\"$INSTDIR\Kot.exe$\" --shutdown' $0
    ${If} $0 != 0
      IfSilent +2
      MessageBox MB_ICONSTOP "Не удалось закрыть kot. Закройте приложение и повторите установку."
      SetErrorLevel 1
      Abort
    ${EndIf}
  ${EndIf}
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  ; Only this small native helper is embedded here. The signed ZIP appended to
  ; EXEPATH is the sole application payload, shared with automatic updates.
  File /oname=Kot.Setup.Extract.exe "${EXTRACTOR}"
  DetailPrint "Проверка подписи и установка файлов kot."
  ClearErrors
  StrCpy $0 -1
  ExecWait '$\"$PLUGINSDIR\Kot.Setup.Extract.exe$\" --extract $\"$EXEPATH$\" $\"$INSTDIR$\" $\"${VERSION}$\" $\"$PLUGINSDIR\extract-error.txt$\"' $0
  ${If} ${Errors}
    StrCpy $0 1
  ${EndIf}
  ${If} $0 != 0
    StrCpy $2 "Не удалось проверить или установить пакет kot. Прежние файлы восстановлены, если началась их замена."
    ClearErrors
    FileOpen $1 "$PLUGINSDIR\extract-error.txt" r
    ${IfNot} ${Errors}
      FileReadUTF16LE $1 $2
      FileClose $1
    ${EndIf}
    IfSilent +2
    MessageBox MB_ICONSTOP "$2"
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetOutPath "$INSTDIR"
  !include "install-cleanup.nsh"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\kot."
  CreateShortcut "$SMPROGRAMS\kot.\kot..lnk" "$INSTDIR\Kot.exe" "" "$INSTDIR\kot.ico" 0
  CreateShortcut "$SMPROGRAMS\kot.\Удалить kot..lnk" "$INSTDIR\Uninstall.exe"
  ${If} $DesktopShortcut == ${BST_CHECKED}
    CreateShortcut "$DESKTOP\kot..lnk" "$INSTDIR\Kot.exe" "" "$INSTDIR\kot.ico" 0
  ${Else}
    Delete "$DESKTOP\kot..lnk"
  ${EndIf}
  ExecWait '$\"$INSTDIR\Kot.exe$\" --register-shortcuts' $0
  ${If} $0 != 0
    IfSilent +2
    MessageBox MB_ICONSTOP "Не удалось зарегистрировать ярлыки kot. Повторите установку."
    SetErrorLevel 1
    Abort
  ${EndIf}
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "DisplayName" "kot."
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "DisplayIcon" "$INSTDIR\kot.ico"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\KotVPN" "NoRepair" 1
SectionEnd
Section "Uninstall"
  SetRegView 64
  SetShellVarContext all
  ExecWait '$\"$INSTDIR\Kot.exe$\" --shutdown' $0
  ${If} $0 != 0
    IfSilent +2
    MessageBox MB_ICONSTOP "Не удалось закрыть kot. Закройте приложение и повторите удаление."
    SetErrorLevel 1
    Abort
  ${EndIf}
  ExecWait '$\"$INSTDIR\Kot.exe$\" --disable-startup' $0
  ${If} $0 != 0
    IfSilent +2
    MessageBox MB_ICONSTOP "Не удалось отключить автозапуск kot. Удаление остановлено."
    SetErrorLevel 1
    Abort
  ${EndIf}
  ExecWait '$\"$INSTDIR\Kot.exe$\" --disable-kill-switch' $0
  ${If} $0 != 0
    IfSilent +2
    MessageBox MB_ICONSTOP "Не удалось снять блокировку сети. Удаление остановлено."
    SetErrorLevel 1
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
