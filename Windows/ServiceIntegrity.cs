using Kot.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Kot.Windows;

// An explicit low/medium executable label can lower a child even when kot is elevated.
// Repair only lower labels inside an owned service; retain DACLs, owners and audit ACEs.
internal static class ServiceIntegrity
{
    internal const int Medium = 0x2000, High = 0x3000;
    const uint LabelInformation = 0x10;
    readonly record struct Label(int Level, uint Policy);

    internal static bool Prepare(string ownedRoot, string serviceDirectory, int minimum)
    {
        if (minimum is not (Medium or High)) throw new ArgumentOutOfRangeException(nameof(minimum));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedRoot));
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serviceDirectory));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UserError("Исправление прав вне папки сервисов запрещено.");
        for (string? parent = directory; parent != null && parent.Length >= root.Length; parent = Path.GetDirectoryName(parent))
            RejectLink(parent);

        var paths = new List<(string Path, bool Directory, Label? Label)>();
        void Collect(string path)
        {
            RejectLink(path);
            bool isDirectory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            paths.Add((path, isDirectory, ReadLabelDetails(path)));
            if (isDirectory) foreach (string child in Directory.GetFileSystemEntries(path)) Collect(child);
        }
        // Validate the entire tree before changing an inheritable label. In particular,
        // never let Windows propagate a label through a junction into another app.
        Collect(directory);
        bool changed = false;
        foreach (var path in paths)
        {
            // An unlabeled executable inherits the normal launch context. Leave it alone.
            if (path.Label is not { } label || label.Level >= minimum) continue;
            RejectLink(path.Path);
            SetLabel(path.Path, minimum, path.Directory, label.Policy); changed = true;
        }
        return changed;
    }

    static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UserError("Папка сервиса содержит перенаправление; права не изменены.");
    }

    internal static int? ReadLabel(string path) => ReadLabelDetails(path)?.Level;
    internal static uint? ReadPolicy(string path) => ReadLabelDetails(path)?.Policy;
    static Label? ReadLabelDetails(string path)
    {
        uint error = GetNamedSecurityInfo(path, 1, LabelInformation, out _, out _, out _, out IntPtr acl, out IntPtr descriptor);
        if (error != 0) throw new Win32Exception((int)error, "Не удалось проверить метку доступа пакета сервиса.");
        try
        {
            if (acl == IntPtr.Zero) return null;
            int count = (ushort)Marshal.ReadInt16(acl, 4);
            for (uint i = 0; i < count; i++)
            {
                if (!GetAce(acl, i, out IntPtr ace)) throw new Win32Exception();
                if (Marshal.ReadByte(ace) != 0x11) continue; // SYSTEM_MANDATORY_LABEL_ACE
                var sid = new SecurityIdentifier(ace + 8);
                return new(int.Parse(sid.Value[(sid.Value.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture), unchecked((uint)Marshal.ReadInt32(ace, 4)));
            }
            return null;
        }
        finally { LocalFree(descriptor); }
    }

    internal static void SetLabel(string path, int level, bool directory, uint policy = 1)
    {
        string sid = "S-1-16-" + level.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string sddl = "S:(ML;" + (directory ? "OICI" : "") + ";0x" + policy.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ";;;" + sid + ")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr descriptor, out _)) throw new Win32Exception();
        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out _, out IntPtr acl, out _)) throw new Win32Exception();
            uint error = SetNamedSecurityInfo(path, 1, LabelInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, acl);
            if (error != 0) throw new Win32Exception((int)error, "Не удалось восстановить метку доступа пакета сервиса.");
        }
        finally { LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", CharSet = CharSet.Unicode)]
    static extern uint GetNamedSecurityInfo(string path, int type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetAce(IntPtr acl, uint index, out IntPtr ace);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, out bool present, out IntPtr sacl, out bool defaulted);
    [DllImport("advapi32.dll", EntryPoint = "SetNamedSecurityInfoW", CharSet = CharSet.Unicode)]
    static extern uint SetNamedSecurityInfo(string path, int type, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
}
