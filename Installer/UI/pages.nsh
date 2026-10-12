!ifndef UI_PLUGIN
!error "Pass /DUI_PLUGIN=directory-containing-KotUI.dll"
!endif
!addplugindir /x86-unicode "${UI_PLUGIN}"
Var Dialog
Var Control
Var DesktopShortcut
Var RunApp
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
  File /oname=Manrope.ttf "${PAYLOAD}/ui/fonts/Manrope.ttf"
  KotUI::Init /NOUNLOAD "$PLUGINSDIR\Manrope.ttf" "${VERSION}" "${mode}"
FunctionEnd
!macroend
!insertmacro InitSkin "" "install"
!insertmacro InitSkin "un" "uninstall"

!macro Label text x y width height role
  ${NSD_CreateLabel} 0 0 1 1 "${text}"
  Pop $Control
  KotUI::Control /NOUNLOAD "$Control" "${x}" "${y}" "${width}" "${height}" "${role}"
!macroend
!macro NewPage
  nsDialogs::Create 1018
  Pop $Dialog
  ${If} $Dialog == error
    Abort
  ${EndIf}
  KotUI::Page /NOUNLOAD "$Dialog" "custom"
!macroend
!macro ButtonText text
  GetDlgItem $Control $HWNDPARENT 1
  SendMessage $Control ${WM_SETTEXT} 0 "STR:${text}"
!macroend
Function Welcome
  !insertmacro NewPage
  !insertmacro Label "" 0 6 76 76 "hero"
  !insertmacro Label "Привет, это kot." 0 108 568 44 "heading"
  !insertmacro Label "Всё для подключения в одном месте.$\r$\nУстановим приложение и подготовим его к работе." 0 168 568 58 "body"
  ${NSD_CreateButton} 0 0 1 1 "Лицензия"
  Pop $Control
  KotUI::Control /NOUNLOAD "$Control" "0" "254" "150" "32" "button"
  ${NSD_OnClick} $Control License
  !insertmacro ButtonText "Далее"
  nsDialogs::Show
FunctionEnd
Function License
  Pop $Control
  SetOutPath "$PLUGINSDIR"
  File /oname=kot-LICENSE.txt "${PAYLOAD}/licenses/kot-LICENSE.txt"
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
  KotUI::Page /NOUNLOAD "$Dialog" "progress"
FunctionEnd
Function Finish
  !insertmacro NewPage
  !insertmacro Label "" 0 6 76 76 "hero"
  !insertmacro Label "Готово. Можно подключаться." 0 108 568 48 "heading"
  !insertmacro Label "kot. установлен и готов к работе.$\r$\nДобавьте подписку или выберите сохранённый сервер." 0 174 568 54 "body"
  ${NSD_CreateCheckbox} 0 0 1 1 "Запустить kot."
  Pop $RunApp
  KotUI::Control /NOUNLOAD "$RunApp" "0" "246" "568" "28" "checkbox"
  ${NSD_Check} $RunApp
  !insertmacro ButtonText "Готово"
  nsDialogs::Show
FunctionEnd
Function FinishLeave
  ${NSD_GetState} $RunApp $0
  ${If} $0 == ${BST_CHECKED}
    Exec '$\"$INSTDIR\Kot.exe$\"'
  ${EndIf}
FunctionEnd
Function un.Confirm
  !insertmacro NewPage
  !insertmacro Label "" 0 6 76 76 "hero"
  !insertmacro Label "Удалить kot.?" 0 108 568 44 "heading"
  !insertmacro Label "Файлы приложения и его ярлыки будут удалены.$\r$\nВаш профиль, подписки и статистика останутся." 0 174 568 56 "body"
  !insertmacro Label "$INSTDIR" 0 250 568 42 "muted"
  !insertmacro ButtonText "Удалить"
  nsDialogs::Show
FunctionEnd
Function un.ProgressShow
  FindWindow $Dialog "#32770" "" $HWNDPARENT
  KotUI::Page /NOUNLOAD "$Dialog" "progress"
FunctionEnd
