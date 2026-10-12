// Small native NSIS skin. Installation, rollback and silent mode stay in NSIS.
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
static HFONT regular, small, heading, bold;
static HBRUSH backgroundBrush, surfaceBrush;
static std::wstring version, fontFile;
static bool uninstalling;
static constexpr COLORREF background = RGB(21,23,24), surface = RGB(32,36,37),
    line = RGB(48,53,54), text = RGB(242,244,237), muted = RGB(150,158,159),
    accent = RGB(213,248,140), ink = RGB(38,51,28);
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
static LRESULT CALLBACK ControlProc(HWND window, UINT message, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR) {
    wchar_t name[64]{}; GetClassNameW(window,name,64);
    bool button = _wcsicmp(name,L"Button")==0;
    bool progress = _wcsicmp(name,PROGRESS_CLASSW)==0;
    bool label = _wcsicmp(name,L"Static")==0;
    bool hero = GetPropW(window,L"kot.hero")!=nullptr;
    if(message==WM_SETTEXT && GetPropW(window,L"kot.progress-status")) l=reinterpret_cast<LPARAM>(uninstalling?L"Удаляем kot.":L"Устанавливаем kot.");
    if (message==WM_NCDESTROY) { RemoveWindowSubclass(window,ControlProc,1); return DefSubclassProc(window,message,w,l); }
    if (button && message==WM_MOUSEMOVE && !GetPropW(window,L"kot.hover")) {
        SetPropW(window,L"kot.hover",reinterpret_cast<HANDLE>(1)); TRACKMOUSEEVENT track{sizeof(track),TME_LEAVE,window,0}; TrackMouseEvent(&track); InvalidateRect(window,nullptr,FALSE);
    }
    if (message==WM_MOUSELEAVE) { RemovePropW(window,L"kot.hover"); InvalidateRect(window,nullptr,FALSE); }
    if ((button||progress||label) && message==WM_ERASEBKGND) return 1;
    if ((button||progress||label) && (message==WM_PAINT||message==WM_PRINTCLIENT)) {
        PAINTSTRUCT paint{}; HDC dc = message==WM_PAINT ? BeginPaint(window,&paint) : reinterpret_cast<HDC>(w);
        RECT bounds; GetClientRect(window,&bounds); FillRect(dc,&bounds,backgroundBrush);
        if (hero) Logo(dc,0,0,std::min(bounds.right,bounds.bottom));
        else if (progress) {
            RECT track=bounds; Box(dc,track,surface,surface,6);
            PBRANGE range{}; SendMessageW(window,PBM_GETRANGE,FALSE,reinterpret_cast<LPARAM>(&range));
            int position=static_cast<int>(SendMessageW(window,PBM_GETPOS,0,0));
            int span=range.iHigh-range.iLow; track.right=span>0 ? MulDiv(bounds.right,position-range.iLow,span) : 0;
            if(track.right>0) Box(dc,track,accent,accent,6);
        } else if(label) {
            wchar_t caption[2048]{}; GetWindowTextW(window,caption,2048);
            auto font=reinterpret_cast<HFONT>(SendMessageW(window,WM_GETFONT,0,0));
            DrawTextIn(dc,caption,bounds,font?font:regular,GetPropW(window,L"kot.primary")?text:muted,DT_LEFT|DT_WORDBREAK);
        } else {
            wchar_t buffer[1024]{}; GetWindowTextW(window,buffer,1024);
            std::wstring caption(buffer);
            caption.erase(std::remove(caption.begin(),caption.end(),L'&'),caption.end());
            if(GetDlgCtrlID(window)==IDOK||GetDlgCtrlID(window)==3) {
                if(caption.compare(0,2,L"< ")==0) caption.erase(0,2);
                if(caption.size()>=2 && caption.compare(caption.size()-2,2,L" >")==0) caption.resize(caption.size()-2);
            }
            auto kind=GetWindowLongW(window,GWL_STYLE)&BS_TYPEMASK;
            bool checkbox=kind==BS_AUTOCHECKBOX||kind==BS_CHECKBOX;
            bool primary=GetDlgCtrlID(window)==IDOK;
            bool enabled=IsWindowEnabled(window)!=FALSE;
            bool hover=GetPropW(window,L"kot.hover")!=nullptr;
            if(checkbox) {
                RECT tick{Scale(1),(bounds.bottom-Scale(16))/2,Scale(17),(bounds.bottom+Scale(16))/2};
                bool checked=SendMessageW(window,BM_GETCHECK,0,0)==BST_CHECKED;
                Box(dc,tick,checked?accent:surface,checked?accent:line,4);
                if(checked) {
                    HPEN pen=CreatePen(PS_SOLID,Scale(2),ink); auto previous=SelectObject(dc,pen);
                    MoveToEx(dc,tick.left+Scale(4),tick.top+Scale(8),nullptr); LineTo(dc,tick.left+Scale(7),tick.top+Scale(11)); LineTo(dc,tick.left+Scale(12),tick.top+Scale(5));
                    SelectObject(dc,previous); DeleteObject(pen);
                }
                bounds.left=Scale(28); DrawTextIn(dc,caption.c_str(),bounds,regular,enabled?text:muted,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
            } else {
                COLORREF fill=enabled&&primary ? accent : hover ? RGB(40,45,46) : surface;
                if(GetDlgCtrlID(window)==9009) fill=hover?RGB(120,44,44):background;
                Box(dc,bounds,fill,primary&&enabled?accent:line,10);
                DrawTextIn(dc,caption.c_str(),bounds,bold,enabled&&primary?ink:enabled?text:muted,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
            }
            if(GetFocus()==window) { RECT focus=bounds; InflateRect(&focus,-Scale(4),-Scale(4)); DrawFocusRect(dc,&focus); }
        }
        if(message==WM_PAINT) EndPaint(window,&paint); return 0;
    }
    auto result=DefSubclassProc(window,message,w,l);
    if(button&&(message==BM_SETCHECK||message==WM_ENABLE||message==WM_SETTEXT||message==WM_SETFOCUS||message==WM_KILLFOCUS||message==WM_LBUTTONDOWN||message==WM_LBUTTONUP)) InvalidateRect(window,nullptr,FALSE);
    if(progress&&(message==PBM_SETPOS||message==PBM_STEPIT||message==PBM_SETRANGE32)) InvalidateRect(window,nullptr,FALSE);
    return result;
}
static LRESULT CALLBACK PageProc(HWND window, UINT message, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR) {
    if(message==WM_COMMAND && LOWORD(w)==1027) ShowWindow(GetDlgItem(window,9063),SW_HIDE);
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
    SendMessageW(window,WM_SETFONT,reinterpret_cast<WPARAM>(regular),TRUE);
    SetWindowTheme(window,L"",L""); SetWindowSubclass(window,ControlProc,1,0);
    wchar_t name[64]{}; GetClassNameW(window,name,64);
    if(_wcsicmp(name,WC_LISTVIEWW)==0) {
        ListView_SetBkColor(window,background); ListView_SetTextBkColor(window,background); ListView_SetTextColor(window,muted);
        SendMessageW(window,WM_SETFONT,reinterpret_cast<WPARAM>(small),TRUE);
    }
    return TRUE;
}
static void Place(HWND window,int x,int y,int width,int height) { MoveWindow(window,Scale(x),Scale(y),Scale(width),Scale(height),TRUE); }
static LRESULT CALLBACK MainProc(HWND window,UINT message,WPARAM w,LPARAM l,UINT_PTR,DWORD_PTR) {
    if(message==WM_COMMAND && LOWORD(w)==9009) { SendMessageW(window,WM_CLOSE,0,0); return 0; }
    if(message==WM_NCHITTEST) {
        POINT point{static_cast<short>(LOWORD(l)),static_cast<short>(HIWORD(l))}; ScreenToClient(window,&point);
        if(point.y<Scale(64)&&point.x<Scale(584)) return HTCAPTION;
    }
    if(message==WM_ERASEBKGND) { RECT bounds; GetClientRect(window,&bounds); FillRect(reinterpret_cast<HDC>(w),&bounds,backgroundBrush); return 1; }
    if(message==WM_PAINT || message==WM_PRINTCLIENT) {
        PAINTSTRUCT paint{}; HDC dc=message==WM_PAINT?BeginPaint(window,&paint):reinterpret_cast<HDC>(w);
        RECT bounds; GetClientRect(window,&bounds); FillRect(dc,&bounds,backgroundBrush);
        HPEN pen=CreatePen(PS_SOLID,1,line); auto previous=SelectObject(dc,pen);
        MoveToEx(dc,0,Scale(64),nullptr); LineTo(dc,bounds.right,Scale(64)); MoveToEx(dc,Scale(36),Scale(408),nullptr); LineTo(dc,bounds.right-Scale(36),Scale(408));
        SelectObject(dc,previous); DeleteObject(pen); Logo(dc,Scale(36),Scale(20),Scale(26));
        RECT brand{Scale(74),Scale(18),Scale(210),Scale(48)}; DrawTextIn(dc,L"kot.",brand,bold,text,DT_LEFT|DT_VCENTER|DT_SINGLELINE);
        std::wstring caption=(uninstalling?L"Удаление  ":L"Установка  ")+version;
        RECT label{Scale(350),Scale(18),Scale(570),Scale(48)}; DrawTextIn(dc,caption.c_str(),label,small,muted,DT_RIGHT|DT_VCENTER|DT_SINGLELINE);
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
    regular=Font(13,500); small=Font(11,500); heading=Font(28,700); bold=Font(13,650);
    backgroundBrush=CreateSolidBrush(background); surfaceBrush=CreateSolidBrush(surface);
    SetWindowSubclass(parent,MainProc,1,0);
    SetWindowLongW(parent,GWL_STYLE,(GetWindowLongW(parent,GWL_STYLE)&~(WS_CAPTION|WS_THICKFRAME|WS_BORDER|DS_MODALFRAME))|WS_POPUP|WS_CLIPCHILDREN);
    SetWindowLongW(parent,GWL_EXSTYLE,(GetWindowLongW(parent,GWL_EXSTYLE)&~(WS_EX_DLGMODALFRAME|WS_EX_CLIENTEDGE))|WS_EX_APPWINDOW);
    MONITORINFO monitor{sizeof(monitor)}; GetMonitorInfoW(MonitorFromWindow(parent,MONITOR_DEFAULTTONEAREST),&monitor);
    int width=Scale(640),height=Scale(480);
    SetWindowPos(parent,nullptr,monitor.rcWork.left+(monitor.rcWork.right-monitor.rcWork.left-width)/2,monitor.rcWork.top+(monitor.rcWork.bottom-monitor.rcWork.top-height)/2,width,height,SWP_NOZORDER|SWP_FRAMECHANGED);
    SetWindowRgn(parent,CreateRoundRectRgn(0,0,width+1,height+1,Scale(16),Scale(16)),TRUE);
    HWND child=GetWindow(parent,GW_CHILD);
    while(child) { int id=GetDlgCtrlID(child); if(id!=IDOK&&id!=IDCANCEL&&id!=3&&id!=1018) ShowWindow(child,SW_HIDE); child=GetWindow(child,GW_HWNDNEXT); }
    Place(GetDlgItem(parent,1018),36,82,568,312);
    Place(GetDlgItem(parent,IDOK),460,428,144,36); Place(GetDlgItem(parent,3),350,428,96,36); Place(GetDlgItem(parent,IDCANCEL),36,428,96,36);
    auto close=CreateWindowExW(0,L"Button",L"×",WS_CHILD|WS_VISIBLE|WS_TABSTOP|BS_PUSHBUTTON,Scale(592),Scale(18),Scale(28),Scale(28),parent,reinterpret_cast<HMENU>(9009),GetModuleHandleW(nullptr),nullptr);
    SkinControl(close,0); SkinControl(GetDlgItem(parent,IDOK),0); SkinControl(GetDlgItem(parent,IDCANCEL),0); SkinControl(GetDlgItem(parent,3),0);
    SetWindowTextW(GetDlgItem(parent,IDCANCEL),L"Отмена"); SetWindowTextW(GetDlgItem(parent,3),L"Назад");
    InvalidateRect(parent,nullptr,TRUE);
}
extern "C" __declspec(dllexport) void Page(HWND,int,wchar_t*,Stack** stack,void*) {
    HWND page=PopWindow(stack); std::wstring role=Pop(stack);
    Place(page,36,82,568,312); SetWindowSubclass(page,PageProc,1,0); EnumChildWindows(page,SkinControl,0);
    if(role==L"options") {
        ShowWindow(GetDlgItem(page,1031),SW_HIDE); ShowWindow(GetDlgItem(page,1020),SW_HIDE);
        HWND title=GetDlgItem(page,1006); Place(title,0,12,568,44); SetWindowTextW(title,L"Настройка установки");
        SendMessageW(title,WM_SETFONT,reinterpret_cast<WPARAM>(heading),TRUE); SetPropW(title,L"kot.primary",reinterpret_cast<HANDLE>(1));
        auto Label=[page](int id,const wchar_t* caption,int y,int height) {
            HWND label=CreateWindowExW(0,L"Static",caption,WS_CHILD|WS_VISIBLE|SS_NOPREFIX,0,0,0,0,page,reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)),GetModuleHandleW(nullptr),nullptr);
            Place(label,0,y,568,height); SkinControl(label,0); return label;
        };
        Label(9060,L"При обновлении ваши серверы, настройки и статистика сохранятся.",64,38);
        HWND folder=Label(9061,L"Папка приложения",108,22); SetPropW(folder,L"kot.primary",reinterpret_cast<HANDLE>(1));
        Place(GetDlgItem(page,1019),0,140,444,30); SetPropW(GetDlgItem(page,1019),L"kot.primary",reinterpret_cast<HANDLE>(1));
        Place(GetDlgItem(page,1001),456,140,112,30); SetWindowTextW(GetDlgItem(page,1001),L"Обзор");
        HWND checkbox=GetDlgItem(page,1008); Place(checkbox,0,194,568,28); SetWindowTextW(checkbox,L"Ярлык на рабочем столе"); ShowWindow(checkbox,SW_SHOW);
        Place(GetDlgItem(page,1023),0,246,568,20); Place(GetDlgItem(page,1024),0,270,568,20);
    }
    if(role==L"progress") {
        ShowWindow(GetDlgItem(page,1031),SW_HIDE);
        HWND status=GetDlgItem(page,1006); Place(status,0,12,568,44);
        SetPropW(status,L"kot.primary",reinterpret_cast<HANDLE>(1)); SetPropW(status,L"kot.progress-status",reinterpret_cast<HANDLE>(1));
        SetWindowTextW(status,L""); SendMessageW(status,WM_SETFONT,reinterpret_cast<WPARAM>(heading),TRUE);
        HWND progress=GetDlgItem(page,1004); Place(progress,0,72,568,6);
        HWND list=GetDlgItem(page,1016); Place(list,0,160,568,138); ListView_SetColumnWidth(list,0,Scale(540));
        HWND controls[]{progress,list};
        for(HWND control:controls) {
            SetWindowLongW(control,GWL_STYLE,GetWindowLongW(control,GWL_STYLE)&~WS_BORDER);
            SetWindowLongW(control,GWL_EXSTYLE,GetWindowLongW(control,GWL_EXSTYLE)&~(WS_EX_CLIENTEDGE|WS_EX_STATICEDGE));
            SetWindowPos(control,nullptr,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_FRAMECHANGED);
        }
        HWND details=GetDlgItem(page,1027); Place(details,0,112,116,32); SetWindowTextW(details,L"Подробнее");
        HWND hint=CreateWindowExW(0,L"Static",uninstalling?L"Ваш профиль, подписки и статистика сохранятся.":L"Приложение скоро будет готово к работе.\r\nВаши сохранённые подключения останутся на месте.",WS_CHILD|WS_VISIBLE|SS_NOPREFIX,0,0,0,0,page,reinterpret_cast<HMENU>(9063),GetModuleHandleW(nullptr),nullptr);
        Place(hint,0,160,568,58); SkinControl(hint,0);
    }
    InvalidateRect(page,nullptr,TRUE);
}
extern "C" __declspec(dllexport) void Control(HWND,int,wchar_t*,Stack** stack,void*) {
    HWND window=PopWindow(stack);
    int x=_wtoi(Pop(stack).c_str()),y=_wtoi(Pop(stack).c_str()),width=_wtoi(Pop(stack).c_str()),height=_wtoi(Pop(stack).c_str());
    std::wstring role=Pop(stack); Place(window,x,y,width,height); SkinControl(window,0);
    if(role==L"heading") { SendMessageW(window,WM_SETFONT,reinterpret_cast<WPARAM>(heading),TRUE); SetPropW(window,L"kot.primary",reinterpret_cast<HANDLE>(1)); }
    if(role==L"body") SetPropW(window,L"kot.primary",reinterpret_cast<HANDLE>(1));
    if(role==L"hero") SetPropW(window,L"kot.hero",reinterpret_cast<HANDLE>(1));
    InvalidateRect(window,nullptr,TRUE);
}
BOOL WINAPI DllMain(HINSTANCE,DWORD reason,LPVOID) {
    if(reason==DLL_PROCESS_DETACH) {
        DeleteObject(regular); DeleteObject(small); DeleteObject(heading); DeleteObject(bold); DeleteObject(backgroundBrush); DeleteObject(surfaceBrush);
        if(!fontFile.empty()) RemoveFontResourceExW(fontFile.c_str(),FR_PRIVATE,nullptr);
    }
    return TRUE;
}
