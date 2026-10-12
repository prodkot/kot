// Native desktop installer layout. NSIS owns installation and silent mode.
// The shared signed payload and transactional extractor are unchanged.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <commctrl.h>
#include <uxtheme.h>
#include <string>
#include <cwchar>
#include <algorithm>
#include <cstdlib>

struct Stack { Stack* next; wchar_t text[1]; };
static int dpi = 96;
static HFONT regular, small, heading, bold, brandFont;
static HWND mainWindow, activePage;
static std::wstring pageRole, progressFile, failureText;
static LONG phase;
static int percentage, pulse;
static HBRUSH backgroundBrush, surfaceBrush;
static std::wstring version, fontFile;
static bool uninstalling;
static constexpr COLORREF background = RGB(21,23,24), surface = RGB(32,36,37),
    line = RGB(48,53,54), text = RGB(242,244,237), muted = RGB(150,158,159),
    accent = RGB(213,248,140), ink = RGB(38,51,28), sidebar = RGB(24,28,29);
static constexpr UINT refreshPage = WM_APP + 20, failurePage = WM_APP + 21;
static void ShowFailure(const std::wstring& detail);
static int Scale(int value) { return MulDiv(value, dpi, 96); }
static std::wstring Pop(Stack** stack) {
    if (!stack || !*stack) return {};
    auto item = *stack; std::wstring value(item->text); *stack = item->next;
    GlobalFree(item); return value;
}
static HWND PopWindow(Stack** stack) { return reinterpret_cast<HWND>(static_cast<UINT_PTR>(std::wcstoull(Pop(stack).c_str(), nullptr, 10))); }
static void Box(HDC dc, RECT bounds, COLORREF fill, COLORREF border, int radius) {
    HBRUSH brush = CreateSolidBrush(fill); HPEN pen = CreatePen(PS_SOLID, 1, border);
    auto oldBrush = SelectObject(dc, brush); auto oldPen = SelectObject(dc, pen);
    RoundRect(dc, bounds.left, bounds.top, bounds.right, bounds.bottom, Scale(radius), Scale(radius));
    SelectObject(dc, oldPen); SelectObject(dc, oldBrush); DeleteObject(pen); DeleteObject(brush);
}
static void DrawTextIn(HDC dc, const wchar_t* value, RECT bounds, HFONT font, COLORREF color, UINT format) {
    auto previous = SelectObject(dc, font); SetBkMode(dc, TRANSPARENT); SetTextColor(dc, color);
    DrawTextW(dc, value, -1, &bounds, format | DT_NOPREFIX); SelectObject(dc, previous);
}
static void Logo(HDC dc, int x, int y, int size) {
    // Draw the actual embedded application icon, including its transparent corners.
    HICON icon=static_cast<HICON>(LoadImageW(GetModuleHandleW(nullptr),MAKEINTRESOURCEW(103),IMAGE_ICON,size,size,LR_SHARED));
    if(icon) DrawIconEx(dc,x,y,icon,size,size,0,nullptr,DI_NORMAL);
}
static RECT Area(int x,int y,int width,int height) { return RECT{Scale(x),Scale(y),Scale(x+width),Scale(y+height)}; }
static void TextAt(HDC dc,const wchar_t* value,int x,int y,int width,int height,HFONT font,COLORREF color=text,UINT format=DT_LEFT|DT_WORDBREAK) {
    DrawTextIn(dc,value,Area(x,y,width,height),font,color,format);
}
static void Rule(HDC dc,int x,int y,int end,COLORREF color=line) {
    HPEN pen=CreatePen(PS_SOLID,1,color); auto old=SelectObject(dc,pen);
    MoveToEx(dc,Scale(x),Scale(y),nullptr); LineTo(dc,Scale(end),Scale(y)); SelectObject(dc,old); DeleteObject(pen);
}
static void Check(HDC dc,int x,int y,COLORREF color) {
    HPEN pen=CreatePen(PS_SOLID,Scale(2),color); auto old=SelectObject(dc,pen);
    MoveToEx(dc,Scale(x),Scale(y+5),nullptr); LineTo(dc,Scale(x+4),Scale(y+9)); LineTo(dc,Scale(x+11),Scale(y+1)); SelectObject(dc,old); DeleteObject(pen);
}
static void Brand(HDC dc,int x,int y,bool large) {
    HFONT font=large?brandFont:bold;
    TextAt(dc,L"kot",x,y,110,large?36:24,font,text,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
    auto old=SelectObject(dc,font); SIZE size{}; GetTextExtentPoint32W(dc,L"kot",3,&size); SelectObject(dc,old);
    RECT dot{Scale(x)+size.cx+Scale(3),Scale(y+(large?16:11)),Scale(x)+size.cx+Scale(large?7:6),Scale(y+(large?20:14))};
    HBRUSH brush=CreateSolidBrush(accent); FillRect(dc,&dot,brush); DeleteObject(brush);
}
static int Step() {
    if(pageRole==L"options") return 1;
    if(pageRole==L"progress") return InterlockedCompareExchange(&phase,0,0)==4?3:2;
    if(pageRole==L"finish") return 3;
    return 0;
}
static void PaintPage(HDC dc) {
    if(pageRole==L"welcome") {
        Box(dc,Area(0,178,512,124),surface,line,18);
        Logo(dc,Scale(22),Scale(201),Scale(76));
        TextAt(dc,L"kot. для Windows",122,209,366,24,bold);
        TextAt(dc,L"VPN, подписки и сервисы.\r\nСпокойный интерфейс. Простой запуск.",122,243,366,44,small,muted);
        const wchar_t* features[]{L"Ядра в комплекте",L"Новая иконка ярлыка",L"Обновление поверх"};
        int left[]{0,150,337},width[]{140,177,175};
        for(int i=0;i<3;i++) {
            Box(dc,Area(left[i],324,width[i],28),background,line,8); Check(dc,left[i]+9,333,accent);
            TextAt(dc,features[i],left[i]+28,324,width[i]-32,28,small,muted,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
        }
    } else if(pageRole==L"options") {
        Box(dc,Area(0,138,408,40),surface,line,12);
        Box(dc,Area(0,226,512,112),surface,line,16); Rule(dc,16,282,496);
        TextAt(dc,L"Меню «Пуск»",16,293,400,21,bold);
        TextAt(dc,L"Приложение и удаление всегда под рукой",16,315,450,20,small,muted);
        Check(dc,477,300,accent);
    } else if(pageRole==L"finish") {
        Box(dc,Area(0,0,54,54),surface,accent,16); Check(dc,21,20,accent);
        Rule(dc,0,229,512); Rule(dc,0,274,512); Rule(dc,0,319,512);
    } else if(pageRole==L"progress") {
        LONG current=InterlockedCompareExchange(&phase,0,0);
        if(current<0) {
            Box(dc,Area(0,0,54,54),surface,RGB(174,100,93),16);
            TextAt(dc,L"!",0,0,54,54,heading,RGB(232,156,146),DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        } else {
            std::wstring status=current==4?L"Готово":current==3?L"Создаём ярлыки":percentage>0?L"Устанавливаем файлы":L"Проверяем и подготавливаем пакет";
            TextAt(dc,status.c_str(),0,152,418,32,regular,muted,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
            std::wstring value=std::to_wstring(percentage)+L"%";
            TextAt(dc,value.c_str(),420,150,92,34,brandFont,text,DT_RIGHT|DT_VCENTER|DT_SINGLELINE);
            const wchar_t* install[]{L"Подготовка и проверка подписи",L"Приложение и ядра подключения",L"Ярлыки и завершение"};
            const wchar_t* remove[]{L"Закрываем приложение",L"Удаляем файлы приложения",L"Удаляем ярлыки"};
            for(int i=0;i<3;i++) {
                int y=229+i*49;
                bool done=current>i+1, running=current==i+1;
                if(done) Check(dc,2,y+8,accent);
                else if(running) {
                    HPEN pen=CreatePen(PS_SOLID,Scale(2),accent); auto old=SelectObject(dc,pen); auto brush=SelectObject(dc,GetStockObject(NULL_BRUSH));
                    RECT ring=Area(0,y+7,16,16); Ellipse(dc,ring.left,ring.top,ring.right,ring.bottom);
                    RECT cover=Area(pulse%2?0:9,y+7,7,8); FillRect(dc,&cover,backgroundBrush);
                    SelectObject(dc,brush); SelectObject(dc,old); DeleteObject(pen);
                } else Box(dc,Area(0,y+7,16,16),background,line,16);
                TextAt(dc,(uninstalling?remove:install)[i],28,y,480,32,regular,running||done?text:muted,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
                if(i<2) Rule(dc,0,y+42,512);
            }
        }
    }
}
static void PaintButton(HWND window, HDC dc, RECT bounds, UINT state) {
    int saved=SaveDC(dc);
    FillRect(dc,&bounds,backgroundBrush);
    wchar_t buffer[1024]{}; GetWindowTextW(window,buffer,1024);
    std::wstring caption(buffer);
    caption.erase(std::remove(caption.begin(),caption.end(),L'&'),caption.end());
    int id=GetDlgCtrlID(window);
    if(id==IDOK||id==3) {
        if(caption.compare(0,2,L"< ")==0) caption.erase(0,2);
        if(caption.size()>=2 && caption.compare(caption.size()-2,2,L" >")==0) caption.resize(caption.size()-2);
    }
    auto kind=GetWindowLongW(window,GWL_STYLE)&BS_TYPEMASK;
    bool checkbox=kind==BS_AUTOCHECKBOX||kind==BS_CHECKBOX;
    bool primary=id==IDOK;
    bool enabled=(state&ODS_DISABLED)==0;
    bool pressed=enabled&&(state&ODS_SELECTED)!=0;
    bool hover=enabled&&GetPropW(window,L"kot.hover")!=nullptr;
    if(checkbox) {
        bool toggle=GetPropW(window,L"kot.toggle")!=nullptr;
        bool checked=SendMessageW(window,BM_GETCHECK,0,0)==BST_CHECKED;
        if(toggle) {
            FillRect(dc,&bounds,surfaceBrush);
            RECT track{bounds.right-Scale(54),(bounds.bottom-Scale(22))/2,bounds.right-Scale(16),(bounds.bottom+Scale(22))/2};
            Box(dc,track,checked?(pressed?RGB(190,224,119):accent):line,checked?accent:line,22);
            int x=checked?track.right-Scale(19):track.left+Scale(3);
            Box(dc,RECT{x,track.top+Scale(3),x+Scale(16),track.top+Scale(19)},checked?ink:muted,checked?ink:muted,16);
            DrawTextIn(dc,caption.c_str(),Area(16,8,420,24),bold,enabled?text:muted,DT_LEFT|DT_SINGLELINE);
            DrawTextIn(dc,L"С новой абстрактной иконкой",Area(16,32,420,20),small,muted,DT_LEFT|DT_SINGLELINE);
        } else {
            RECT tick{Scale(1),(bounds.bottom-Scale(16))/2,Scale(17),(bounds.bottom+Scale(16))/2};
            Box(dc,tick,checked?(pressed?RGB(190,224,119):accent):(pressed?RGB(26,30,31):surface),checked?accent:line,4);
            if(checked) Check(dc,5,(bounds.bottom/Scale(1)-16)/2+3,ink);
            bounds.left=Scale(28); DrawTextIn(dc,caption.c_str(),bounds,regular,enabled?text:muted,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
        }
    } else {
        COLORREF fill=enabled&&primary ? (pressed?RGB(190,224,119):hover?RGB(224,255,163):accent) : pressed?RGB(26,30,31):hover?RGB(40,45,46):surface;
        if(id==9009) fill=pressed?RGB(95,32,32):hover?RGB(120,44,44):background;
        Box(dc,bounds,fill,primary&&enabled?accent:line,10);
        DrawTextIn(dc,caption.c_str(),bounds,bold,enabled&&primary?ink:enabled?text:muted,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
    }
    if(enabled&&(state&ODS_FOCUS)!=0 && !(SendMessageW(window,WM_QUERYUISTATE,0,0)&UISF_HIDEFOCUS)) {
        RECT focus=bounds; InflateRect(&focus,-Scale(4),-Scale(4));
        HPEN pen=CreatePen(PS_SOLID,1,primary?ink:accent); auto previous=SelectObject(dc,pen);
        auto brush=SelectObject(dc,GetStockObject(NULL_BRUSH));
        RoundRect(dc,focus.left,focus.top,focus.right,focus.bottom,Scale(6),Scale(6));
        SelectObject(dc,brush); SelectObject(dc,previous); DeleteObject(pen);
    }
    RestoreDC(dc,saved);
}
static UINT ButtonState(HWND window) {
    auto native=SendMessageW(window,BM_GETSTATE,0,0);
    return (IsWindowEnabled(window)?0u:ODS_DISABLED)|((native&BST_PUSHED)?ODS_SELECTED:0u)|((native&BST_FOCUS)?ODS_FOCUS:0u);
}
static bool DrawButton(LPARAM parameter) {
    auto item=reinterpret_cast<DRAWITEMSTRUCT*>(parameter);
    if(!item||item->CtlType!=ODT_BUTTON||!GetPropW(item->hwndItem,L"kot.owner-button")) return false;
    PaintButton(item->hwndItem,item->hDC,item->rcItem,item->itemState); return true;
}
static LRESULT CALLBACK ControlProc(HWND window, UINT message, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR) {
    wchar_t name[64]{}; GetClassNameW(window,name,64);
    bool button = _wcsicmp(name,L"Button")==0;
    bool progress = _wcsicmp(name,PROGRESS_CLASSW)==0;
    bool label = _wcsicmp(name,L"Static")==0;
    bool hero = GetPropW(window,L"kot.hero")!=nullptr;
    bool ownerButton=GetPropW(window,L"kot.owner-button")!=nullptr;
    if(ownerButton&&message==BM_SETSTYLE) {
        // Dialog focus changes switch the default button style. Keep those
        // semantics without letting Windows restore its stock renderer.
        if((w&BS_TYPEMASK)==BS_DEFPUSHBUTTON) SetPropW(window,L"kot.default-button",reinterpret_cast<HANDLE>(1));
        else if((w&BS_TYPEMASK)==BS_PUSHBUTTON) RemovePropW(window,L"kot.default-button");
        w=(w&~static_cast<WPARAM>(BS_TYPEMASK))|BS_OWNERDRAW;
    }
    if(ownerButton&&message==WM_GETDLGCODE) {
        auto code=DefSubclassProc(window,message,w,l)&~(DLGC_DEFPUSHBUTTON|DLGC_UNDEFPUSHBUTTON);
        return code|(GetPropW(window,L"kot.default-button")?DLGC_DEFPUSHBUTTON:DLGC_UNDEFPUSHBUTTON);
    }
    if(message==WM_SETTEXT && GetPropW(window,L"kot.progress-status")) l=reinterpret_cast<LPARAM>(uninstalling?L"Удаляем kot.":L"Устанавливаем kot.");
    if (message==WM_NCDESTROY) { RemoveWindowSubclass(window,ControlProc,1); return DefSubclassProc(window,message,w,l); }
    if (button && message==WM_MOUSEMOVE && !GetPropW(window,L"kot.hover")) {
        SetPropW(window,L"kot.hover",reinterpret_cast<HANDLE>(1)); TRACKMOUSEEVENT track{sizeof(track),TME_LEAVE,window,0}; TrackMouseEvent(&track); InvalidateRect(window,nullptr,FALSE);
    }
    if (message==WM_MOUSELEAVE) { RemovePropW(window,L"kot.hover"); InvalidateRect(window,nullptr,FALSE); }
    if ((button||progress||label) && message==WM_ERASEBKGND) return 1;
    if ((button||progress||label) && (message==WM_PAINT||message==WM_PRINTCLIENT||message==WM_PRINT)) {
        PAINTSTRUCT paint{}; HDC dc = message==WM_PAINT ? BeginPaint(window,&paint) : reinterpret_cast<HDC>(w);
        RECT bounds; GetClientRect(window,&bounds); FillRect(dc,&bounds,GetPropW(window,L"kot.surface")?surfaceBrush:backgroundBrush);
        if (hero) Logo(dc,0,0,std::min(bounds.right,bounds.bottom));
        else if (progress) {
            RECT track=bounds; Box(dc,track,surface,surface,6);
            track.right=MulDiv(bounds.right,percentage,100);
            if(percentage==0&&InterlockedCompareExchange(&phase,0,0)>0) {
                int width=bounds.right/6; track.left=(pulse*Scale(8))%(bounds.right+width)-width; track.right=track.left+width;
            }
            if(track.right>0) Box(dc,track,accent,accent,6);
        } else if(label) {
            wchar_t caption[2048]{}; GetWindowTextW(window,caption,2048);
            auto font=reinterpret_cast<HFONT>(SendMessageW(window,WM_GETFONT,0,0));
            DrawTextIn(dc,caption,bounds,font?font:regular,GetPropW(window,L"kot.accent")?accent:GetPropW(window,L"kot.primary")?text:muted,DT_LEFT|DT_WORDBREAK);
        } else PaintButton(window,dc,bounds,ButtonState(window));
        if(message==WM_PAINT) EndPaint(window,&paint); return 0;
    }
    auto result=DefSubclassProc(window,message,w,l);
    if(button&&(message==BM_SETCHECK||message==BM_SETSTATE||message==BM_SETSTYLE||message==WM_ENABLE||message==WM_SETTEXT||message==WM_SETFOCUS||message==WM_KILLFOCUS||message==WM_LBUTTONDOWN||message==WM_LBUTTONUP||message==WM_KEYDOWN||message==WM_KEYUP)) {
        // Checkboxes retain native check-state and accessibility semantics.
        // Their immediate state painting must also finish in our palette.
        RedrawWindow(window,nullptr,nullptr,RDW_INVALIDATE|RDW_UPDATENOW);
    }
    if(progress&&(message==PBM_SETPOS||message==PBM_STEPIT||message==PBM_SETRANGE32)) InvalidateRect(window,nullptr,FALSE);
    return result;
}
static LRESULT CALLBACK PageProc(HWND window, UINT message, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR) {
    if(message==WM_DRAWITEM&&DrawButton(l)) return TRUE;
    if(message==WM_PAINT||message==WM_PRINTCLIENT) {
        PAINTSTRUCT paint{}; HDC dc=message==WM_PAINT?BeginPaint(window,&paint):reinterpret_cast<HDC>(w);
        RECT bounds; GetClientRect(window,&bounds); FillRect(dc,&bounds,backgroundBrush); PaintPage(dc);
        if(message==WM_PAINT) EndPaint(window,&paint); return 0;
    }
    if(message==WM_CTLCOLORSTATIC||message==WM_CTLCOLORBTN||message==WM_CTLCOLOREDIT||message==WM_CTLCOLORDLG) {
        HWND child=reinterpret_cast<HWND>(l); HDC dc=reinterpret_cast<HDC>(w);
        SetTextColor(dc,GetPropW(child,L"kot.primary")?text:muted); SetBkColor(dc,message==WM_CTLCOLOREDIT?surface:background);
        return reinterpret_cast<LRESULT>(message==WM_CTLCOLOREDIT?surfaceBrush:backgroundBrush);
    }
    if(message==WM_ERASEBKGND) { RECT bounds; GetClientRect(window,&bounds); FillRect(reinterpret_cast<HDC>(w),&bounds,backgroundBrush); return 1; }
    if(message==WM_NCDESTROY) RemoveWindowSubclass(window,PageProc,1);
    return DefSubclassProc(window,message,w,l);
}
static BOOL CALLBACK SkinControl(HWND window, LPARAM) {
    // nsDialogs labels start transparent. Our skin paints their background,
    // so remove deferred sibling painting, including during window capture.
    SetWindowLongW(window,GWL_EXSTYLE,GetWindowLongW(window,GWL_EXSTYLE)&~WS_EX_TRANSPARENT);
    SendMessageW(window,WM_SETFONT,reinterpret_cast<WPARAM>(regular),TRUE);
    SetWindowTheme(window,L"",L""); SetWindowSubclass(window,ControlProc,1,0);
    wchar_t name[64]{}; GetClassNameW(window,name,64);
    if(_wcsicmp(name,L"Button")==0) {
        auto style=GetWindowLongW(window,GWL_STYLE); auto kind=style&BS_TYPEMASK;
        if(kind==BS_PUSHBUTTON||kind==BS_DEFPUSHBUTTON) {
            SetPropW(window,L"kot.owner-button",reinterpret_cast<HANDLE>(1));
            if(kind==BS_DEFPUSHBUTTON) SetPropW(window,L"kot.default-button",reinterpret_cast<HANDLE>(1));
            SetWindowLongW(window,GWL_STYLE,(style&~BS_TYPEMASK)|BS_OWNERDRAW);
        }
    }
    if(_wcsicmp(name,WC_LISTVIEWW)==0) {
        ListView_SetBkColor(window,background); ListView_SetTextBkColor(window,background); ListView_SetTextColor(window,muted);
        SendMessageW(window,WM_SETFONT,reinterpret_cast<WPARAM>(small),TRUE);
    }
    return TRUE;
}
static void Place(HWND window,int x,int y,int width,int height) { MoveWindow(window,Scale(x),Scale(y),Scale(width),Scale(height),TRUE); }
static HWND Label(HWND page,int id,const wchar_t* caption,int x,int y,int width,int height,HFONT font,COLORREF color=text) {
    HWND label=CreateWindowExW(0,L"Static",caption,WS_CHILD|WS_VISIBLE|SS_NOPREFIX,0,0,0,0,page,reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)),GetModuleHandleW(nullptr),nullptr);
    Place(label,x,y,width,height); SkinControl(label,0); SendMessageW(label,WM_SETFONT,reinterpret_cast<WPARAM>(font),TRUE);
    if(color==text) SetPropW(label,L"kot.primary",reinterpret_cast<HANDLE>(1));
    if(color==accent) SetPropW(label,L"kot.accent",reinterpret_cast<HANDLE>(1));
    return label;
}
static void RefreshProgress() {
    LONG current=InterlockedCompareExchange(&phase,0,0);
    if(current>=1&&current<3&&!progressFile.empty()) {
        HANDLE file=CreateFileW(progressFile.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(file!=INVALID_HANDLE_VALUE) {
            char data[24]{}; DWORD length=0;
            if(ReadFile(file,data,23,&length,nullptr)&&length) {
                percentage=std::max(0,std::min(atoi(data),94));
                if(percentage>0) InterlockedExchange(&phase,2);
            }
            CloseHandle(file);
        }
    } else if(current==3) percentage=96;
    else if(current==4) percentage=100;
    if(current<0) {
        ShowWindow(GetDlgItem(mainWindow,IDCANCEL),SW_HIDE);
        ShowWindow(GetDlgItem(mainWindow,3),SW_HIDE);
        EnableWindow(GetDlgItem(mainWindow,IDOK),TRUE);
        SetWindowTextW(GetDlgItem(mainWindow,IDOK),L"Закрыть");
    }
    ++pulse;
    if(activePage&&pageRole==L"progress"&&current>=0) {
        InvalidateRect(activePage,nullptr,FALSE); InvalidateRect(GetDlgItem(activePage,1004),nullptr,FALSE);
    }
}

static LRESULT CALLBACK MainProc(HWND window,UINT message,WPARAM w,LPARAM l,UINT_PTR,DWORD_PTR) {
    if(message==WM_DRAWITEM&&DrawButton(l)) return TRUE;
    if(message==failurePage) {
        auto detail=reinterpret_cast<std::wstring*>(l); ShowFailure(*detail); delete detail; return 0;
    }
    if(message==WM_COMMAND&&LOWORD(w)==IDOK&&phase<0) {
        HWND cancel=GetDlgItem(window,IDCANCEL);
        if(IsWindowEnabled(cancel)) SendMessageW(window,WM_COMMAND,IDCANCEL,reinterpret_cast<LPARAM>(cancel));
        return 0;
    }
    if(message==WM_TIMER&&w==9200) { RefreshProgress(); return 0; }
    if(message==refreshPage) {
        // Native pages become visible after their show callback returns.
        // Repaint the shared header once NSIS has completed that switch.
        RedrawWindow(window,nullptr,nullptr,RDW_INVALIDATE|RDW_ALLCHILDREN|RDW_UPDATENOW);
        return 0;
    }
    if((message==WM_COMMAND && LOWORD(w)==9009)||message==WM_CLOSE) {
        HWND cancel=GetDlgItem(window,IDCANCEL),next=GetDlgItem(window,IDOK);
        if(IsWindowEnabled(cancel)) SendMessageW(window,WM_COMMAND,IDCANCEL,reinterpret_cast<LPARAM>(cancel));
        else if(IsWindowEnabled(next)) SendMessageW(window,WM_COMMAND,IDOK,reinterpret_cast<LPARAM>(next));
        return 0;
    }
    if(message==WM_NCHITTEST) {
        POINT point{static_cast<short>(LOWORD(l)),static_cast<short>(HIWORD(l))}; ScreenToClient(window,&point);
        if(point.y<Scale(40)&&point.x<Scale(752)) return HTCAPTION;
    }
    // The paint handler fills the background and header together. Erasing
    // separately can clear the header while switching native pages.
    if(message==WM_ERASEBKGND) return 1;
    if(message==WM_PAINT || message==WM_PRINTCLIENT) {
        PAINTSTRUCT paint{}; HDC dc=message==WM_PAINT?BeginPaint(window,&paint):reinterpret_cast<HDC>(w);
        RECT bounds; GetClientRect(window,&bounds); FillRect(dc,&bounds,backgroundBrush);
        RECT side=Area(0,40,218,560); HBRUSH sideBrush=CreateSolidBrush(sidebar); FillRect(dc,&side,sideBrush); DeleteObject(sideBrush);
        Rule(dc,0,40,800); Rule(dc,218,522,800);
        HPEN pen=CreatePen(PS_SOLID,1,line); auto previous=SelectObject(dc,pen);
        MoveToEx(dc,Scale(218),Scale(40),nullptr); LineTo(dc,Scale(218),Scale(600)); SelectObject(dc,previous); DeleteObject(pen);
        Logo(dc,Scale(22),Scale(13),Scale(14)); Brand(dc,47,8,false);
        std::wstring caption=(uninstalling?L"Удаление ":L"Установка ")+version;
        TextAt(dc,caption.c_str(),87,9,350,22,small,muted,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
        Logo(dc,Scale(23),Scale(69),Scale(36)); Brand(dc,69,69,true);
        TextAt(dc,uninstalling?L"Удаление приложения":L"Установка для Windows",23,134,180,22,small,muted);
        int step=Step(); int count=uninstalling?3:4;
        if(uninstalling) step=pageRole==L"progress"?(phase==4?2:1):0;
        const wchar_t* installSteps[]{L"Знакомство",L"Параметры",L"Установка",L"Готово"};
        const wchar_t* removeSteps[]{L"Подтверждение",L"Удаление",L"Готово"};
        pen=CreatePen(PS_SOLID,1,line); previous=SelectObject(dc,pen);
        MoveToEx(dc,Scale(37),Scale(188),nullptr); LineTo(dc,Scale(37),Scale(188+(count-1)*52)); SelectObject(dc,previous); DeleteObject(pen);
        for(int i=0;i<count;i++) {
            int y=174+i*52;
            Box(dc,Area(23,y,28,28),i==step?accent:sidebar,i<=step?accent:line,28);
            if(i<step) Check(dc,32,y+9,accent);
            else { std::wstring number=std::to_wstring(i+1); TextAt(dc,number.c_str(),23,y,28,28,small,i==step?ink:muted,DT_CENTER|DT_VCENTER|DT_SINGLELINE); }
            TextAt(dc,(uninstalling?removeSteps:installSteps)[i],63,y,144,28,small,i==step?text:muted,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
        }
        TextAt(dc,uninstalling?L"ВАШ ПРОФИЛЬ ОСТАНЕТСЯ":L"БЕЗ ЛИШНИХ ШАГОВ",23,430,190,20,small,muted);
        TextAt(dc,uninstalling?L"Подписки, настройки и статистика\r\nсохраняются после удаления.":L"Приложение, ядра подключения\r\nи всё необходимое в одном пакете.",23,456,180,42,small,muted);
        TextAt(dc,L"Windows 10 / 11 · x64",23,507,180,20,small,muted);
        TextAt(dc,phase<0?L"Подробности ошибки выше.":pageRole==L"finish"?L"Всё готово к первому запуску.":L"Настройки и подписки\r\nсохранятся при обновлении.",253,543,205,40,small,muted);
        if(message==WM_PAINT) EndPaint(window,&paint); return 0;
    }
    if(message==WM_CTLCOLORSTATIC||message==WM_CTLCOLORBTN||message==WM_CTLCOLOREDIT||message==WM_CTLCOLORDLG) return PageProc(window,message,w,l,1,0);
    if(message==WM_NCDESTROY) RemoveWindowSubclass(window,MainProc,1);
    return DefSubclassProc(window,message,w,l);
}
extern "C" __declspec(dllexport) void Init(HWND parent,int,wchar_t*,Stack** stack,void*) {
    fontFile=Pop(stack); version=Pop(stack); uninstalling=Pop(stack)==L"uninstall";
    dpi=static_cast<int>(GetDpiForWindow(parent)); if(!dpi) dpi=96;
    AddFontResourceExW(fontFile.c_str(),FR_PRIVATE,nullptr);
    auto Font=[](int size,int weight) { return CreateFontW(-Scale(size),0,0,0,weight,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Manrope"); };
    regular=Font(13,500); small=Font(11,500); heading=Font(30,750); bold=Font(13,650); brandFont=Font(25,750);
    mainWindow=parent;
    backgroundBrush=CreateSolidBrush(background); surfaceBrush=CreateSolidBrush(surface);
    SetWindowSubclass(parent,MainProc,1,0);
    SetWindowLongW(parent,GWL_STYLE,(GetWindowLongW(parent,GWL_STYLE)&~(WS_CAPTION|WS_THICKFRAME|WS_BORDER|DS_MODALFRAME))|WS_POPUP|WS_CLIPCHILDREN);
    SetWindowLongW(parent,GWL_EXSTYLE,(GetWindowLongW(parent,GWL_EXSTYLE)&~(WS_EX_DLGMODALFRAME|WS_EX_CLIENTEDGE))|WS_EX_APPWINDOW);
    MONITORINFO monitor{sizeof(monitor)}; GetMonitorInfoW(MonitorFromWindow(parent,MONITOR_DEFAULTTONEAREST),&monitor);
    int width=Scale(800),height=Scale(600);
    SetWindowPos(parent,nullptr,monitor.rcWork.left+(monitor.rcWork.right-monitor.rcWork.left-width)/2,monitor.rcWork.top+(monitor.rcWork.bottom-monitor.rcWork.top-height)/2,width,height,SWP_NOZORDER|SWP_FRAMECHANGED);
    SetWindowRgn(parent,CreateRoundRectRgn(0,0,width+1,height+1,Scale(16),Scale(16)),TRUE);
    HWND child=GetWindow(parent,GW_CHILD);
    while(child) { int id=GetDlgCtrlID(child); if(id!=IDOK&&id!=IDCANCEL&&id!=3&&id!=1018) ShowWindow(child,SW_HIDE); child=GetWindow(child,GW_HWNDNEXT); }
    Place(GetDlgItem(parent,1018),253,72,512,430);
    Place(GetDlgItem(parent,IDOK),625,541,140,39); Place(GetDlgItem(parent,3),552,541,65,39); Place(GetDlgItem(parent,IDCANCEL),23,548,90,32);
    auto close=CreateWindowExW(0,L"Button",L"×",WS_CHILD|WS_VISIBLE|WS_TABSTOP|BS_PUSHBUTTON,Scale(757),Scale(6),Scale(30),Scale(28),parent,reinterpret_cast<HMENU>(9009),GetModuleHandleW(nullptr),nullptr);
    SkinControl(close,0); SkinControl(GetDlgItem(parent,IDOK),0); SkinControl(GetDlgItem(parent,IDCANCEL),0); SkinControl(GetDlgItem(parent,3),0);
    SetWindowTextW(GetDlgItem(parent,IDCANCEL),L"Отмена"); SetWindowTextW(GetDlgItem(parent,3),L"Назад");
    SetTimer(parent,9200,140,nullptr);
    InvalidateRect(parent,nullptr,TRUE);
}
extern "C" __declspec(dllexport) void Page(HWND,int,wchar_t*,Stack** stack,void*) {
    HWND page=PopWindow(stack); pageRole=Pop(stack); activePage=page;
    SetWindowLongW(page,GWL_STYLE,GetWindowLongW(page,GWL_STYLE)|WS_CLIPCHILDREN);
    Place(page,253,72,512,430); SetWindowSubclass(page,PageProc,1,0); EnumChildWindows(page,SkinControl,0);
    if(pageRole==L"options") {
        ShowWindow(GetDlgItem(page,1031),SW_HIDE); ShowWindow(GetDlgItem(page,1020),SW_HIDE);
        HWND title=GetDlgItem(page,1006); Place(title,0,26,512,44); SetWindowTextW(title,L"Пара штрихов.");
        SendMessageW(title,WM_SETFONT,reinterpret_cast<WPARAM>(heading),TRUE); SetPropW(title,L"kot.primary",reinterpret_cast<HANDLE>(1));
        Label(page,9060,L"УСТАНОВКА ПОД ВАС",0,2,512,22,small,muted);
        Label(page,9061,L"Выберите папку и то, как kot будет появляться в системе.",0,81,512,30,small,muted);
        Label(page,9062,L"Папка установки",0,112,512,22,small,muted);
        HWND edit=GetDlgItem(page,1019); Place(edit,12,148,384,23); SetPropW(edit,L"kot.primary",reinterpret_cast<HANDLE>(1));
        SetWindowLongW(edit,GWL_STYLE,GetWindowLongW(edit,GWL_STYLE)&~WS_BORDER);
        SetWindowLongW(edit,GWL_EXSTYLE,GetWindowLongW(edit,GWL_EXSTYLE)&~WS_EX_CLIENTEDGE);
        SetWindowPos(edit,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_FRAMECHANGED);
        Place(GetDlgItem(page,1001),420,138,92,40); SetWindowTextW(GetDlgItem(page,1001),L"Обзор");
        HWND checkbox=GetDlgItem(page,1008); Place(checkbox,2,227,508,54); SetWindowTextW(checkbox,L"Ярлык на рабочем столе"); ShowWindow(checkbox,SW_SHOW);
        SetPropW(checkbox,L"kot.toggle",reinterpret_cast<HANDLE>(1));
        SetWindowLongW(checkbox,GWL_STYLE,GetWindowLongW(checkbox,GWL_STYLE)|BS_PUSHLIKE);
        Place(GetDlgItem(page,1023),0,184,512,20); ShowWindow(GetDlgItem(page,1024),SW_HIDE);
        Label(page,9064,L"Установка для всех пользователей этого компьютера.\r\nАвтозапуск можно настроить в приложении.",0,359,512,52,small,muted);
    }
    if(pageRole==L"progress") {
        InterlockedExchange(&phase,1); percentage=0; progressFile=Pop(stack);
        ShowWindow(GetDlgItem(page,1031),SW_HIDE); ShowWindow(GetDlgItem(page,1016),SW_HIDE); ShowWindow(GetDlgItem(page,1027),SW_HIDE);
        HWND status=GetDlgItem(page,1006); Place(status,0,26,512,44);
        SetPropW(status,L"kot.primary",reinterpret_cast<HANDLE>(1)); SetPropW(status,L"kot.progress-status",reinterpret_cast<HANDLE>(1));
        SetWindowTextW(status,L""); SendMessageW(status,WM_SETFONT,reinterpret_cast<WPARAM>(heading),TRUE);
        Place(GetDlgItem(page,1004),0,191,512,6);
        Label(page,9060,L"ПОЧТИ НА СВЯЗИ",0,2,512,22,small,muted);
        Label(page,9061,uninstalling?L"Удалим файлы приложения и ярлыки. Ваш профиль останется.":L"Проверим пакет, установим приложение\r\nи подготовим ярлыки.",0,81,512,48,small,muted);
        Label(page,9063,L"Ваши сохранённые подключения останутся на месте.",0,392,512,30,small,muted);
    }
    InvalidateRect(page,nullptr,TRUE); PostMessageW(mainWindow,refreshPage,0,0);
}
extern "C" __declspec(dllexport) void Stage(HWND,int,wchar_t*,Stack** stack,void*) {
    int current=_wtoi(Pop(stack).c_str()); InterlockedExchange(&phase,current);
    PostMessageW(mainWindow,refreshPage,0,0);
}
extern "C" __declspec(dllexport) void Failure(HWND,int,wchar_t*,Stack** stack,void*) {
    // Sections run on the NSIS worker. Construct controls only on the UI thread.
    auto detail=new std::wstring(Pop(stack));
    if(!PostMessageW(mainWindow,failurePage,0,reinterpret_cast<LPARAM>(detail))) delete detail;
}
static void ShowFailure(const std::wstring& detail) {
    failureText=detail; InterlockedExchange(&phase,-1);
    HWND child=GetWindow(activePage,GW_CHILD); while(child) { ShowWindow(child,SW_HIDE); child=GetWindow(child,GW_HWNDNEXT); }
    Label(activePage,9090,uninstalling?L"Удаление прервалось.":L"Установка прервалась.",0,78,512,80,heading);
    Label(activePage,9091,L"Закройте приложения, проверьте выбранную папку\r\nи запустите установщик повторно.",0,164,512,48,regular,muted);
    HWND details=CreateWindowExW(WS_EX_CLIENTEDGE,L"Edit",failureText.c_str(),WS_CHILD|WS_VISIBLE|WS_TABSTOP|ES_MULTILINE|ES_READONLY|ES_AUTOVSCROLL|WS_VSCROLL,0,0,0,0,activePage,reinterpret_cast<HMENU>(9092),GetModuleHandleW(nullptr),nullptr);
    Place(details,0,237,512,123); SkinControl(details,0); SetPropW(details,L"kot.primary",reinterpret_cast<HANDLE>(1));
    Label(activePage,9093,L"Профиль и подписки остаются в вашей папке пользователя.",0,389,512,34,small,muted);
    ShowWindow(GetDlgItem(mainWindow,3),SW_HIDE); SetWindowTextW(GetDlgItem(mainWindow,IDOK),L"Закрыть");
    InvalidateRect(activePage,nullptr,TRUE); PostMessageW(mainWindow,refreshPage,0,0);
}
extern "C" __declspec(dllexport) void Control(HWND,int,wchar_t*,Stack** stack,void*) {
    HWND window=PopWindow(stack);
    int x=_wtoi(Pop(stack).c_str()),y=_wtoi(Pop(stack).c_str()),width=_wtoi(Pop(stack).c_str()),height=_wtoi(Pop(stack).c_str());
    std::wstring role=Pop(stack); Place(window,x,y,width,height); SkinControl(window,0);
    if(role==L"heading") { SendMessageW(window,WM_SETFONT,reinterpret_cast<WPARAM>(heading),TRUE); SetPropW(window,L"kot.primary",reinterpret_cast<HANDLE>(1)); }
    if(role==L"small"||role==L"overline") SendMessageW(window,WM_SETFONT,reinterpret_cast<WPARAM>(small),TRUE);
    if(role==L"accent") { SendMessageW(window,WM_SETFONT,reinterpret_cast<WPARAM>(small),TRUE); SetPropW(window,L"kot.accent",reinterpret_cast<HANDLE>(1)); }
    if(role==L"surface") SetPropW(window,L"kot.surface",reinterpret_cast<HANDLE>(1));
    if(role==L"body") SetPropW(window,L"kot.primary",reinterpret_cast<HANDLE>(1));
    if(role==L"hero") SetPropW(window,L"kot.hero",reinterpret_cast<HANDLE>(1));
    InvalidateRect(window,nullptr,TRUE);
}
BOOL WINAPI DllMain(HINSTANCE,DWORD reason,LPVOID) {
    if(reason==DLL_PROCESS_DETACH) {
        DeleteObject(regular); DeleteObject(small); DeleteObject(heading); DeleteObject(bold); DeleteObject(brandFont); DeleteObject(backgroundBrush); DeleteObject(surfaceBrush);
        if(!fontFile.empty()) RemoveFontResourceExW(fontFile.c_str(),FR_PRIVATE,nullptr);
    }
    return TRUE;
}
