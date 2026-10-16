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
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)); work = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
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
        WriteDurable(temp, JsonSerializer.SerializeToUtf8Bytes(changes, PackageJsonContext.Default.ListChange)); File.Move(temp, path, true);
    }
    static void WriteDurable(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        output.Write(bytes); output.Flush(true);
    }
    static void CopyDurable(string source, string destination)
    {
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(true);
    }
    // The caller must hold its installation lock and stop every process using
    // the destination. In particular, a running Kot.exe cannot restore itself.
    public static string? PendingRecovery(string destination)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)); SafePath(root);
        if (!Directory.Exists(root)) return null;
        var pending = new List<string>();
        foreach (string folder in Directory.EnumerateDirectories(root, ".kot-update-*"))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(folder), "^\\.kot-update-[a-f0-9]{32}$")) continue;
            SafePath(folder);
            if (File.Exists(Path.Combine(folder, "journal.json")) && !File.Exists(Path.Combine(folder, "committed")) && !File.Exists(Path.Combine(folder, "rolled-back"))) pending.Add(folder);
        }
        if (pending.Count > 1) throw new UserError("Найдено несколько незавершённых обновлений. Резервные файлы сохранены; требуется восстановление установки.");
        return pending.SingleOrDefault();
    }
    public static bool RecoverPending(string destination)
    {
        string? pending = PendingRecovery(destination);
        if (pending == null) return false;
        new UpdateTransaction(destination, pending).Rollback(); return true;
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
                if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(old)!); CopyDurable(target, old); }
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
        {
            string journal = Path.Combine(work, "journal.json"); SafePath(journal);
            if (new FileInfo(journal).Length > 1024 * 1024) throw new UserError("Журнал обновления повреждён. Резервные файлы сохранены.");
            try { changes.AddRange(JsonSerializer.Deserialize(File.ReadAllText(journal), PackageJsonContext.Default.ListChange) ?? throw new JsonException()); }
            catch (JsonException) { throw new UserError("Журнал обновления повреждён. Резервные файлы сохранены."); }
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (changes.Count > 3000) throw new UserError("Журнал обновления слишком большой.");
        // Validate the whole journal and all backups before restoring any file.
        foreach (var change in changes)
        {
            if (change == null || change.Name == null || !names.Add(change.Name)) throw new UserError("Некорректный журнал обновления.");
            Target(change.Name);
            if (change.Existed)
            {
                string backup = Path.Combine(work, "backup", change.Name.Replace('/', Path.DirectorySeparatorChar)); SafePath(backup);
                if (!File.Exists(backup)) throw new UserError("Не найдена резервная копия: " + change.Name + ". Остальные файлы сохранены.");
            }
        }
        foreach (var change in changes.AsEnumerable().Reverse())
        {
            string target = Target(change.Name), backup = Path.Combine(work, "backup", change.Name.Replace('/', Path.DirectorySeparatorChar));
            if (change.Existed)
            {
                string temporary = target + ".kot-restore-" + Guid.NewGuid().ToString("N");
                try { CopyDurable(backup, temporary); File.Move(temporary, target, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else File.Delete(target);
        }
        WriteDurable(Path.Combine(work, "rolled-back"), "ok"u8.ToArray());
    }
    public void Commit() => WriteDurable(Path.Combine(work, "committed"), "ok"u8.ToArray());
}
