using Kot.Core;
namespace Kot.Installer;

public static class SetupExtraction
{
    public static void Extract(string setup, string destination, string version, string? publicKey = null, Action<int>? afterWrite = null, Action<int, int>? onProgress = null)
    {
        string source = Path.GetFullPath(setup), root = Path.GetFullPath(destination);
        UpdateTransaction.SafePath(root);
        using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = SetupPackage.Open(file, expected: version, publicKey: publicKey);
        var manifest = ReleasePackage.Validate(archive, publicKey: publicKey);
        if (manifest.Version != version) throw new UserError("Версии установщика и приложения не совпадают.");
        foreach (var entry in archive.Entries)
            if (source.Equals(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase))
                throw new UserError("Переместите установщик за пределы файлов приложения.");

        // Authenticate before creating anything in the target. Stage all files before
        // replacement, preserving a crash-recovery journal and copies of old files.
        string work = Path.Combine(root, ".kot-update-" + Guid.NewGuid().ToString("N"));
        var transaction = new UpdateTransaction(root, work);
        try
        {
            // Manual Setup also supports reinstalling the same version.
            transaction.Apply(archive, new Version("0.0.0"), version, publicKey, count =>
            {
                afterWrite?.Invoke(count);
                onProgress?.Invoke(count, archive.Entries.Count);
            });
            transaction.Commit();
        }
        catch
        {
            if (File.Exists(Path.Combine(work, "journal.json")) && !File.Exists(Path.Combine(work, "rolled-back")))
                throw new UserError("Не удалось восстановить установку. Резервные файлы сохранены: " + work);
            throw;
        }
        finally
        {
            bool safe = !File.Exists(Path.Combine(work, "journal.json")) || File.Exists(Path.Combine(work, "rolled-back")) || File.Exists(Path.Combine(work, "committed"));
            if (safe && Directory.Exists(work))
            {
                try { Directory.Delete(work, true); }
                catch (IOException) { } // A committed journal can be cleaned up at app startup.
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
