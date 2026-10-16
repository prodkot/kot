using Kot.Core;
using Kot.Windows;
using System.Security.Cryptography;
using System.Text;

namespace Kot.WindowTests;

static class ProfileUpgradeChecks
{
    // 0.6.1 wrote these files in the user's installed profile with these DPAPI salts.
    // Use the old on-disk format, including its removed accent field, rather than
    // generating a profile with the new serializer and reading it back unchanged.
    const string LegacyProfile = """
    {"name":"Legacy subscription","address":"https://legacy.example/subscription",
     "nodes":[],"selected":"","theme":"light","accent":"purple","mode":"smart",
     "connectionMode":"proxy","bypass":["legacy.example"],"startup":true,
     "autoConnect":true,"tray":true,"killSwitch":true,"sendHwid":false,
     "activeSubscription":"legacy-subscription","subscriptions":[
       {"id":"legacy-subscription","name":"Legacy subscription",
        "address":"https://legacy.example/subscription","nodes":[],"selected":"","favorites":[]}],
     "favorites":[]}
    """;
    const string LegacyTraffic = """
    {"version":1,"firstRecorded":"2026-10-11T00:00:00+00:00",
     "allTime":{"download":8192,"upload":1024,"connectedMilliseconds":90000,"sessions":2},
     "lastSession":null,"days":[{"date":"2026-10-11",
       "totals":{"download":8192,"upload":1024,"connectedMilliseconds":90000,"sessions":2}}]}
    """;

    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KotVPN");
        check(Store.Folder == folder, "upgrade uses the same installed profile directory as 0.6.1");
        // Refuse existing data, even when someone invokes the fixture outside CI.
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new InvalidOperationException("Profile upgrade checks require a fresh Windows test account; existing KotVPN data was left untouched.");
        bool created = !Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        var owned = new List<string>();
        void Write(string name, byte[] data)
        {
            string path = Path.Combine(folder, name);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            owned.Add(path); file.Write(data);
        }
        byte[] Protect(string text, string entropy) => ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(entropy), DataProtectionScope.CurrentUser);
        string hwid = new('C', 64);
        byte[] device = Protect(hwid, "KotVPN.device.v1");
        try
        {
            Write("profile.bin", Protect(LegacyProfile, "KotVPN.profile.v1"));
            Write("device.bin", device);
            Write("traffic-history.json", Encoding.UTF8.GetBytes(LegacyTraffic));
            var profile = Store.Load(); profile.Validate();
            check(profile.Name == "Legacy subscription" && profile.Address == "https://legacy.example/subscription"
                && profile.ActiveSubscription == "legacy-subscription" && profile.Subscriptions.Count == 1,
                "0.6.1 encrypted profile preserves its subscription and active selection");
            check(profile.Startup && profile.AutoConnect && profile.Tray && profile.KillSwitch && !profile.SendHwid
                && profile.Theme == "light" && profile.ConnectionMode == "proxy" && profile.Bypass.SequenceEqual(new[] { "legacy.example" }),
                "upgrade preserves startup, protection, privacy and UI preferences");
            check(DeviceIdentity.Get().Hwid == hwid && File.ReadAllBytes(Path.Combine(folder, "device.bin")).SequenceEqual(device),
                "upgrade reads the existing encrypted HWID without generating or rewriting it");
            var totals = new UsageTotals(8192, 1024, 90000, 2);
            var archive = UsageFile.Load(Path.Combine(folder, "traffic-history.json"));
            check(archive.AllTime == totals && archive.Days.Single().Totals == totals,
                "0.6.1 traffic history retains its bytes, session duration and sessions");
            owned.Add(Path.Combine(folder, "profile.bin.tmp"));
            owned.Add(Path.Combine(folder, "profile.previous.bin"));
            Store.Save(profile); profile = Store.Load();
            check(profile.Startup && profile.ActiveSubscription == "legacy-subscription" && !profile.SendHwid
                && DeviceIdentity.Get().Hwid == hwid && UsageFile.Load(Path.Combine(folder, "traffic-history.json")).AllTime == totals,
                "first save and reload preserve the upgraded profile, HWID and history");
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(folder, "profile.bin")), Encoding.UTF8.GetBytes("KotVPN.profile.v1"), DataProtectionScope.CurrentUser);
            try { check(Encoding.UTF8.GetString(plain).Contains("legacy-subscription"), "updated profile remains encrypted with the installed profile's DPAPI format"); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally
        {
            foreach (string path in owned) File.Delete(path);
            if (created && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
    }
}
