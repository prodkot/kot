using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
namespace Kot.Windows;

// Stable across versions and shared by the executable and its installed shortcuts.
// AppUserModelID controls the Windows shell. Task Manager separately groups child
// processes; its WebView2 display on recent Windows 11 builds is runtime-dependent.
public static class ShellIdentity
{
    public static string AppId => "Prodkot.Kot.Client";
    static readonly Guid PropertyStoreId = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    static PropertyKey IdKey => new() { Format = new("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3"), Id = 5 };

    public static void Initialize() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
    public static string? CurrentId()
    {
        Marshal.ThrowExceptionForHR(GetCurrentProcessExplicitAppUserModelID(out var value));
        try { return Marshal.PtrToStringUni(value); } finally { Marshal.FreeCoTaskMem(value); }
    }
    public static void RegisterInstalledShortcuts()
    {
        foreach (string path in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "kot.", "kot..lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "kot..lnk") })
            if (File.Exists(path))
            {
                if (Environment.ProcessPath is { } target)
                    RegisterInstalledShortcut(path, target, Path.Combine(AppContext.BaseDirectory, "kot.ico"));
            }
    }
    public static void RegisterInstalledShortcut(string path, string target, string iconPath)
    {
        // These are the installer's two app-owned shortcuts. Write their paths
        // through the Unicode shell interface, including on non-Russian Windows.
        var link = LoadShortcut(path, 2);
        try
        {
            link.SetPath(Path.GetFullPath(target));
            link.SetWorkingDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            link.SetIconLocation(Path.GetFullPath(iconPath), 0);
            ((IPersistFile)link).Save(Path.GetFullPath(path), true);
        }
        finally { Marshal.ReleaseComObject(link); }
        SetShortcutId(path);
        SHChangeNotify(0x00002000, 0x00002005, path, IntPtr.Zero);
    }
    public readonly record struct Shortcut(string Target, string IconPath, int IconIndex, string Arguments, string WorkingDirectory);
    public static Shortcut ReadShortcut(string path)
    {
        var link = LoadShortcut(path, 0);
        try
        {
            var target = new StringBuilder(32768); var icon = new StringBuilder(32768);
            var args = new StringBuilder(32768); var directory = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
            link.GetIconLocation(icon, icon.Capacity, out int index);
            link.GetArguments(args, args.Capacity); link.GetWorkingDirectory(directory, directory.Capacity);
            return new(target.ToString(), icon.ToString(), index, args.ToString(), directory.ToString());
        }
        finally { Marshal.ReleaseComObject(link); }
    }
    public static void RefreshPinnedIcon(string iconPath)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        if (!Directory.Exists(folder) || Environment.ProcessPath is not { } target) return;
        foreach (string path in Directory.EnumerateFiles(folder, "*.lnk"))
        {
            try { SetShortcutIcon(path, target, iconPath); }
            catch (Exception ex) { AppLog.Error("pinned icon refresh", ex); }
        }
    }
    public static bool SetShortcutIcon(string path, string target, string iconPath)
    {
        var link = LoadShortcut(path, 2);
        try
        {
            // Only edit this executable's shortcut; leave other pinned applications alone.
            var buffer = new StringBuilder(32768);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 4);
            string shortcutTarget = buffer.ToString();
            if (string.IsNullOrWhiteSpace(shortcutTarget) || !string.Equals(Path.GetFullPath(shortcutTarget), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return false;
            string location = Path.GetFullPath(iconPath);
            buffer.Clear(); link.GetIconLocation(buffer, buffer.Capacity, out int index);
            if (index == 0 && string.Equals(buffer.ToString(), location, StringComparison.OrdinalIgnoreCase)) return true;
            link.SetIconLocation(location, 0); ((IPersistFile)link).Save(Path.GetFullPath(path), true);
            SHChangeNotify(0x00002000, 0x00002005, path, IntPtr.Zero); // UPDATEITEM, PATHW | FLUSHNOWAIT
            return true;
        }
        finally { Marshal.ReleaseComObject(link); }
    }
    static IShellLinkW LoadShortcut(string path, int mode)
    {
        var link = (IShellLinkW)new NativeShellLink();
        try { ((IPersistFile)link).Load(Path.GetFullPath(path), mode); return link; }
        catch { Marshal.ReleaseComObject(link); throw; }
    }
    [ComImport, Guid("00021401-0000-0000-c000-000000000046")] class NativeShellLink { }
    [ComImport, Guid("000214f9-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr data, uint flags);
        void GetIDList(out IntPtr id); void SetIDList(IntPtr id);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key); void SetHotkey(short key);
        void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    public static void SetShortcutId(string path)
    {
        var iid = PropertyStoreId;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(Path.GetFullPath(path), IntPtr.Zero, 2, ref iid, out var store));
        var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(AppId) };
        try { var key = IdKey; Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); Marshal.ThrowExceptionForHR(store.Commit()); }
        finally { PropVariantClear(ref value); Marshal.ReleaseComObject(store); }
    }
    public static string? ShortcutId(string path)
    {
        var iid = PropertyStoreId;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(Path.GetFullPath(path), IntPtr.Zero, 0, ref iid, out var store));
        var value = new PropVariant();
        try { var key = IdKey; Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value)); return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); Marshal.ReleaseComObject(store); }
    }
    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32.dll")] static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(uint events, uint flags, string item, IntPtr other);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr context, uint flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
}
