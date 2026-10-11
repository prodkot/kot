using System.Runtime.InteropServices;
namespace Kot.WindowTests;

static class ProcessParent
{
    // Inspect only the handles of processes created by the fixtures, never enumerate the machine.
    [StructLayout(LayoutKind.Sequential)] struct BasicInformation
    {
        public int ExitStatus; public IntPtr Peb; public UIntPtr Affinity;
        public int Priority; public UIntPtr Id; public UIntPtr Parent;
    }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr handle, int kind, out BasicInformation info, int length, out int returned);
    public static int Id(IntPtr handle)
    {
        int status = NtQueryInformationProcess(handle, 0, out var info, Marshal.SizeOf<BasicInformation>(), out _);
        if (status != 0) throw new InvalidOperationException("Process parent query failed: " + status.ToString("X8"));
        return checked((int)info.Parent.ToUInt64());
    }
}
