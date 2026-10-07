using System.IO.Compression;
using System.Text.Json;
namespace Kot.Core;

// The caller owns a private working directory and must stop the app before Apply.
// Backups and the journal survive a crash. Never remove them before a successful commit.
public sealed class UpdateTransaction
{
    public sealed record Change(string Name, bool Existed);
    readonly string root, work;
    readonly List<Change> changes = [];
    public UpdateTransaction(string destination, string workingDirectory)
    {
        root = Path.GetFullPath(destination); work = Path.GetFullPath(workingDirectory);
        if (Path.GetDirectoryName(work) != root || !Path.GetFileName(work).StartsWith(".kot-update-", StringComparison.Ordinal)) throw new UserError("Некорректная папка обновления.");
        SafePath(root); SafePath(work);
    }
    public static void SafePath(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new UserError("Обновление через ссылки или junction запрещено.");
    }
    string Target(string name) { if (!ReleasePackage.SafeName(name)) throw new UserError("Небезопасный путь обновления."); string p = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)); SafePath(p);
        if (Directory.Exists(p)) throw new UserError("Папка занимает путь файла обновления.");
        for (string? parent = Path.GetDirectoryName(p); parent != null && parent != root; parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent)) throw new UserError("Файл занимает путь папки обновления.");
        return p; }
    void Journal()
    {
        string path = Path.Combine(work, "journal.json"), temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(changes)); File.Move(temp, path, true);
    }
    public ReleaseManifest Apply(ZipArchive archive, Version current, string? expected = null, string? publicKey = null, Action<int>? afterWrite = null)
    {
        var manifest = ReleasePackage.Validate(archive, current, publicKey);
        if (expected != null && manifest.Version != expected) throw new UserError("Версия архива не совпадает с источником обновления.");
        Directory.CreateDirectory(work); string staged = Path.Combine(work, "staged"), backup = Path.Combine(work, "backup");
        Directory.CreateDirectory(staged); Directory.CreateDirectory(backup);
        // Validate all destination paths before the first write.
        foreach (var e in archive.Entries) { Target(e.FullName); var p = Path.Combine(staged, e.FullName.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(p)!); using var input = e.Open(); using var output = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None); input.CopyTo(output); }
        try
        {
            foreach (var e in archive.Entries.OrderBy(e => e.FullName.Equals("Kot.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                string target = Target(e.FullName), old = Path.Combine(backup, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                bool existed = File.Exists(target);
                if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(old)!); File.Copy(target, old); }
                changes.Add(new(e.FullName, existed)); Journal(); // Write-ahead: rollback is safe even if the next rename never happened.
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(staged, e.FullName.Replace('/', Path.DirectorySeparatorChar)), target, true);
                afterWrite?.Invoke(changes.Count);
            }
            return manifest;
        }
        catch { Rollback(); throw; }
    }
    public void Rollback()
    {
        if (changes.Count == 0 && File.Exists(Path.Combine(work, "journal.json")))
            changes.AddRange(JsonSerializer.Deserialize<List<Change>>(File.ReadAllText(Path.Combine(work, "journal.json"))) ?? []);
        foreach (var change in changes.AsEnumerable().Reverse())
        {
            string target = Target(change.Name), backup = Path.Combine(work, "backup", change.Name.Replace('/', Path.DirectorySeparatorChar));
            if (change.Existed) { SafePath(backup); File.Copy(backup, target, true); }
            else File.Delete(target);
        }
        File.WriteAllText(Path.Combine(work, "rolled-back"), "ok");
    }
    public void Commit() => File.WriteAllText(Path.Combine(work, "committed"), "ok");
}
