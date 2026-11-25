using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

// Windows PowerShell 5.1 cannot load the client's .NET 10 assemblies. Query
// the same native SDK loader, after Checks.ps1 verifies the installed payload.
// This discovers availability only; WindowTests exercises actual WebView startup.
public static class KotQaWebViewRuntime
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    delegate int GetVersion([MarshalAs(UnmanagedType.LPWStr)] string folder, out IntPtr version);

    public static string Probe(string loaderPath)
    {
        if (!Path.IsPathRooted(loaderPath) || !File.Exists(loaderPath))
            throw new ArgumentException("verified-loader-required");
        // Search dependencies in the verified DLL's folder and System32 only.
        var module = LoadLibraryEx(Path.GetFullPath(loaderPath), IntPtr.Zero, 0x100 | 0x800);
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr version = IntPtr.Zero;
        try
        {
            var address = GetProcAddress(module, "GetAvailableCoreWebView2BrowserVersionString");
            if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var probe = (GetVersion)Marshal.GetDelegateForFunctionPointer(address, typeof(GetVersion));
            int result = probe(null, out version);
            if (result == unchecked((int)0x80070002)) return null; // runtime not found
            Marshal.ThrowExceptionForHR(result);
            return version == IntPtr.Zero ? null : Marshal.PtrToStringUni(version);
        }
        finally
        {
            if (version != IntPtr.Zero) Marshal.FreeCoTaskMem(version);
            FreeLibrary(module);
        }
    }
}
