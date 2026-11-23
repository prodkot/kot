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

        var old = Subscriptions.ParseNode("vless://abcdefab-cdef-4abc-8def-abcdefabcdef@de.example:443#Germany");
        var other = Subscriptions.ParseNode("vless://abcdefab-cdef-4abc-8def-abcdefabcdef@se.example:443#Sweden");
        var berlin = Subscriptions.ParseNode("vless://22222222-2222-4222-8222-222222222222@de.example:443#Berlin");
        var saved = new SavedSubscription { Local = true, Selected = other.Id, Nodes = [old, other] };
        var original = new Profile { Subscriptions = [saved] };
        original.Activate(saved.Id);
        void Update(Profile p, Node? connected, params Node[] nodes)
        {
            p.CurrentSubscription!.Refresh(new(nodes.ToList(), []), DateTimeOffset.UtcNow, connected);
            p.Activate(saved.Id);
        }
        var missing = store.Change(original, p => Update(p, old, other));
        failed = false;
        try { failing.Change(missing, p => Update(p, null, other, berlin)); } catch (IOException) { failed = true; }
        check(failed && missing.CurrentSubscription!.RequiresConnectionConfirmation && store.Load().CurrentSubscription!.RequiresConnectionConfirmation,
            "failed save on endpoint return leaves the running profile and disk blocked");
        var returned = store.Change(store.Load(), p => Update(p, null, other, berlin));
        check(returned.Selected == other.Id && returned.ManualConnectionNode?.Id == old.Id,
            "return after a disk reload restores the next target without moving the highlight");
        var recovered = store.RestoreBackup();
        check(recovered.CurrentSubscription!.RequiresConnectionConfirmation && recovered.ManualConnectionNode == null,
            "backup restores the unavailable endpoint without dialing the highlighted server");
        returned = store.Change(store.Load(), p => Update(p, null, other, berlin));
        check(returned.ManualConnectionNode?.Id == old.Id && returned.ManualConnectionNode.SourceId == berlin.Id, "restored backup identifies the renamed endpoint after credential rotation");

        var realityA = Subscriptions.ParseNode("vless://abcdefab-cdef-4abc-8def-abcdefabcdef@192.0.2.10:443?security=reality&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=11111111&sni=peer.example#Germany");
        var realityB = Subscriptions.ParseNode("vless://abcdefab-cdef-4abc-8def-abcdefabcdef@192.0.2.11:443?security=reality&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=22222222&sni=peer.example#Germany");
        saved = new SavedSubscription { Local = true, Nodes = [realityA, other], Selected = realityA.Id };
        original = new Profile { Subscriptions = [saved] }; original.Activate(saved.Id);
        missing = store.Change(original, p => Update(p, realityA, other));
        string? realityDigest = missing.CurrentSubscription!.MissingRealityFingerprint;
        failed = false;
        try { failing.Change(missing, p => Update(p, null, other, realityB)); } catch (IOException) { failed = true; }
        check(failed && realityDigest?.Length == 64 && missing.ManualConnectionNode == null &&
            store.Load().CurrentSubscription!.MissingRealityFingerprint == realityDigest,
            "failed REALITY return save preserves the missing target and recovery evidence on disk");
        returned = store.Change(store.Load(), p => Update(p, null, other, realityB));
        check(returned.ManualConnectionNode?.Id == realityA.Id && returned.CurrentSubscription!.MissingRealityFingerprint == null,
            "committed REALITY return clears the digest and keeps the original next connection ID");
        recovered = store.RestoreBackup();
        check(recovered.ManualConnectionNode == null && recovered.CurrentSubscription!.MissingRealityFingerprint == realityDigest,
            "backup restores the REALITY target guard and its evidence together");
        var legacy = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(recovered))!;
        legacy.CurrentSubscription!.MissingRealityFingerprint = null; legacy.Validate();
        Update(legacy, null, other, realityB);
        check(legacy.ManualConnectionNode == null, "older missing-target profiles load but cannot invent lost REALITY evidence");
        recovered.CurrentSubscription!.MissingRealityFingerprint = "invalid";
        failed = false; try { recovered.Validate(); } catch (UserError) { failed = true; }
        check(failed, "invalid REALITY recovery evidence is rejected without exposing its value");
    }
}
