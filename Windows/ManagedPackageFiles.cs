using Kot.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace Kot.Windows;

internal static class ManagedPackageFiles
{
    // Only owned paths are eligible. Never unload a driver shared with other applications.
    public static bool Remove(string ownedRoot, string path, string? immediateFile = null)
    {
        string root = Path.GetFullPath(ownedRoot).TrimEnd(Path.DirectorySeparatorChar);
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UserError("Удаление вне папки управляемого сервиса запрещено.");
        for (string? parent = path; parent != null && parent.Length >= root.Length; parent = Path.GetDirectoryName(parent))
            if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new UserError("Папка сервиса содержит перенаправление.");
        if (!Directory.Exists(path)) return false;

        var files = new List<string>(); var directories = new List<string>();
        // Validate the complete tree before deleting anything; do not follow junctions or links.
        void Collect(string directory)
        {
            foreach (string item in Directory.GetFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UserError("Папка сервиса содержит перенаправление.");
                if ((attributes & FileAttributes.Directory) != 0) Collect(item); else files.Add(item);
            }
            directories.Add(directory);
        }
        Collect(path);
        // The manifest has a reusable name. Never queue it for reboot deletion: a fresh
        // installation could otherwise recreate it before the pending operation runs.
        if (immediateFile != null && files.Remove(Path.GetFullPath(immediateFile))) File.Delete(immediateFile);
        bool deferred = false;
        foreach (string file in files)
        {
            try { File.Delete(file); }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && (ex.HResult & 0xffff) is 5 or 32 or 33)
            {
                Schedule(file); deferred = true;
            }
        }
        foreach (string directory in directories)
        {
            try { Directory.Delete(directory); }
            catch (IOException ex) when (deferred && (ex.HResult & 0xffff) == 145) { Schedule(directory); }
        }
        return deferred;
    }
    static void Schedule(string path)
    {
        if (!MoveFileEx(path, null, 4)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось запланировать удаление файла сервиса после перезагрузки Windows.");
    }
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool MoveFileEx(string existing, string? destination, uint flags);
}
