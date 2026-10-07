using System.ComponentModel;
using System.Runtime.InteropServices;
using Kot.Core;
namespace Kot.Windows;

// Persistent WFP filters survive termination of both kot. and its core.
// Only our own fixed keys are ever modified. Transactions avoid an unfiltered gap.
public static class KillSwitch
{
    static readonly Guid Sublayer = new("da3e55f0-f72b-4ee7-b67b-8a74560a0100");
    static readonly Guid[] Layers = [new("c38d57d1-05a7-4c33-904f-7fbceee60e82"), new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"), new("e1cd9fe7-f4b5-4273-96c0-592e487b8650"), new("a3b42c97-9f04-4672-b87e-cee9c483257f")];
    static readonly Guid AppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971"), Flags = new("632ce23b-5167-435c-86d7-e903684aa80c"), Interface = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    static readonly Guid Protocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7"), RemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    static Guid Key(int n) => new($"da3e55f0-f72b-4ee7-b67b-8a74560a{n + 1:x4}");
    public static bool Active { get; private set; }
    public static bool Detect()
    {
        using var engine = new Engine(); var key = Key(2); uint result = FwpmFilterGetByKey0(engine.Handle, ref key, out var pointer);
        if (result == 0) FwpmFreeMemory0(ref pointer); else if (result != 0x80320003) Error(result);
        return Active = result == 0;
    }
    public static void Arm(ulong tun = 0) => Configure(true, tun);
    public static void Release() => Configure(false, 0);
    public static ulong TunnelInterface()
    {
        uint result = ConvertInterfaceAliasToLuid("kot-tun", out ulong luid);
        if (result != 0) throw new UserError("Kill switch: сетевой интерфейс kot-tun не появился.");
        return luid;
    }
    internal static void ArmForTests(string application, string allowed, ulong tun = 0) => Configure(true, tun, application, allowed);
    static void Configure(bool enabled, ulong tun, string? testApplication = null, string? testAllowed = null)
    {
        using var engine = new Engine(); Error(FwpmTransactionBegin0(engine.Handle, 0));
        try
        {
            for (int n = 0; n < 12; n++) { var key = Key(n); uint r = FwpmFilterDeleteByKey0(engine.Handle, ref key); if (r != 0 && r != 0x80320003) Error(r); }
            if (enabled)
            {
                var layer = new SubLayer { Key = Sublayer, Display = new() { Name = "kot. kill switch" }, Flags = 1, Weight = 0x7fff };
                uint r = FwpmSubLayerAdd0(engine.Handle, ref layer, IntPtr.Zero); if (r != 0 && r != 0x80320009) Error(r);
                Error(FwpmGetAppIdFromFileName0(testAllowed ?? Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe"), out var core));
                try
                {
                    Error(FwpmGetAppIdFromFileName0(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "svchost.exe"), out var dhcp));
                    try
                    {
                        for (int i = 0; i < Layers.Length; i++)
                        {
                            Add(engine.Handle, Key(i * 3), Layers[i], true, [new(AppId, 0, Value.Pointer(12, core))]);
                            Add(engine.Handle, Key(i * 3 + 1), Layers[i], true, [new(AppId, 0, Value.Pointer(12, dhcp)), new(Protocol, 0, Value.Number(1, 17)), new(RemotePort, 0, Value.Number(2, i % 2 == 0 ? 67u : 547u))]);
                            var conditions = new List<Condition> { new(Flags, 8, Value.Number(3, 1)) }; // non-loopback only
                            IntPtr luid = IntPtr.Zero, testId = IntPtr.Zero;
                            try
                            {
                                if (testApplication != null) { Error(FwpmGetAppIdFromFileName0(testApplication, out testId)); conditions.Add(new(AppId, 0, Value.Pointer(12, testId))); }
                                if (tun != 0) { luid = Marshal.AllocHGlobal(8); Marshal.WriteInt64(luid, unchecked((long)tun)); conditions.Add(new(Interface, 10, Value.Pointer(4, luid))); }
                                Add(engine.Handle, Key(i * 3 + 2), Layers[i], false, conditions.ToArray());
                            }
                            finally { if (luid != IntPtr.Zero) Marshal.FreeHGlobal(luid); if (testId != IntPtr.Zero) FwpmFreeMemory0(ref testId); }
                        }
                    }
                    finally { FwpmFreeMemory0(ref dhcp); }
                }
                finally { FwpmFreeMemory0(ref core); }
            }
            Error(FwpmTransactionCommit0(engine.Handle)); Active = enabled;
            AppLog.Write("kill-switch", enabled ? "armed; tunnel interface=" + tun : "released");
        }
        catch { FwpmTransactionAbort0(engine.Handle); throw; }
    }
    static void Add(IntPtr engine, Guid key, Guid layer, bool permit, Condition[] conditions)
    {
        int size = Marshal.SizeOf<Condition>(); var memory = Marshal.AllocHGlobal(size * conditions.Length);
        try
        {
            for (int i = 0; i < conditions.Length; i++) Marshal.StructureToPtr(conditions[i], memory + i * size, false);
            var filter = new Filter { Key = key, Display = new() { Name = "kot. kill switch: " + (permit ? "core / DHCP" : "block direct traffic") }, Flags = 1, Layer = layer, Sublayer = Sublayer, Weight = Value.Number(1, permit ? 15u : 14u), Count = (uint)conditions.Length, Conditions = memory, Action = new() { Type = permit ? 0x1002u : 0x1001u } };
            Error(FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    static void Error(uint code) { if (code != 0) throw new UserError($"Не удалось настроить kill switch (WFP 0x{code:X8}): {new Win32Exception(unchecked((int)code)).Message}"); }
    sealed class Engine : IDisposable
    {
        public IntPtr Handle;
        public Engine() { Error(FwpmEngineOpen0(null, 10, IntPtr.Zero, IntPtr.Zero, out Handle)); }
        public void Dispose() { if (Handle != IntPtr.Zero) FwpmEngineClose0(Handle); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Display { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; [MarshalAs(UnmanagedType.LPWStr)] public string? Description; }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] struct Value
    {
        [FieldOffset(0)] public uint Type; [FieldOffset(8)] public uint NumberValue; [FieldOffset(8)] public IntPtr PointerValue;
        public static Value Number(uint type, uint value) => new() { Type = type, NumberValue = value };
        public static Value Pointer(uint type, IntPtr value) => new() { Type = type, PointerValue = value };
    }
    [StructLayout(LayoutKind.Sequential)] struct Condition(Guid key, uint match, Value value) { public Guid Key = key; public uint Match = match; public Value Value = value; }
    [StructLayout(LayoutKind.Sequential)] struct Action { public uint Type; public Guid Key; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] struct Context { [FieldOffset(0)] public Guid Key; [FieldOffset(0)] public ulong Raw; }
    [StructLayout(LayoutKind.Sequential)] struct SubLayer { public Guid Key; public Display Display; public uint Flags; public IntPtr Provider; public Blob Data; public ushort Weight; }
    [StructLayout(LayoutKind.Sequential)] struct Filter { public Guid Key; public Display Display; public uint Flags; public IntPtr Provider; public Blob Data; public Guid Layer; public Guid Sublayer; public Value Weight; public uint Count; public IntPtr Conditions; public Action Action; public Context Context; public IntPtr Reserved; public ulong Id; public Value EffectiveWeight; }
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] static extern uint FwpmEngineOpen0(string? server, uint auth, IntPtr identity, IntPtr session, out IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionCommit0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionAbort0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmSubLayerAdd0(IntPtr engine, ref SubLayer layer, IntPtr security);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter filter, IntPtr security, out ulong id);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterGetByKey0(IntPtr engine, ref Guid key, out IntPtr filter);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] static extern uint FwpmGetAppIdFromFileName0(string name, out IntPtr id);
    [DllImport("fwpuclnt.dll")] static extern void FwpmFreeMemory0(ref IntPtr memory);
    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)] static extern uint ConvertInterfaceAliasToLuid(string alias, out ulong luid);
}
