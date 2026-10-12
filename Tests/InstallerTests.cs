using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kot.Core;
using Kot.Installer;
namespace Kot.Tests;

public static class InstallerTests
{
    public static void Run(Action<bool, string> check, string folder)
    {
        using var rsa = RSA.Create(2048);
        string key = rsa.ExportSubjectPublicKeyInfoPem(), root = Path.Combine(folder, "setup tests с пробелами"), target = Path.Combine(root, "installed");
        var files = new Dictionary<string, byte[]> { ["Kot.exe"] = "new app"u8.ToArray(), ["core/sing-box.exe"] = "new core"u8.ToArray(), ["ui/index.html"] = "new UI"u8.ToArray() };
        byte[] Build(string innerVersion = "0.7.0", bool damagedInner = false)
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            byte[] release = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest(innerVersion, files.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(p.Value)))), options);
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                void Add(string name, byte[] data) { using var entry = zip.CreateEntry(name).Open(); entry.Write(data); }
                Add("release.json", release); Add("release.sig", rsa.SignData(release, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                foreach (var pair in files) Add(pair.Key, damagedInner && pair.Key == "Kot.exe" ? [0] : pair.Value);
            }
            byte[] prefix = "MZsmall-installer"u8.ToArray(), archive = buffer.ToArray();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new SetupPackage.Manifest("0.7.0", prefix.Length, archive.Length, Convert.ToHexString(SHA256.HashData(prefix)), Convert.ToHexString(SHA256.HashData(archive))), options);
            byte[] signature = rsa.SignData(json, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), footer = new byte[24];
            BinaryPrimitives.WriteInt32LittleEndian(footer, json.Length); BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(4), signature.Length); Encoding.ASCII.GetBytes(SetupPackage.Magic).CopyTo(footer, 8);
            return prefix.Concat(archive).Concat(json).Concat(signature).Concat(footer).ToArray();
        }
        Directory.CreateDirectory(root);
        string setup = Path.Combine(root, "Setup.exe");
        void Reset()
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "Kot.exe"), "old app");
            File.WriteAllText(Path.Combine(target, "user-note.txt"), "keep");
            File.WriteAllBytes(setup, Build());
        }
        bool Unchanged() => File.ReadAllText(Path.Combine(target, "Kot.exe")) == "old app" && File.ReadAllText(Path.Combine(target, "user-note.txt")) == "keep" && !Directory.EnumerateDirectories(target, ".kot-update-*").Any();
        try
        {
            Reset();
            SetupExtraction.Extract(setup, target, "0.7.0", key);
            bool matches = files.All(p => File.ReadAllBytes(Path.Combine(target, p.Key)).SequenceEqual(p.Value));
            check(matches && File.ReadAllText(Path.Combine(target, "user-note.txt")) == "keep", "manual installer extracts the same authenticated update payload and preserves user files");
            SetupExtraction.Extract(setup, target, "0.7.0", key);
            check(File.ReadAllText(Path.Combine(target, "Kot.exe")) == "new app" && !Directory.EnumerateDirectories(target, ".kot-update-*").Any(), "same-version reinstall succeeds and cleans committed backups");

            Reset(); bool rolledBack = false;
            try { SetupExtraction.Extract(setup, target, "0.7.0", key, count => { if (count == 2) throw new IOException("injected disk failure"); }); }
            catch (IOException) { rolledBack = true; }
            check(rolledBack && Unchanged() && !File.Exists(Path.Combine(target, "core/sing-box.exe")), "manual install failure restores the previous app and removes newly written files");

            foreach (string kind in new[] { "prefix", "archive", "signature", "version", "inner-version", "inner-hash", "key" })
            {
                Reset(); byte[] bytes = Build(kind == "inner-version" ? "0.7.1" : "0.7.0", kind == "inner-hash");
                if (kind == "prefix") bytes[3] ^= 1;
                if (kind == "archive") bytes[24] ^= 1;
                if (kind == "signature") bytes[^25] ^= 1;
                File.WriteAllBytes(setup, bytes); bool blocked = false;
                try { SetupExtraction.Extract(setup, target, kind == "version" ? "0.7.1" : "0.7.0", kind == "key" ? ReleaseKey.Public : key); }
                catch (UserError) { blocked = true; }
                check(blocked && Unchanged(), "manual install rejects " + kind + " without modifying the previous installation");
            }
            Reset(); string external = Path.Combine(root, "external"); Directory.CreateDirectory(external); File.WriteAllText(Path.Combine(external, "sing-box.exe"), "outside");
            Directory.CreateSymbolicLink(Path.Combine(target, "core"), Path.GetFullPath(external)); bool linked = false;
            try { SetupExtraction.Extract(setup, target, "0.7.0", key); } catch (UserError) { linked = true; }
            check(linked && Unchanged() && File.ReadAllText(Path.Combine(external, "sing-box.exe")) == "outside", "manual install rejects destination junctions without writing outside its folder");
            Directory.Delete(Path.Combine(target, "core"));

            Reset(); File.WriteAllBytes(Path.Combine(target, "Kot.exe"), Build()); bool collision = false;
            try { SetupExtraction.Extract(Path.Combine(target, "Kot.exe"), target, "0.7.0", key); } catch (UserError) { collision = true; }
            check(collision && !Directory.EnumerateDirectories(target, ".kot-update-*").Any(), "installer refuses to overwrite its own source package");
        }
        finally { Directory.Delete(root, true); }
    }
}
