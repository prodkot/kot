!ifndef UI_PLUGIN
!error "Pass /DUI_PLUGIN=directory-containing-KotUI.dll"
!endif
!addplugindir /x86-unicode "${UI_PLUGIN}"
Var Dialog
Var Control
Var DesktopShortcut
Var RunApp
Var InstallError
Page custom Welcome
Page directory "" OptionsShow OptionsLeave
Page instfiles "" ProgressShow
Page custom Finish FinishLeave
UninstPage custom un.Confirm
UninstPage instfiles "" un.ProgressShow

!macro InitSkin prefix mode
Function ${prefix}.onGUIInit
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=Manrope.ttf "${PAYLOAD}\ui\fonts\Manrope.ttf"
  KotUI::Init /NOUNLOAD "$PLUGINSDIR\Manrope.ttf" "${VERSION}" "${mode}"
FunctionEnd
!macroend
!insertmacro InitSkin "" "install"
!insertmacro InitSkin "un" "uninstall"
!macro FailInstall
  IfSilent +2
  KotUI::Failure /NOUNLOAD "$InstallError"
  SetErrorLevel 1
  Abort
!macroend

!macro Label text x y width height role
  ${NSD_CreateLabel} 0 0 1 1 "${text}"
  Pop $Control
  KotUI::Control /NOUNLOAD "$Control" "${x}" "${y}" "${width}" "${height}" "${role}"
!macroend
!macro NewPage role
  nsDialogs::Create 1018
  Pop $Dialog
  ${If} $Dialog == error
    Abort
  ${EndIf}
  KotUI::Page /NOUNLOAD "$Dialog" "${role}"
!macroend
!macro ButtonText text
  GetDlgItem $Control $HWNDPARENT 1
  SendMessage $Control ${WM_SETTEXT} 0 "STR:${text}"
!macroend
Function Welcome
  ; Native directory pages only call Leave on Next. Preserve the checkbox on Back too.
  GetDlgItem $Control $Dialog 1008
  ${If} $Control != 0
    SendMessage $Control ${BM_GETCHECK} 0 0 $DesktopShortcut
  ${EndIf}
  !insertmacro NewPage "welcome"
  !insertmacro Label "ПРИВЕТ, ЭТО KOT" 0 2 512 22 "overline"
  !insertmacro Label "Всё готово$\r$\nдля связи." 0 26 512 82 "heading"
  !insertmacro Label "Установим приложение и подготовим его к работе.$\r$\nВаши подключения будут в одном месте." 0 116 512 44 "small"
  ${NSD_CreateButton} 0 0 1 1 "Лицензия MIT"
  Pop $Control
  KotUI::Control /NOUNLOAD "$Control" "0" "369" "120" "28" "button"
  ${NSD_OnClick} $Control License
  !insertmacro Label "Настройки и подписки сохранятся при обновлении." 0 407 512 22 "small"
  !insertmacro ButtonText "Продолжить"
  nsDialogs::Show
FunctionEnd
Function License
  Pop $Control
  SetOutPath "$PLUGINSDIR"
  File /oname=kot-LICENSE.txt "${PAYLOAD}\licenses\kot-LICENSE.txt"
  ExecShell "open" "$PLUGINSDIR\kot-LICENSE.txt"
FunctionEnd
Function OptionsShow
  FindWindow $Dialog "#32770" "" $HWNDPARENT
  KotUI::Page /NOUNLOAD "$Dialog" "options"
  GetDlgItem $Control $Dialog 1008
  SendMessage $Control ${BM_SETCHECK} $DesktopShortcut 0
  !insertmacro ButtonText "Установить"
FunctionEnd
Function OptionsLeave
  GetDlgItem $Control $Dialog 1008
  SendMessage $Control ${BM_GETCHECK} 0 0 $DesktopShortcut
FunctionEnd
Function ProgressShow
  FindWindow $Dialog "#32770" "" $HWNDPARENT
  Delete "$PLUGINSDIR\extract-progress.txt"
  KotUI::Page /NOUNLOAD "$Dialog" "progress" "$PLUGINSDIR\extract-progress.txt"
FunctionEnd
Function Finish
  !insertmacro NewPage "finish"
  !insertmacro Label "Можно подключаться." 0 78 512 48 "heading"
  !insertmacro Label "kot. установлен и готов к работе.$\r$\nДобавьте подписку или выберите сохранённый сервер." 0 140 512 48 "small"
  !insertmacro Label "Версия" 0 204 110 22 "small"
  !insertmacro Label "${VERSION} · Windows x64" 280 204 232 22 "body"
  !insertmacro Label "Установлено в" 0 247 110 22 "small"
  !insertmacro Label "$INSTDIR" 130 245 382 28 "body"
  !insertmacro Label "Ярлык на рабочем столе" 0 293 300 22 "small"
  ${If} $DesktopShortcut == ${BST_CHECKED}
    !insertmacro Label "Создан" 400 293 112 22 "body"
  ${Else}
    !insertmacro Label "Не создавался" 400 293 112 22 "body"
  ${EndIf}
  ${NSD_CreateCheckbox} 0 0 1 1 "Открыть kot после установки"
  Pop $RunApp
  KotUI::Control /NOUNLOAD "$RunApp" "0" "347" "512" "36" "checkbox"
  ${NSD_Check} $RunApp
  GetDlgItem $Control $HWNDPARENT 3
  ShowWindow $Control ${SW_HIDE}
  GetDlgItem $Control $HWNDPARENT 2
  EnableWindow $Control 0
  !insertmacro ButtonText "Готово"
  nsDialogs::Show
FunctionEnd
Function FinishLeave
  ${NSD_GetState} $RunApp $0
  ${If} $0 == ${BST_CHECKED}
    Exec '$\"$INSTDIR\Kot.exe$\"'
  ${EndIf}
  SetErrorLevel 0
  Quit
FunctionEnd
Function un.Confirm
  !insertmacro NewPage "uninstall"
  !insertmacro Label "" 0 0 76 76 "hero"
  !insertmacro Label "Удалить kot.?" 0 103 512 48 "heading"
  !insertmacro Label "Файлы приложения и его ярлыки будут удалены.$\r$\nВаш профиль, подписки и статистика останутся." 0 174 512 56 "body"
  !insertmacro Label "$INSTDIR" 0 266 512 50 "small"
  !insertmacro ButtonText "Удалить"
  nsDialogs::Show
FunctionEnd
Function un.ProgressShow
  FindWindow $Dialog "#32770" "" $HWNDPARENT
  KotUI::Page /NOUNLOAD "$Dialog" "progress" ""
FunctionEnd
