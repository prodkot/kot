using System.Runtime.InteropServices;
namespace Kot.Windows;

// Stable across versions and shared by the executable and its installed shortcuts.
// AppUserModelID controls the Windows shell. Task Manager separately groups child
// processes; its WebView2 display on recent Windows 11 builds is runtime-dependent.
public static class ShellIdentity
{
    public const string AppId = "Prodkot.Kot.Client";
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
            if (File.Exists(path)) SetShortcutId(path);
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
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr context, uint flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
}
