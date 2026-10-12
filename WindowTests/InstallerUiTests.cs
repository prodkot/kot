using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Kot.Windows;
namespace Kot.WindowTests;

static class InstallerUiTests
{
    delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", CharSet=CharSet.Unicode)] static extern IntPtr SendTextMessage(IntPtr window, int message, IntPtr size, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr parent, int id);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, int flags);
    static string Text(IntPtr window)
    {
        // Edit text belongs to the installer process, so GetWindowText is not
        // sufficient. WM_GETTEXT is marshalled by Windows across processes.
        var text=new StringBuilder(2048);
        SendTextMessage(window,0xD,(IntPtr)text.Capacity,text,2,2000,out _);
        return text.ToString();
    }
    static List<IntPtr> Children(IntPtr window) { List<IntPtr> result=[]; EnumChildWindows(window,(child,_)=>{result.Add(child);return true;},IntPtr.Zero); return result; }
    static IntPtr ByText(IntPtr window, string caption) => Children(window).FirstOrDefault(child=>IsWindowVisible(child)&&Text(child)==caption);
    static IntPtr ById(IntPtr window, int id) => Children(window).FirstOrDefault(child=>IsWindowVisible(child)&&GetDlgCtrlID(child)==id);
    static void Wait(Func<bool> ready,string stage,int seconds=30)
    {
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed.TotalSeconds<seconds) { if(ready()) return; Application.DoEvents(); Thread.Sleep(80); }
        throw new Exception("Installer UI timeout: "+stage);
    }
    static IntPtr Window(Process process)
    {
        IntPtr result=IntPtr.Zero;
        Wait(()=>{EnumWindows((window,_)=>{GetWindowThreadProcessId(window,out uint pid);if(pid==process.Id&&IsWindowVisible(window)) result=window;return true;},IntPtr.Zero);return result!=IntPtr.Zero;},"window creation");
        return result;
    }
    static void Click(IntPtr button)
    {
        if(button==IntPtr.Zero||!IsWindowEnabled(button)) throw new Exception("Installer button missing or disabled");
        // BM_CLICK may be ignored on an inactive dialog after the install
        // worker switches pages. Deliver the same BN_CLICKED notification
        // a native button sends, asynchronously so progress can be captured.
        PostMessage(GetParent(button),0x111,(IntPtr)GetDlgCtrlID(button),button);
    }
    static void Capture(IntPtr window,string folder,string name,Action<bool,string> check)
    {
        SetForegroundWindow(window);
        Thread.Sleep(250); GetWindowRect(window,out var bounds); GetClientRect(window,out var client);
        float scale=GetDpiForWindow(window)/96f;
        check(Math.Abs(client.Right-640*scale)<=2&&Math.Abs(client.Bottom-480*scale)<=2,"custom Setup has a 640 by 480 client area at native DPI: "+name);
        check((GetWindowLong(window,-16)&0xC00000)==0,"custom Setup uses its own borderless title: "+name);
        using var bitmap=new Bitmap(bounds.Right-bounds.Left,bounds.Bottom-bounds.Top);
        using(var graphics=Graphics.FromImage(bitmap)) { var dc=graphics.GetHdc(); try { check(PrintWindow(window,dc,0),"capture real installer window: "+name); } finally {graphics.ReleaseHdc(dc);} }
        bitmap.Save(Path.Combine(folder,name+".png"),ImageFormat.Png);
        using(var screen=new Bitmap(bitmap.Width,bitmap.Height))
        {
            using(var graphics=Graphics.FromImage(screen)) graphics.CopyFromScreen(bounds.Left,bounds.Top,0,0,screen.Size);
            screen.Save(Path.Combine(folder,name+"-screen.png"),ImageFormat.Png);
            int headerPixels=0;
            for(int y=(int)(18*scale);y<(int)(48*scale);y++)
                for(int x=(int)(36*scale);x<(int)(210*scale);x++)
                { var color=screen.GetPixel(x,y); if(color.R>130&&color.G>130&&color.B>100) headerPixels++; }
            check(headerPixels>100*scale*scale,"custom Setup header remains visible on the desktop: "+name);
        }
        var pixel=bitmap.GetPixel((int)(20*scale),(int)(100*scale));
        check(pixel.R==21&&pixel.G==23&&pixel.B==24,"custom Setup paints the app background: "+name);
        if(name is "setup-welcome" or "setup-finish" or "setup-uninstall")
        {
            int textPixels=0,iconPixels=0;
            for(int y=(int)(190*scale);y<(int)(238*scale);y++)
                for(int x=(int)(36*scale);x<(int)(604*scale);x++)
                { var color=bitmap.GetPixel(x,y); if(color.R>130&&color.G>130&&color.B>100) textPixels++; }
            for(int y=(int)(88*scale);y<(int)(164*scale);y++)
                for(int x=(int)(36*scale);x<(int)(112*scale);x++)
                { var color=bitmap.GetPixel(x,y); if(color.G>140&&color.R>100) iconPixels++; }
            if(textPixels<=100*scale*scale||iconPixels<=50*scale*scale)
            {
                using var screen=new Bitmap(bitmap.Width,bitmap.Height);
                using(var graphics=Graphics.FromImage(screen)) graphics.CopyFromScreen(bounds.Left,bounds.Top,0,0,screen.Size);
                screen.Save(Path.Combine(folder,name+"-screen.png"),ImageFormat.Png);
                foreach(var child in Children(window).Where(IsWindowVisible))
                {
                    GetWindowRect(child,out var rect);
                    Console.WriteLine($"Setup control {GetDlgCtrlID(child)}: {Text(child)}, bounds {rect.Left-bounds.Left},{rect.Top-bounds.Top},{rect.Right-rect.Left},{rect.Bottom-rect.Top}, style {GetWindowLong(child,-16):X}, exstyle {GetWindowLong(child,-20):X}");
                }
            }
            check(textPixels>100*scale*scale,"custom page heading is visibly painted: "+name);
            check(iconPixels>50*scale*scale,"custom page displays the actual abstract icon: "+name);
        }
        var next=GetDlgItem(window,1); GetWindowRect(next,out var button);
        check(button.Right<=bounds.Right&&button.Bottom<=bounds.Bottom&&button.Left>=bounds.Left,"Setup action remains inside the custom window: "+name);
    }
    static Process Start(string file,string args) => Process.Start(new ProcessStartInfo(file,args){UseShellExecute=true})??throw new Exception("Could not start installer");
    static void Close(Process process, bool success)
    {
        if(!process.WaitForExit(120000)) throw new Exception("Installer did not exit");
        if(success&&process.ExitCode!=0) throw new Exception("Installer failed: "+process.ExitCode);
    }
    public static void Run(string setup,string folder,Action<bool,string> check)
    {
        Directory.CreateDirectory(folder);
        string target=Path.Combine(Path.GetTempPath(),"kot-setup-ui-"+Guid.NewGuid().ToString("N"),"Проверка kot");
        List<Process> processes=[];
        try
        {
            var cancel=Start(setup,"/D="+target); processes.Add(cancel); var window=Window(cancel);
            Wait(()=>ByText(window,"Привет, это kot.")!=IntPtr.Zero,"welcome"); Capture(window,folder,"setup-welcome",check);
            Click(GetDlgItem(window,9009)); Close(cancel,false);
            check(!Directory.Exists(target),"cancelling custom Setup before installation creates no application files");
            var install=Start(setup,"/D="+target); processes.Add(install); window=Window(install);
            Wait(()=>ByText(window,"Привет, это kot.")!=IntPtr.Zero,"welcome again");
            Click(GetDlgItem(window,1)); Wait(()=>ById(window,1019)!=IntPtr.Zero,"directory");
            var edit=ById(window,1019); check(Text(edit)==target,"native directory page preserves the Unicode destination");
            var desktop=ById(window,1008); check(SendMessage(desktop,0xF0,IntPtr.Zero,IntPtr.Zero)==(IntPtr)1,"desktop shortcut starts enabled");
            SendMessage(desktop,0xF1,IntPtr.Zero,IntPtr.Zero); // BM_SETCHECK unchecked
            Click(GetDlgItem(window,3)); Wait(()=>ByText(window,"Привет, это kot.")!=IntPtr.Zero,"back to welcome");
            Click(GetDlgItem(window,1)); Wait(()=>ById(window,1019)!=IntPtr.Zero,"directory after back");
            desktop=ById(window,1008);
            check(SendMessage(desktop,0xF0,IntPtr.Zero,IntPtr.Zero)==IntPtr.Zero,"desktop option survives Back and Next");
            SendMessage(desktop,0xF1,(IntPtr)1,IntPtr.Zero); Capture(window,folder,"setup-options",check);
            Click(GetDlgItem(window,1)); Wait(()=>ById(window,1004)!=IntPtr.Zero,"progress"); Capture(window,folder,"setup-progress",check);
            Wait(()=>IsWindowEnabled(GetDlgItem(window,1)),"installation completion",120);
            Click(GetDlgItem(window,1)); Wait(()=>ByText(window,"Готово. Можно подключаться.")!=IntPtr.Zero,"finish");
            Capture(window,folder,"setup-finish",check);
            var run=ByText(window,"Запустить kot."); check(run!=IntPtr.Zero,"finish offers application launch"); SendMessage(run,0xF1,IntPtr.Zero,IntPtr.Zero);
            Click(GetDlgItem(window,1)); Close(install,true);
            check(File.Exists(Path.Combine(target,"Kot.exe"))&&File.Exists(Path.Combine(target,"Uninstall.exe")),"custom pages install the verified client in a Unicode path");
            string shortcut=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),"kot..lnk");
            check(File.Exists(shortcut),"custom installer creates the actual common desktop shortcut");
            object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!; object? link=null;
            try { dynamic wsh=shell; link=wsh.CreateShortcut(shortcut); dynamic value=link;
                string actualTarget=(string)value.TargetPath, actualIcon=(string)value.IconLocation;
                string? actualId=ShellIdentity.ShortcutId(shortcut);
                Console.WriteLine($"Installed desktop shortcut: target={actualTarget}; icon={actualIcon}; application ID={actualId}");
                static bool SamePath(string left,string right)=>!string.IsNullOrWhiteSpace(left)&&string.Equals(Path.GetFullPath(left),Path.GetFullPath(right),StringComparison.OrdinalIgnoreCase);
                check(SamePath(actualTarget,Path.Combine(target,"Kot.exe")),"desktop shortcut launches the installed executable");
                int separator=actualIcon.LastIndexOf(',');
                check(separator>0&&actualIcon[(separator+1)..].Trim()=="0"&&SamePath(actualIcon[..separator].Trim(' ', '"'),Path.Combine(target,"kot.ico"))&&File.Exists(Path.Combine(target,"kot.ico")),"desktop shortcut uses the installed abstract icon at index zero");
                check(actualId==ShellIdentity.AppId,"desktop shortcut persists the stable taskbar application ID");
            } finally {if(link!=null)Marshal.ReleaseComObject(link);Marshal.ReleaseComObject(shell);}
            File.WriteAllText(Path.Combine(target,"user-note.txt"),"preserve");
            var uninstall=Start(Path.Combine(target,"Uninstall.exe"),"_?="+target); processes.Add(uninstall); window=Window(uninstall);
            Wait(()=>ByText(window,"Удалить kot.?")!=IntPtr.Zero,"uninstall confirmation"); Capture(window,folder,"setup-uninstall",check);
            Click(GetDlgItem(window,1)); Wait(()=>ById(window,1004)!=IntPtr.Zero,"uninstall progress");
            Wait(()=>IsWindowEnabled(GetDlgItem(window,1)),"uninstall completion",120); Click(GetDlgItem(window,1)); Close(uninstall,true);
            check(!File.Exists(Path.Combine(target,"Kot.exe"))&&File.ReadAllText(Path.Combine(target,"user-note.txt"))=="preserve","custom uninstaller removes its files and preserves unrelated user data");
        }
        catch
        {
            foreach(var process in processes.Where(p=>!p.HasExited)) { try {Capture(Window(process),folder,"setup-failure",(_,_)=>{});}catch{} }
            throw;
        }
        finally
        {
            foreach(var process in processes) {try {if(!process.HasExited){process.Kill(true);process.WaitForExit(10000);}}finally{process.Dispose();}}
            if(Directory.Exists(Path.GetDirectoryName(target))) Directory.Delete(Path.GetDirectoryName(target)!,true);
        }
    }
}
