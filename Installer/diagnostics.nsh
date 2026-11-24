; Explicit opt-in diagnostics. Only stage, elapsed tick and result, never settings.
; Preserve registers AND the NSIS error flag so observation cannot change control flow.
Var SetupLog
!macro SetupTrace message
  Push $7
  Push $8
  Push $9
  StrCpy $9 0
  IfErrors 0 +2
  StrCpy $9 1
  ${If} $SetupLog != ""
    ClearErrors
    FileOpen $7 "$SetupLog" a
    ${IfNot} ${Errors}
      System::Call 'kernel32::GetTickCount() i.r8'
      FileWriteUTF16LE $7 "$8 ${message}$\r$\n"
      FileClose $7
    ${EndIf}
  ${EndIf}
  ClearErrors
  ${If} $9 == 1
    SetErrors
  ${EndIf}
  Pop $9
  Pop $8
  Pop $7
!macroend
