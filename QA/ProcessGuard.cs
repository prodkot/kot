using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

// The OS closes this non-inherited job handle even if PowerShell is killed.
// Only processes started by this QA invocation are assigned to it.
public sealed class KotQaProcessGuard : IDisposable
{
    IntPtr job;
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters {
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimit info, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public KotQaProcessGuard() {
        job=CreateJobObject(IntPtr.Zero,null);
        if(job==IntPtr.Zero) throw new Win32Exception();
        var info=new ExtendedLimit(); info.Basic.Flags=0x2000;
        if(!SetInformationJobObject(job,9,ref info,(uint)Marshal.SizeOf(typeof(ExtendedLimit)))) {
            int error=Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error);
        }
    }
    public void Add(Process process) {
        if(!AssignProcessToJobObject(job,process.Handle)) {
            int error=Marshal.GetLastWin32Error();
            if(process.HasExited) return;
            process.Kill(); process.WaitForExit(); throw new Win32Exception(error);
        }
    }
    public void Dispose() { if(job!=IntPtr.Zero) { CloseHandle(job);job=IntPtr.Zero; } }
}
