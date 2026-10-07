using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Kot.Core;
namespace Kot.Windows;

// Use WinINet's LAN settings API rather than modifying undocumented registry blobs.
// Save before changing Windows and restore only while the settings still belong to us.
public static class SystemProxy
{
    internal sealed record Settings(uint Flags, string Server, string Bypass, string AutoUrl);
    sealed record Receipt(string Token, Settings Original, Settings Applied);
    static string ReceiptPath => Path.Combine(Store.Folder, "system-proxy.bin");
    static readonly byte[] Entropy = "KotVPN.system-proxy.v1"u8.ToArray();
    static Mutex Lock() => new(false, "Local\\KotVPN-SystemProxy-" + WindowsIdentity.GetCurrent().User!.Value);
    static void Enter(Mutex mutex) { try { if (!mutex.WaitOne(15000)) throw new UserError("Настройки прокси Windows заняты."); } catch (AbandonedMutexException) { } }
    public static void Enable(int port)
    {
        using var mutex = Lock(); Enter(mutex);
        try
        {
            RestoreLocked(null);
            if (port is < 1 or > 65535) throw new UserError("Некорректный порт прокси.");
            var receipt = new Receipt(Guid.NewGuid().ToString("N"), Read(), new(3, "127.0.0.1:" + port, "<local>;localhost;127.*;[::1]", ""));
            Directory.CreateDirectory(Store.Folder);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(receipt);
            try { File.WriteAllBytes(ReceiptPath + ".tmp", ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            File.Move(ReceiptPath + ".tmp", ReceiptPath, true);
            // The helper survives a terminated UI and restores the saved settings when that exact process exits.
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            foreach (string value in new[] { "--proxy-watch", Environment.ProcessId.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString(), receipt.Token }) start.ArgumentList.Add(value);
            try
            {
                using var watcher = Process.Start(start) ?? throw new UserError("Не удалось запустить восстановление прокси.");
                Write(receipt.Applied);
                if (Read() != receipt.Applied) throw new UserError("Windows не применил настройки системного прокси.");
            }
            catch { Write(receipt.Original); File.Delete(ReceiptPath); throw; }
            AppLog.Write("system-proxy", "enabled on loopback");
        }
        finally { mutex.ReleaseMutex(); }
    }
    public static void Restore(string? token = null)
    {
        using var mutex = Lock(); Enter(mutex);
        try { RestoreLocked(token); }
        finally { mutex.ReleaseMutex(); }
    }
    static void RestoreLocked(string? token)
    {
        if (!File.Exists(ReceiptPath)) return;
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(ReceiptPath), Entropy, DataProtectionScope.CurrentUser);
        Receipt receipt;
        try { receipt = JsonSerializer.Deserialize<Receipt>(plain) ?? throw new UserError("Не удалось прочитать прежние настройки прокси."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (token != null && receipt.Token != token) return;
        if (Read() == receipt.Applied) Write(receipt.Original);
        // If another app/user changed the settings, leave their new selection alone.
        File.Delete(ReceiptPath);
        AppLog.Write("system-proxy", "restored or released after external change");
    }
    public static void Watch(int pid, long ticks, string token)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks == ticks)
            while (!parent.WaitForExit(500))
            {
                using var mutex = Lock(); Enter(mutex);
                try
                {
                    if (!File.Exists(ReceiptPath)) return;
                    byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(ReceiptPath), Entropy, DataProtectionScope.CurrentUser);
                    try { if (JsonSerializer.Deserialize<Receipt>(plain)?.Token != token) return; }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        catch (ArgumentException) { }
        Restore(token);
    }
    internal static Settings Read()
    {
        // FLAGS_UI preserves the visible auto-detection setting instead of WinINet's cached result.
        int size = Marshal.SizeOf<Option>();
        var buffer = Marshal.AllocHGlobal(size * 4);
        try
        {
            uint[] ids = [10, 2, 3, 4];
            for (int i = 0; i < ids.Length; i++) Marshal.StructureToPtr(new Option { Id = ids[i] }, buffer + i * size, false);
            var list = List(buffer); int bytes = Marshal.SizeOf<OptionList>();
            if (!InternetQueryOption(IntPtr.Zero, 75, ref list, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var values = Enumerable.Range(0, 4).Select(i => Marshal.PtrToStructure<Option>(buffer + i * size)).ToArray();
            try { return new(values[0].Value.Number, Text(values[1]), Text(values[2]), Text(values[3])); }
            finally { for (int i = 1; i < values.Length; i++) if (values[i].Value.Pointer != IntPtr.Zero) GlobalFree(values[i].Value.Pointer); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    static string Text(Option value) => Marshal.PtrToStringUni(value.Value.Pointer) ?? "";
    internal static void Write(Settings settings)
    {
        int size = Marshal.SizeOf<Option>();
        var buffer = Marshal.AllocHGlobal(size * 4);
        var strings = new[] { settings.Server, settings.Bypass, settings.AutoUrl }.Select(Marshal.StringToHGlobalUni).ToArray();
        try
        {
            Marshal.StructureToPtr(new Option { Id = 1, Value = new() { Number = settings.Flags } }, buffer, false);
            for (int i = 0; i < strings.Length; i++) Marshal.StructureToPtr(new Option { Id = (uint)i + 2, Value = new() { Pointer = strings[i] } }, buffer + (i + 1) * size, false);
            var list = List(buffer);
            if (!InternetSetOption(IntPtr.Zero, 75, ref list, Marshal.SizeOf<OptionList>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!InternetNotify(IntPtr.Zero, 95, IntPtr.Zero, 0) || !InternetNotify(IntPtr.Zero, 37, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { foreach (var pointer in strings) Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(buffer); }
    }
    static OptionList List(IntPtr options) => new() { Size = (uint)Marshal.SizeOf<OptionList>(), Count = 4, Options = options };
    [StructLayout(LayoutKind.Explicit, Size = 8)] struct OptionValue { [FieldOffset(0)] public uint Number; [FieldOffset(0)] public IntPtr Pointer; }
    [StructLayout(LayoutKind.Sequential)] struct Option { public uint Id; public OptionValue Value; }
    [StructLayout(LayoutKind.Sequential)] struct OptionList { public uint Size; public IntPtr Connection; public uint Count; public uint Error; public IntPtr Options; }
    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)] static extern bool InternetQueryOption(IntPtr handle, int option, ref OptionList list, ref int bytes);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)] static extern bool InternetSetOption(IntPtr handle, int option, ref OptionList list, int bytes);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)] static extern bool InternetNotify(IntPtr handle, int option, IntPtr buffer, int bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr pointer);
}
