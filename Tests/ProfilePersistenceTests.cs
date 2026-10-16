using Kot.Core;
using System.Text.Json;
namespace Kot.Tests;

public static class ProfilePersistenceTests
{
    public static void Run(Action<bool, string> check, string folder)
    {
        Directory.CreateDirectory(folder);
        var store = new ProfilePersistence(folder, bytes => JsonSerializer.Deserialize<Profile>(bytes) ?? throw new JsonException(), profile => JsonSerializer.SerializeToUtf8Bytes(profile));
        check(store.Load().Nodes.Count == 0, "first launch has an empty profile");
        store.Save(new() { Theme = "light" }); store.Save(new() { Theme = "dark" });
        check(store.Load().Theme == "dark" && store.LoadBackup().Theme == "light", "saving retains the previous valid profile");
        File.WriteAllText(store.PrimaryPath, "broken bytes");
        bool failed = false; try { store.Save(new()); } catch (JsonException) { failed = true; }
        check(failed && File.ReadAllText(store.PrimaryPath) == "broken bytes" && store.LoadBackup().Theme == "light", "damaged primary cannot replace the last good backup");
        check(store.RestoreBackup().Theme == "light" && store.Load().Theme == "light" && Directory.GetFiles(folder, "profile-recovery-*.bin").Any(path => File.ReadAllText(path) == "broken bytes"), "recovery preserves damaged bytes and restores a readable profile");
        File.Delete(store.PrimaryPath); failed = false;
        try { store.Load(); } catch (UserError) { failed = true; }
        check(failed && store.RestoreBackup().Theme == "light", "missing primary offers the existing backup instead of silently resetting");
        File.WriteAllText(store.PrimaryPath, "damaged primary"); File.WriteAllText(store.BackupPath, "damaged backup");
        store.Replace(new() { Theme = "dark" });
        check(store.Load().Theme == "dark" && !File.Exists(store.BackupPath) && Directory.GetFiles(folder, "*-recovery-*.bin").Any(path => File.ReadAllText(path) == "damaged primary") && Directory.GetFiles(folder, "*-recovery-*.bin").Any(path => File.ReadAllText(path) == "damaged backup"), "explicit reset/import preserves both damaged files");
        store.Save(new() { Theme = "light" });
        string before = File.ReadAllText(store.PrimaryPath);
        var invalid = new ProfilePersistence(folder, _ => throw new JsonException(), _ => "invalid"u8.ToArray());
        failed = false; try { invalid.Replace(new()); } catch (JsonException) { failed = true; }
        check(failed && File.ReadAllText(store.PrimaryPath) == before, "failed replacement validation preserves the primary");
        check(Directory.GetFiles(folder, "*.tmp").Length == 0, "profile writes leave no temporary files");
        var live = new Profile { Name = "Original", Local = true, Favorites = ["old"] };
        live.CaptureActive(); store.Save(live);
        var failing = new ProfilePersistence(folder, bytes => JsonSerializer.Deserialize<Profile>(bytes)!, _ => throw new IOException("disk full"));
        failed = false;
        try { failing.Change(live, p => { p.Name = "Changed"; p.Favorites.Clear(); p.Subscriptions.Clear(); }); }
        catch (IOException) { failed = true; }
        check(failed && live.Name == "Original" && live.Favorites.SequenceEqual(["old"]) && live.Subscriptions.Count == 1 && store.Load().Name == "Original", "failed edit preserves both the live profile and its saved subscriptions");
        var changed = store.Change(live, p => { p.Name = "Changed"; p.Favorites.Clear(); });
        check(changed.Name == "Changed" && changed.Favorites.Count == 0 && changed.Subscriptions[0].Name == "Changed" && live.Name == "Original" && live.Favorites.Count == 1 && store.Load().Name == "Changed", "successful edit commits a separate profile and captures its active subscription");
    }
}
